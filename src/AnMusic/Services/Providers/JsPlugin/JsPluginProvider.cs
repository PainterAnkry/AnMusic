using System.IO;
using System.Net.Http;
using System.Text.Json;
using AnMusic.Models;
using Jint;
using Jint.Native;

namespace AnMusic.Services.Providers.JsPlugin;

/// <summary>
/// 外部 .js 音源插件（MusicFree 兼容子集）：
/// 插件文件以 CommonJS 形式提供 module.exports，约定以下入口：
///   platform: 源唯一标识; version: 版本;
///   async search(keyword, page, type) → { data: [{ id, title, artist, album, duration(秒), cover, sourceUrl }] }
///   async getMediaSource(musicItem, quality) → { url }  quality ∈ "standard" | "high" | "super"
/// </summary>
public sealed class JsPluginProvider : IOnlineMusicProvider
{
    public static readonly string CacheDir = Services.AppPaths.PluginAudioCacheDir;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Engine _engine;
    private readonly JsValue _exports;
    private readonly JsValue _jsonParse;
    private readonly JsValue _jsonStringify;
    private readonly CoverCacheService _covers;

    /// <summary>引擎访问串行闸：Jint 引擎非线程安全，搜索/封面补全/解析播放地址等可能并发触发
    /// （后台封面任务与前台搜索同时调用会损坏引擎内部状态，表现为偶发
    /// "Unable to cast ... to Environment" 等随机错误），所有引擎入口统一排队执行。</summary>
    private readonly SemaphoreSlim _engineGate = new(1, 1);

    public string Id { get; }
    public string DisplayName { get; }
    public string Version { get; }
    public string FileName { get; }
    public bool IsOnline => true;

    public JsPluginProvider(string filePath, CoverCacheService covers)
    {
        _covers = covers;
        FileName = Path.GetFileName(filePath);

        var code = File.ReadAllText(filePath);
        _engine = new Engine(o =>
        {
            o.TimeoutInterval(TimeSpan.FromSeconds(15))
                .LimitRecursion(64)
                .Strict();
            // Jint 默认 Constraints.PromiseTimeout 只有 10 秒：插件 Promise 内部 await 宿主网络
            // I/O 的总耗时一旦超过 10 秒，等待本身就会被 Jint 拒绝并报
            // "Promise was rejected with value Timeout of 00:00:10 reached"，
            // 即使引擎只是空闲等待（脚本执行未超时）。放宽到 60 秒，
            // 真实上限由 CallAsync 的 45 秒 CancellationToken 兜底。
            o.Constraints.PromiseTimeout = TimeSpan.FromSeconds(60);
        });

        // 注册宿主 C# 能力（Task 会被 Jint 自动转为 JS Promise）
        _engine.SetValue("__axiosRequestAsync", new Func<string, string, string, string, Task<string>>(AxiosRequestAsync));
        _engine.SetValue("__btoa", new Func<string, string>(s => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s))));
        _engine.SetValue("__atob", new Func<string, string>(s => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(s))));
        _engine.SetValue("__bufferFrom", new Func<string, string, int[]>(BufferFrom));
        _engine.SetValue("__bytesToString", new Func<int[], string, string>(BytesToString));
        _engine.SetValue("__textEncode", new Func<string, int[]>(s => [.. System.Text.Encoding.UTF8.GetBytes(s)]));
        _engine.SetValue("__textDecode", new Func<int[], string>(b => System.Text.Encoding.UTF8.GetString(b.Select(i => (byte)i).ToArray())));
        // setTimeout：记录但不执行。插件常用 setTimeout(reject, 10000) 做超时保护，
        // 引擎内同步立即执行会导致请求被瞬间拒绝（"Timeout of 00:00:10 reached"）。
        // 禁用插件侧超时后由宿主 CallAsync 的 45s 兜底统一处理真实超时。
        _engine.SetValue("setTimeout", new Func<JsValue, double, JsValue>((fn, _) => JsValue.Undefined));
        _engine.SetValue("clearTimeout", new Action<JsValue>(_ => { }));
        _engine.SetValue("setInterval", new Func<JsValue, double, JsValue>((_, _) => JsValue.Undefined));
        _engine.SetValue("clearInterval", new Action<JsValue>(_ => { }));

        // CommonJS 运行环境 + MusicFree 宿主兼容层
        _engine.Execute("""
            var module = { exports: {} };
            var exports = module.exports;
            var console = { log: function(){}, warn: function(){}, error: function(){}, info: function(){} };
            var environment = { os: 'anmusic-desktop', version: '2.0.1', platform: 'windows' };
            var env = environment;
            var window = globalThis, self = globalThis, navigator = { userAgent: 'AnMusic/1.0' };
            var performance = { now: function(){ return Date.now(); } };
            // setTimeout / clearTimeout 由宿主异步实现（见 SetValue），插件的超时保护依赖它
            // atob / btoa
            var btoa = function(s){ return __btoa(s); };
            var atob = function(s){ return __atob(s); };
            // 内存版 localStorage
            var localStorage = (function(){ var s = {}; return {
                getItem: function(k){ return Object.prototype.hasOwnProperty.call(s, k) ? s[k] : null; },
                setItem: function(k, v){ s[k] = String(v); },
                removeItem: function(k){ delete s[k]; },
                clear: function(){ s = {}; }
            }; })();
            // 宿主模块表 + require（内置库加载后填充 __modules）
            var __modules = {};
            var require = function(name){
                if (__modules[name]) return __modules[name];
                throw new Error('AnMusic 宿主暂不支持模块: ' + name);
            };
            // URLSearchParams（简易实现，覆盖 get/has/append/toString/entries）
            var URLSearchParams = function(init){
                this._m = {};
                var self = this;
                function put(k, v){ (self._m[k] = self._m[k] || []).push(String(v)); }
                if (typeof init === 'string') {
                    String(init).replace(/^\?/, '').split('&').forEach(function(p){
                        if (!p) return;
                        var i = p.indexOf('=');
                        var k = i < 0 ? p : p.slice(0, i), v = i < 0 ? '' : p.slice(i + 1);
                        try { put(decodeURIComponent(k), decodeURIComponent(v.replace(/\+/g, ' '))); } catch (e) { put(k, v); }
                    });
                } else if (init && typeof init === 'object') {
                    for (var k in init) if (Object.prototype.hasOwnProperty.call(init, k)) put(k, init[k]);
                }
                this.get = function(k){ var a = this._m[k]; return a ? a[0] : null; };
                this.has = function(k){ return Object.prototype.hasOwnProperty.call(this._m, k); };
                this.append = function(k, v){ put(k, v); };
                this.entries = function(){
                    var out = [];
                    for (var k in this._m) for (var i = 0; i < this._m[k].length; i++) out.push([k, this._m[k][i]]);
                    return out;
                };
                this.toString = function(){
                    var out = [];
                    for (var k in this._m) for (var i = 0; i < this._m[k].length; i++) out.push(encodeURIComponent(k) + '=' + encodeURIComponent(this._m[k][i]));
                    return out.join('&');
                };
            };
            // URL（简易实现）
            var URL = function(url, base){
                var href = base ? String(base).replace(/\/+$/, '') + '/' + String(url).replace(/^\//, '') : String(url);
                this.href = href;
                var qi = href.indexOf('?');
                this.search = qi >= 0 ? href.slice(qi) : '';
                this.searchParams = new URLSearchParams(this.search);
            };
            // Buffer（hex/base64/utf8 常用子集）
            var Buffer = {
                from: function(input, enc){
                    enc = enc || 'utf8';
                    if (Array.isArray(input) || (input && typeof input === 'object' && typeof input.length === 'number' && typeof input !== 'string')) {
                        var arr = Array.prototype.slice.call(input);
                        return { data: arr, toString: function(e){ return __bytesToString(this.data, e || 'utf8'); } };
                    }
                    return { data: __bufferFrom(String(input), enc),
                        toString: function(e){ return __bytesToString(this.data, e || enc); } };
                },
                isBuffer: function(x){ return !!(x && x.data && typeof x.toString === 'function'); }
            };
            // TextEncoder / TextDecoder
            var TextEncoder = function(){ this.encode = function(s){ return __textEncode(String(s)); }; };
            var TextDecoder = function(){ this.decode = function(b){ return __textDecode(b); }; };
            var structuredClone = function(x){ return JSON.parse(JSON.stringify(x)); };
            """);

        // 注入宿主内置库（在临时 module 作用域执行 UMD，导出写入 __modules 后还原）
        LoadBuiltinLibraries(_engine);

        // axios 兼容层（基于 HttpClient，覆盖 get/post 最常见用法）
        _engine.Execute("""
            var __axiosRequest = function(method, url, headersJson, body){
                return __axiosRequestAsync(method, url, headersJson, body);
            };
            var axios = (function(){
                function request(method, url, config, bodyData){
                    var headers = (config && config.headers) ? config.headers : {};
                    // 支持 config.params（对象自动序列化为查询串）
                    var fullUrl = String(url);
                    if (config && config.params) {
                        var p = typeof config.params === 'string'
                            ? config.params
                            : (__modules['qs'] ? __modules['qs'].stringify(config.params) : '');
                        if (p) fullUrl = fullUrl + (fullUrl.indexOf('?') >= 0 ? '&' : '?') + p;
                    }
                    // 按 Content-Type 序列化 body（真实 axios 对 urlencoded 的对象也自动 qs 序列化）
                    var ct = String(headers['Content-Type'] || headers['content-type'] || '').toLowerCase();
                    var body;
                    if (bodyData == null) {
                        body = '';
                    } else if (typeof bodyData === 'string') {
                        body = bodyData;
                    } else if (ct.indexOf('x-www-form-urlencoded') >= 0 && __modules['qs']) {
                        body = __modules['qs'].stringify(bodyData);
                    } else {
                        body = JSON.stringify(bodyData);
                    }
                    return __axiosRequest(method, fullUrl, JSON.stringify(headers), body).then(function(raw){
                        var r = JSON.parse(raw);
                        var data = r.body;
                        // 内容探测：JSON 响应头经常不规范（text/html、text/plain），以正文首字符为准尝试解析
                        if (typeof data === 'string') {
                            var t = data.replace(/^\s+/, '');
                            if (t.length > 0 && (t[0] === '{' || t[0] === '[')) {
                                try { data = JSON.parse(t); } catch (e) {}
                            }
                        }
                        return { data: data, status: r.status, headers: r.headers, config: {} };
                    });
                }
                // axios 本身可调用：axios(config) / axios(url, config)
                function axiosInstance(configOrUrl, config){
                    var cfg = (typeof configOrUrl === 'string')
                        ? Object.assign({}, config || {}, { url: configOrUrl })
                        : (configOrUrl || {});
                    var method = (cfg.method || 'GET').toUpperCase();
                    return request(method, cfg.url, cfg, cfg.data);
                }
                axiosInstance.request = function(cfg){ return axiosInstance(cfg); };
                axiosInstance.get = function(url, config){ return request('GET', url, config, null); };
                axiosInstance.post = function(url, data, config){ return request('POST', url, config, data); };
                axiosInstance.put = function(url, data, config){ return request('PUT', url, config, data); };
                axiosInstance.delete = function(url, config){ return request('DELETE', url, config, null); };
                axiosInstance.head = function(url, config){ return request('HEAD', url, config, null); };
                axiosInstance.all = function(promises){ return Promise.all(promises); };
                axiosInstance.spread = function(cb){ return function(arr){ return cb.apply(null, arr); }; };
                axiosInstance.interceptors = { request: { use: function(){} }, response: { use: function(){} } };
                axiosInstance.defaults = { headers: {} };
                axiosInstance.create = function(defaults){
                    var inst = function(c){ return axiosInstance(c); };
                    for (var k in axiosInstance) if (Object.prototype.hasOwnProperty.call(axiosInstance, k)) inst[k] = axiosInstance[k];
                    if (defaults && defaults.headers && axiosInstance.defaults) {
                        inst.defaults = Object.assign({}, axiosInstance.defaults, { headers: Object.assign({}, axiosInstance.defaults.headers, defaults.headers) });
                    }
                    return inst;
                };
                return axiosInstance;
            })();
            // MusicFree 协议中 axios 通过 require 提供；TS 编译插件调用 axios_1.default(...)
            axios.default = axios;
            __modules['axios'] = axios;
            """);

        _engine.Execute(code);

        _exports = _engine.GetValue("module").AsObject().Get("exports");
        Id = _exports.Get("platform").IsString() ? _exports.Get("platform").AsString()
            : Path.GetFileNameWithoutExtension(filePath);
        Version = _exports.Get("version").IsString() ? _exports.Get("version").AsString() : "";
        DisplayName = Id;

        _jsonParse = _engine.Evaluate("(s)=>JSON.parse(s)");
        _jsonStringify = _engine.Evaluate("(x)=>JSON.stringify(x)");
    }

    /// <summary>执行宿主内置库（crypto-js / dayjs / big-integer / jsencrypt），导出注册进 __modules 供 require 使用。</summary>
    private static void LoadBuiltinLibraries(Engine engine)
    {
        var asm = typeof(JsPluginProvider).Assembly;
        var libs = new[]
        {
            ("crypto-js", "AnMusic.Assets.Plugins.crypto-js.min.js"),
            ("dayjs", "AnMusic.Assets.Plugins.dayjs.min.js"),
            ("big-integer", "AnMusic.Assets.Plugins.big-integer.min.js"),
            ("jsencrypt", "AnMusic.Assets.Plugins.jsencrypt.min.js"),
            ("he", "AnMusic.Assets.Plugins.he.min.js"),
        };

        foreach (var (name, res) in libs)
        {
            string code;
            try
            {
                using var stream = asm.GetManifestResourceStream(res);
                if (stream is null) continue;
                using var reader = new StreamReader(stream);
                code = reader.ReadToEnd();
            }
            catch
            {
                continue;
            }

            // 临时替换 module/exports 执行 UMD 库，导出写入 __modules，最后还原供插件代码使用
            var script = """
                (function(){
                    var __libModule = { exports: {} };
                    var __savedModule = module, __savedExports = exports;
                    module = __libModule; exports = __libModule.exports;
                    try {
                        __LIB_CODE__
                        __modules['__LIB_NAME__'] = __libModule.exports;
                    } catch (e) {
                        __modules['__LIB_NAME__'] = null;
                    }
                    module = __savedModule; exports = __savedExports;
                })();
                """.Replace("__LIB_NAME__", name).Replace("__LIB_CODE__", code);
            engine.Execute(script);
        }

        // 常用库同时挂为全局（部分插件直接引用全局 CryptoJS/dayjs）
        engine.Execute("""
            if (__modules['crypto-js']) { try { globalThis.CryptoJS = __modules['crypto-js']; } catch (e) {} }
            if (__modules['dayjs']) { try { globalThis.dayjs = __modules['dayjs']; } catch (e) {} }
            if (__modules['big-integer']) { try { globalThis.bigInt = __modules['big-integer']; } catch (e) {} }
            if (__modules['jsencrypt']) { try { globalThis.JSEncrypt = __modules['jsencrypt']; } catch (e) {} }
            // TS 编译的插件会调用 xx_1.default(...)，给所有内置模块补 default 自引用（与真实 axios 包行为一致）
            ['crypto-js', 'dayjs', 'big-integer', 'jsencrypt', 'he', 'axios'].forEach(function(n){
                var m = __modules[n];
                if (m && typeof m === 'object' && !m.default) { try { m.default = m; } catch (e) {} }
            });
            """);

        // qs（querystring 序列化/解析，纯 JS 实现）与 cheerio 兼容 stub（HTML 解析返回空结果）
        engine.Execute("""
            __modules['qs'] = (function(){
                function stringify(obj){
                    var parts = [];
                    for (var k in obj) {
                        if (!Object.prototype.hasOwnProperty.call(obj, k)) continue;
                        var v = obj[k];
                        if (v == null) { parts.push(encodeURIComponent(k) + '='); continue; }
                        if (Array.isArray(v)) {
                            for (var i = 0; i < v.length; i++) parts.push(encodeURIComponent(k) + '=' + encodeURIComponent(v[i]));
                        } else {
                            parts.push(encodeURIComponent(k) + '=' + encodeURIComponent(v));
                        }
                    }
                    return parts.join('&');
                }
                function parse(str){
                    var out = {};
                    String(str).replace(/^\?/, '').split('&').forEach(function(p){
                        if (!p) return;
                        var i = p.indexOf('=');
                        var k = i < 0 ? p : p.slice(0, i);
                        var v = i < 0 ? '' : p.slice(i + 1);
                        try { out[decodeURIComponent(k)] = decodeURIComponent(v.replace(/\+/g, ' ')); } catch (e) { out[k] = v; }
                    });
                    return out;
                }
                return { stringify: stringify, parse: parse };
            })();
            __modules['querystring'] = __modules['qs'];
            // cheerio stub：返回空结果的兼容实现（插件可加载，HTML 抓取类功能受限）
            // 注意：元素用普通对象而非函数对象——函数的 length 属性只读，严格模式下赋值会抛 TypeError
            __modules['cheerio'] = (function(){
                function makeEl(html){
                    var el = {};
                    el.length = 0;
                    el.text = function(){ return ''; };
                    el.html = function(){ return html || ''; };
                    el.attr = function(){ return el; };
                    el.find = function(){ return makeEl(''); };
                    el.each = function(){ return el; };
                    el.map = function(){ return { get: function(){ return []; } }; };
                    el.get = function(){ return []; };
                    el.toArray = function(){ return []; };
                    el.first = function(){ return el; };
                    el.eq = function(){ return el; };
                    el.parent = function(){ return el; };
                    el.children = function(){ return el; };
                    el.val = function(){ return ''; };
                    // 支持 for...of / 展开运算符（空结果迭代器）
                    el[Symbol.iterator] = function(){
                        return { next: function(){ return { done: true, value: undefined }; } };
                    };
                    return el;
                }
                return {
                    load: function(html){
                        var $ = function(){ return makeEl(html || ''); };
                        $.html = function(){ return html || ''; };
                        $.text = function(){ return ''; };
                        return $;
                    }
                };
            })();
            """);
    }

    /// <summary>axios 底层 HTTP（返回 JSON 字符串: { body, status, contentType, headers }）。</summary>
    private static async Task<string> AxiosRequestAsync(string method, string url, string headersJson, string body)
    {
        var logPath = Path.Combine(
            Services.AppPaths.PluginsDir, "http-debug.log");
        try
        {
            WriteHttpLog(logPath, $"[{DateTime.Now:HH:mm:ss}] >> {method} {url} bodyLen={body.Length} body={body[..Math.Min(200, body.Length)]}");
            using var req = new HttpRequestMessage(new HttpMethod(method), url);

            string? reqContentType = null;
            if (!string.IsNullOrEmpty(headersJson))
            {
                using var h = JsonDocument.Parse(headersJson);
                foreach (var p in h.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.String) continue;
                    var name = p.Name;
                    if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                        reqContentType = p.Value.GetString(); // content-type 属于 HttpContent，单独取出
                    else
                        req.Headers.TryAddWithoutValidation(name, p.Value.GetString());
                }
            }
            if (!string.IsNullOrEmpty(body) && method is not "GET" and not "HEAD")
                req.Content = new StringContent(body, System.Text.Encoding.UTF8, reqContentType ?? "application/json");

            using var resp = await Http.SendAsync(req);
            // 手动解码：响应 charset 可能非法（如网易 HTML 页），ReadAsStringAsync 会整体抛异常
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            string text;
            try
            {
                var charset = resp.Content.Headers.ContentType?.CharSet;
                var enc = string.IsNullOrEmpty(charset) ? System.Text.Encoding.UTF8 : System.Text.Encoding.GetEncoding(charset);
                text = enc.GetString(bytes);
            }
            catch (ArgumentException)
            {
                text = System.Text.Encoding.UTF8.GetString(bytes); // 未知 charset 回退 UTF-8
            }
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "";

            var preview = text.Length > 300 ? text[..300] : text;
            WriteHttpLog(logPath, $"[{DateTime.Now:HH:mm:ss}] << {(int)resp.StatusCode} {contentType}{Environment.NewLine}  {preview.Replace("\n", " ")}");

            return JsonSerializer.Serialize(new { body = text, status = (int)resp.StatusCode, contentType, headers = new { } });
        }
        catch (Exception ex)
        {
            WriteHttpLog(logPath, $"[{DateTime.Now:HH:mm:ss}] !! 异常: {ex.Message}");
            return JsonSerializer.Serialize(new { body = "", status = 0, contentType = "", error = ex.Message, headers = new { } });
        }
    }

    /// <summary>写 HTTP 调试日志（超过 200KB 自动清理）。</summary>
    private static void WriteHttpLog(string logPath, string line)
    {
        try
        {
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 200_000) File.Delete(logPath);
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch { /* 日志失败不影响请求 */ }
    }

    /// <summary>Buffer.from：字符串按 hex/base64/utf8 解码为字节数组。</summary>
    private static int[] BufferFrom(string input, string enc) => enc.ToLowerInvariant() switch
    {
        "hex" => Enumerable.Range(0, input.Length / 2).Select(i => (int)Convert.ToByte(input.Substring(i * 2, 2), 16)).ToArray(),
        "base64" => [.. Convert.FromBase64String(input).Select(b => (int)b)],
        _ => [.. System.Text.Encoding.UTF8.GetBytes(input).Select(b => (int)b)],
    };

    /// <summary>字节数组按 hex/base64/utf8 编码为字符串。</summary>
    private static string BytesToString(int[] bytes, string enc)
    {
        var arr = bytes.Select(i => (byte)i).ToArray();
        return enc.ToLowerInvariant() switch
        {
            "hex" => Convert.ToHexStringLower(arr),
            "base64" => Convert.ToBase64String(arr),
            _ => System.Text.Encoding.UTF8.GetString(arr),
        };
    }

    /// <summary>调用插件异步函数（引擎入口，全程持锁串行）；未定义的函数返回 Undefined。</summary>
    private async Task<JsValue> CallAsync(string fn, params JsValue[] args)
    {
        await _engineGate.WaitAsync();
        try
        {
            var f = _exports.Get(fn);
            if (f.IsUndefined() || !f.IsCallable()) return JsValue.Undefined;
            JsValue res;
            try
            {
                res = _engine.Invoke(f, args);
            }
            catch (Jint.Runtime.JavaScriptException ex)
            {
                throw new InvalidOperationException($"插件「{DisplayName}」{fn} 执行错误: {FormatJsError(ex)}");
            }
            // 插件若返回 Promise 则异步等待落定；永不落定时超时兜底（getMediaSource 链路含多次网络请求，放宽到 45s；
            // 引擎 Constraints.PromiseTimeout 已在构造时放宽到 60s，不会早于此处的 45s 截断）
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                return await res.UnwrapIfPromiseAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"插件「{DisplayName}」调用 {fn} 超时");
            }
            catch (Jint.Runtime.PromiseRejectedException ex)
            {
                // Promise 被拒绝：提取 rejection value 的 stack（定位插件内部出错位置）
                var stack = "";
                try
                {
                    if (ex.RejectedValue is JsValue jv && jv.IsObject())
                    {
                        var s = jv.AsObject().Get("stack");
                        if (s.IsString()) stack = s.AsString();
                    }
                }
                catch { /* 堆栈提取失败不影响错误上抛 */ }
                var detail = stack == "" ? ex.Message
                    : $"{ex.Message}\n{string.Join("\n", stack.Split('\n').Take(5))}";
                throw new InvalidOperationException($"插件「{DisplayName}」{fn} 错误: {detail}");
            }
            catch (Jint.Runtime.JavaScriptException ex)
            {
                throw new InvalidOperationException($"插件「{DisplayName}」{fn} 错误: {FormatJsError(ex)}");
            }
        }
        finally
        {
            _engineGate.Release();
        }
    }

    /// <summary>提取 JS 异常信息与堆栈（定位插件内部错误行）。</summary>
    private static string FormatJsError(Jint.Runtime.JavaScriptException ex)
    {
        var stack = "";
        try
        {
            var s = ex.Error?.Get("stack");
            if (s is JsValue sv && sv.IsString()) stack = sv.AsString();
        }
        catch { /* 堆栈提取失败不影响错误上抛 */ }
        return stack == "" ? ex.Message
            : $"{ex.Message}\n{string.Join("\n", stack.Split('\n').Take(4))}";
    }

    private async Task<string> ToJsonAsync(JsValue value)
    {
        await _engineGate.WaitAsync();
        try
        {
            return _engine.Invoke(_jsonStringify, value).AsString();
        }
        finally
        {
            _engineGate.Release();
        }
    }

    /// <summary>关键词搜索（调用插件 search 第 1 页）。</summary>
    public async Task<IReadOnlyList<Track>> SearchAsync(string keyword, CancellationToken ct = default)
        => await SearchPageAsync(keyword, 1, ct);

    /// <summary>分页搜索（page 从 1 开始，每页条数由插件决定；供搜索"加载更多"逐页拉取）。</summary>
    public async Task<IReadOnlyList<Track>> SearchPageAsync(string keyword, int page, CancellationToken ct = default)
    {
        var res = await CallAsync("search", keyword, Math.Max(1, page), "music");
        if (res.IsUndefined() || res.IsNull()) return [];
        var json = await ToJsonAsync(res);
        var tracks = ParseTracks(json);
        LoadCoversInBackground(tracks);
        return tracks;
    }

    /// <summary>获取插件提供的排行榜列表（调用 getTopLists，MusicFree 协议）。</summary>
    public async Task<IReadOnlyList<(string Id, string Title, string? Cover)>> GetTopListsAsync(CancellationToken ct = default)
    {
        var res = await CallAsync("getTopLists");
        if (res.IsUndefined() || res.IsNull()) return [];
        var json = await ToJsonAsync(res);
        var result = new List<(string Id, string Title, string? Cover)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 返回格式: [ { title, data: [ { id, title, coverImg } ] } ] 或 { data: [...] }
            var groups = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var gd) && gd.ValueKind == JsonValueKind.Array ? gd
                : default;
            if (groups.ValueKind != JsonValueKind.Array) return result;

            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object ||
                    !group.TryGetProperty("data", out var items) || items.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var it in items.EnumerateArray())
                {
                    string Str(string n) => it.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String
                        ? e.GetString() ?? "" : "";
                    var id = Str("id");
                    var title = Str("title");
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) continue;
                    var cover = Str("coverImg") is { Length: > 0 } c ? c : (Str("cover") is { Length: > 0 } c2 ? c2 : null);
                    result.Add((id, title, cover));
                }
            }
        }
        catch (JsonException) { }
        return result;
    }

    /// <summary>按分享链接导入歌单（MusicFree importMusicSheet：支持网易云/QQ音乐等平台分享页，取决于插件实现）。
    /// 返回 (歌单名, 曲目列表)；插件不支持（未实现该函数）或解析失败返回 null。</summary>
    public async Task<(string Name, IReadOnlyList<Track> Tracks)?> ImportMusicSheetAsync(string url, CancellationToken ct = default)
    {
        var res = await CallAsync("importMusicSheet", url);
        if (res.IsUndefined() || res.IsNull()) return null;
        var json = await ToJsonAsync(res);
        if (string.IsNullOrEmpty(json) || json == "null") return null;

        // 返回形如 { name/name, cover, musicList: [...] }、{ data: [...] } 或直接数组
        var name = "";
        var tracks = ParseTracks(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    name = n.GetString() ?? "";
                if (tracks.Count == 0 && doc.RootElement.TryGetProperty("musicList", out var ml) &&
                    ml.ValueKind == JsonValueKind.Array)
                    tracks = ParseTracks(ml.GetRawText());
            }
        }
        catch (JsonException) { }
        if (tracks.Count == 0) return null;
        LoadCoversInBackground(tracks);
        return (name, tracks);
    }

    /// <summary>获取指定榜单的歌曲详情（调用 getTopListDetail(id)，MusicFree 协议）。</summary>
    public async Task<IReadOnlyList<Track>> GetTopListDetailAsync(string boardId, CancellationToken ct = default)
    {
        var res = await CallAsync("getTopListDetail", boardId);
        if (res.IsUndefined() || res.IsNull()) return [];
        var json = await ToJsonAsync(res);

        // 详情返回 { title, musicList: [...] } 或直接数组
        var tracks = ParseTracks(json);
        if (tracks.Count == 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("musicList", out var ml) && ml.ValueKind == JsonValueKind.Array)
                    tracks = ParseTracks(ml.GetRawText());
            }
            catch (JsonException) { }
        }
        LoadCoversInBackground(tracks);
        return tracks;
    }

    /// <summary>解析插件返回的曲目 JSON（数组或 { data: [...] }）。</summary>
    private List<Track> ParseTracks(string json)
    {
        var list = new List<Track>();
        if (string.IsNullOrEmpty(json) || json == "null") return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Array) arr = root;
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
                arr = d;
            else
                return list;

            foreach (var s in arr.EnumerateArray())
            {
                string Str(string name) => s.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString() ?? "" : "";
                var id = s.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
                if (string.IsNullOrEmpty(id) || id == "null") continue;

                var duration = 0.0;
                if (s.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number)
                    duration = du.GetDouble();

                list.Add(new Track
                {
                    Id = id,
                    Title = Str("title") is { Length: > 0 } t ? t : "未知标题",
                    Artist = Str("artist") is { Length: > 0 } a ? a : "未知艺术家",
                    Album = Str("album"),
                    Duration = TimeSpan.FromSeconds(duration),
                    FilePath = "",
                    ProviderId = Id,
                    SourceUrl = Str("sourceUrl"),
                    CoverUrl = Str("artwork") is { Length: > 0 } aw ? aw : Str("cover"), // MusicFree 协议字段为 artwork，部分插件用 cover
                    PluginData = s.GetRawText() // 保留插件原始 musicItem（含 songmid 等自定义字段）
                });
            }
        }
        catch (JsonException)
        {
            // 插件返回格式异常时按空结果处理
        }
        return list;
    }

    /// <summary>将曲目缓冲为本地可播放文件（调用插件 getMediaSource，按音质降级重试）。</summary>
    public async Task<string> ResolveToLocalAsync(Track track, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(track.FilePath) && File.Exists(track.FilePath))
            return track.FilePath;

        string? url = null;
        Exception? lastError = null;
        foreach (var quality in new[] { "standard", "high", "super" })
        {
            ct.ThrowIfCancellationRequested();

            // 插件侧偶发 10s 超时（网络/接口慢）：同音质自动重试一次
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var res = await CallAsync("getMediaSource", await BuildMusicItemAsync(track), quality);
                    if (res.IsUndefined() || res.IsNull()) break;

                    var json = await ToJsonAsync(res);
                    WriteHttpLog(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnMusic", "plugins", "http-debug.log"),
                        $"[{DateTime.Now:HH:mm:ss}] mediaSource {track.Id} quality={quality} resp={json[..Math.Min(180, json.Length)]}");
                    if (string.IsNullOrEmpty(json) || json == "null") break;
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                            doc.RootElement.TryGetProperty("url", out var u) &&
                            u.ValueKind == JsonValueKind.String &&
                            IsValidMediaUrl(u.GetString()))
                        {
                            url = u.GetString();
                        }
                    }
                    catch (JsonException) { }
                    break;
                }
                catch (Exception ex) when (attempt == 0)
                {
                    lastError = ex;
                    // 超时/接口错误延迟后重试一次
                    await Task.Delay(300, ct);
                }
            }

            if (url is not null) break;
        }

        if (string.IsNullOrEmpty(url))
        {
            if (lastError is not null && lastError.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"插件「{DisplayName}」解析播放地址超时（网络较慢），请稍后重试", lastError);
            throw lastError ?? new InvalidOperationException($"插件「{DisplayName}」未返回可用播放地址");
        }

        url = NormalizeMediaUrl(url);

        Directory.CreateDirectory(CacheDir);
        var ext = GetUrlExtension(url);
        var destPath = Path.Combine(CacheDir, $"{Id}_{SanitizeId(track.Id)}{ext}");
        if (!File.Exists(destPath) || new FileInfo(destPath).Length == 0)
        {
            // 并发防护：同一首歌可能被播放/下载/预加载同时触发解析
            var gate = _resolveGates.GetOrAdd(destPath, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (!File.Exists(destPath) || new FileInfo(destPath).Length == 0)
                {
                    // tmp 用唯一名，避免上次异常残留的文件/句柄冲突
                    var tmp = destPath + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                    try
                    {
                        await using var remote = await Http.GetStreamAsync(url, ct);
                        await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.Read);
                        await remote.CopyToAsync(fs, ct);
                    }
                    catch
                    {
                        try { File.Delete(tmp); } catch { }
                        throw;
                    }

                    // 目标可能被播放器/杀毒短暂占用：Delete+Move 重试，最终直接以 tmp 播放
                    var moved = false;
                    for (var attempt = 0; attempt < 3 && !moved; attempt++)
                    {
                        try
                        {
                            if (File.Exists(destPath))
                                File.Delete(destPath);
                            File.Move(tmp, destPath);
                            moved = true;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 2)
                        {
                            await Task.Delay(150, ct);
                        }
                    }
                    if (!moved)
                        destPath = tmp; // 放弃重命名，tmp 内容完整可直接播放
                }
            }
            finally
            {
                gate.Release();
            }
        }

        track.FilePath = destPath;
        return destPath;
    }

    /// <summary>按目标路径的解析并发锁。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _resolveGates = new();

    /// <summary>规范插件返回的播放地址（协议相对补全、非法字符转义）。</summary>
    private static string NormalizeMediaUrl(string url)
    {
        url = url.Trim();
        if (url.StartsWith("//")) url = "https:" + url;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"插件返回了无效的播放地址: {url[..Math.Min(80, url.Length)]}");
        return url;
    }

    /// <summary>插件返回的播放地址是否有效（过滤 null / "None" / 非 http 等无效值）。</summary>
    private static bool IsValidMediaUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var t = url.Trim();
        if (t is "None" or "null" or "undefined" or "N/A") return false;
        return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("//");
    }

    /// <summary>插件音源音质由 getMediaSource 决定，仅提供标准档（下载走 ResolveToLocal）。</summary>
    public Task<IReadOnlyList<AudioQuality>> GetAvailableQualitiesAsync(Track track, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AudioQuality>>([AudioQuality.Standard]);

    public Task<string> DownloadAsync(Track track, AudioQuality quality, CancellationToken ct = default)
        => ResolveToLocalAsync(track, ct);

    /// <summary>把 Track 还原为插件约定的 musicItem 对象（引擎入口，持锁执行）。</summary>
    private async Task<JsValue> BuildMusicItemAsync(Track track)
    {
        await _engineGate.WaitAsync();
        try
        {
            // 优先回传插件原始 musicItem（含 songmid / copyrightId 等插件自定义字段）
            if (!string.IsNullOrEmpty(track.PluginData))
            {
                try
                {
                    return _engine.Invoke(_jsonParse, track.PluginData);
                }
                catch
                {
                    // 原始 JSON 解析失败时退回构造对象
                }
            }

            var json = JsonSerializer.Serialize(new
            {
                id = track.Id,
                title = track.Title,
                artist = track.Artist,
                album = track.Album,
                duration = (long)track.Duration.TotalSeconds,
                cover = track.CoverUrl,
                sourceUrl = track.SourceUrl
            }, CamelCase);
            return _engine.Invoke(_jsonParse, json);
        }
        finally
        {
            _engineGate.Release();
        }
    }

    /// <summary>后台下载封面缩略图到本地缓存，完成后刷新 CoverKey 触发 UI 更新（供外部兜底数据调用）。</summary>
    public void PreloadCovers(IEnumerable<Track> tracks) => LoadCoversInBackground(tracks);

    /// <summary>后台下载封面缩略图到本地缓存，完成后刷新 CoverKey 触发 UI 更新。</summary>
    private void LoadCoversInBackground(IEnumerable<Track> tracks)
    {
        _ = Task.Run(async () =>
        {
            foreach (var t in tracks)
            {
                try
                {
                    // 封面缺失时尝试 getMusicInfo 补全（MusicFree 协议，返回含 artwork 的完整信息）
                    // 注意：网易云 song/detail 类接口对高频无登录请求有“操作频繁(405)”风控，
                    // 批量补全加小间隔节流，降低触发限频导致后续导入/搜索失败的概率
                    if (string.IsNullOrEmpty(t.CoverUrl) && Supports("getMusicInfo"))
                    {
                        await Task.Delay(80);
                    }
                    if (string.IsNullOrEmpty(t.CoverUrl) && Supports("getMusicInfo"))
                    {
                        try
                        {
                            var info = await CallAsync("getMusicInfo", await BuildMusicItemAsync(t));
                            if (!info.IsUndefined() && !info.IsNull())
                            {
                                var infoJson = await ToJsonAsync(info);
                                using var doc = System.Text.Json.JsonDocument.Parse(infoJson);
                                if (doc.RootElement.TryGetProperty("artwork", out var aw) && aw.ValueKind == System.Text.Json.JsonValueKind.String)
                                    t.CoverUrl = aw.GetString();
                                else if (doc.RootElement.TryGetProperty("cover", out var cv) && cv.ValueKind == System.Text.Json.JsonValueKind.String)
                                    t.CoverUrl = cv.GetString();
                            }
                        }
                        catch { /* getMusicInfo 失败不影响其他封面 */ }
                    }

                    if (string.IsNullOrEmpty(t.CoverUrl)) continue;
                    var local = await _covers.GetOrCreateFromUrlAsync(t.CoverUrl);
                    if (local is not null) t.CoverKey = local;
                }
                catch { /* 单曲目封面失败不中断 */ }
            }
        });
    }

    /// <summary>插件是否实现了指定函数（引擎入口，持锁执行；仅后台封面线程调用）。</summary>
    private bool Supports(string func)
    {
        _engineGate.Wait();
        try
        {
            try { return !_exports.Get(func).IsUndefined(); }
            catch { return false; }
        }
        finally
        {
            _engineGate.Release();
        }
    }

    private static string GetUrlExtension(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var ext = Path.GetExtension(path);
            return string.IsNullOrEmpty(ext) ? ".mp3" : ext;
        }
        catch
        {
            return ".mp3";
        }
    }

    private static string SanitizeId(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            id = id.Replace(c, '_');
        return id;
    }
}
