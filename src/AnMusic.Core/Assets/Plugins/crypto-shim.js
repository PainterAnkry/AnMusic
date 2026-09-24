/*
 * AnMusic 宿主内置模块：node「crypto」的最小兼容实现（基于内置的 crypto-js）。
 *
 * 为什么需要它：
 *   不少 MusicFree 音源插件（例如网易云）在取「歌曲详情 / 播放地址 / 封面」这些需要签名的
 *   接口时会 require('crypto')（node 内置模块）。宿主以前没有这个模块，插件一 require 就抛错，
 *   表现是"搜索能出结果、但封面和详情大量缺失"。
 *
 * 覆盖范围（音源插件实际用到的部分）：
 *   createHash('md5'|'sha1'|'sha256'|'sha512')  → update()/digest([enc])
 *   createHmac(算法, key)                        → update()/digest([enc])
 *   randomBytes(n)                               → 类 Buffer（toString('hex'|'base64')）
 *   createCipheriv / createDecipheriv            → AES-ECB / AES-CBC，update()/final()
 *
 * 实现要点（踩过的坑）：
 *   crypto-js 在 node 分支里自己会 `require('crypto')` 取安全随机数，而插件加载本模块时
 *   crypto-js 可能"正在加载中"。所以本模块**初始化阶段绝不能 require crypto-js**，
 *   只能在真正调用哈希/加密时惰性取（那时 crypto-js 必然已加载完），
 *   randomBytes 则完全不依赖 crypto-js —— 否则会出现 require 死循环。
 *
 * 限制：加解密按"一次性 update"实现（音源插件都是拿到完整报文再加密），不做流式分块。
 */
(function () {
    var cachedLib = null;

    // 惰性拿 crypto-js：优先复用宿主已加载的实例（插件 bootstrap 的 __modules / 全局 CryptoJS）
    function lib() {
        if (cachedLib) return cachedLib;
        if (typeof __modules !== 'undefined' && __modules && __modules['crypto-js']) {
            cachedLib = __modules['crypto-js'];
        } else if (typeof globalThis !== 'undefined' && globalThis.CryptoJS) {
            cachedLib = globalThis.CryptoJS;
        } else {
            cachedLib = require('crypto-js');
        }
        return cachedLib;
    }

    function toWA(data) {
        var C = lib();
        if (data === null || data === undefined) return C.lib.WordArray.create();
        if (typeof data === 'string') return C.enc.Utf8.parse(data);
        if (data.words !== undefined) return data;                    // 已经是 WordArray
        if (typeof data === 'number') return C.lib.WordArray.create([data], 4);
        if (data.byteLength !== undefined || data.length !== undefined) {
            return bytesToWA(data);
        }
        return C.enc.Utf8.parse(String(data));
    }

    function bytesToWA(bytes) {
        var C = lib();
        var n = bytes.length !== undefined ? bytes.length : bytes.byteLength;
        var wa = C.lib.WordArray.create([], n);
        for (var i = 0; i < n; i++) {
            wa.words[i >>> 2] |= (bytes[i] & 0xff) << (24 - (i % 4) * 8);
        }
        wa.sigBytes = n;
        return wa;
    }

    function encOf(name) {
        var C = lib();
        switch (String(name || 'hex').toLowerCase()) {
            case 'base64': return C.enc.Base64;
            case 'latin1':
            case 'binary': return C.enc.Latin1;
            case 'utf8':
            case 'utf-8': return C.enc.Utf8;
            default: return C.enc.Hex;
        }
    }

    function algoOf(name) {
        return String(name).toUpperCase().replace(/[^A-Z0-9]/g, '');
    }

    // 类 Buffer：插件常直接 toString('hex'|'base64')，crypto-js 取随机数时会用 readInt32LE
    function bufferLike(wa) {
        var bytes = [];
        for (var i = 0; i < wa.sigBytes; i++) {
            bytes.push((wa.words[i >>> 2] >>> (24 - (i % 4) * 8)) & 0xff);
        }
        return {
            type: 'Buffer',
            words: wa.words,
            length: bytes.length,
            toString: function (e) {
                if (e === undefined) return wa.toString(lib().enc.Utf8);
                return wa.toString(encOf(e));
            },
            readInt32LE: function (offset) {
                var o = offset || 0;
                var v = (bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16) | (bytes[o + 3] << 24)) | 0;
                return v;
            },
            toJSON: function () { return { type: 'Buffer', data: wa.toString(encOf('hex')) }; }
        };
    }

    function out(wa, encoding) {
        return encoding === undefined ? bufferLike(wa) : wa.toString(encOf(encoding));
    }

    function hasher(name) {
        var C = lib();
        var algo = C.algo[algoOf(name)];
        if (!algo) throw new Error('AnMusic crypto: 不支持的摘要算法 ' + name);
        var h = algo.create();
        return {
            update: function (data, inputEncoding) {
                h.update(inputEncoding && typeof data === 'string' ? encOf(inputEncoding).parse(data) : toWA(data));
                return this;
            },
            digest: function (encoding) { return out(h.finalize(), encoding); }
        };
    }

    function hmac(name, key) {
        var C = lib();
        // crypto-js 的 HMAC 是"通用算法 + 指定哈希"：HMAC.create(SHA1, key)
        var hashAlgo = C.algo[algoOf(name)];
        if (!hashAlgo || !C.algo.HMAC) throw new Error('AnMusic crypto: 不支持的 HMAC 算法 ' + name);
        var h = C.algo.HMAC.create(hashAlgo, toWA(key));
        return {
            update: function (data, inputEncoding) {
                h.update(inputEncoding && typeof data === 'string' ? encOf(inputEncoding).parse(data) : toWA(data));
                return this;
            },
            digest: function (encoding) { return out(h.finalize(), encoding); }
        };
    }

    // 不依赖 crypto-js（crypto-js 初始化时会调到这里）
    function randomBytes(n) {
        var count = n || 0;
        var C = (typeof __modules !== 'undefined' && __modules && __modules['crypto-js'])
            || (typeof globalThis !== 'undefined' && globalThis.CryptoJS) || null;
        var bytes = [];
        for (var i = 0; i < count; i++) bytes.push(Math.floor(Math.random() * 256));

        if (!C) return plainBuffer(bytes);
        var wa = C.lib.WordArray.create([], count);
        for (var j = 0; j < count; j++) wa.words[j >>> 2] |= bytes[j] << (24 - (j % 4) * 8);
        wa.sigBytes = count;
        return bufferLike(wa);
    }

    // 没有 crypto-js 时的极简 Buffer 壳（crypto-js 初始化期间用得到）
    function plainBuffer(bytes) {
        return {
            type: 'Buffer',
            length: bytes.length,
            toString: function (e) {
                var enc = String(e || 'utf8').toLowerCase();
                if (enc === 'hex') {
                    var hex = '';
                    for (var i = 0; i < bytes.length; i++) {
                        hex += ('0' + bytes[i].toString(16)).slice(-2);
                    }
                    return hex;
                }
                if (enc === 'base64') {
                    var chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
                    var out = '', i2 = 0;
                    while (i2 < bytes.length) {
                        var b1 = bytes[i2++], b2 = bytes[i2++], b3 = bytes[i2++];
                        out += chars[b1 >> 2];
                        out += chars[((b1 & 3) << 4) | (b2 === undefined ? 0 : b2 >> 4)];
                        out += b2 === undefined ? '=' : chars[((b2 & 15) << 2) | (b3 === undefined ? 0 : b3 >> 6)];
                        out += b3 === undefined ? '=' : chars[b3 & 63];
                    }
                    return out;
                }
                var s = '';
                for (var k = 0; k < bytes.length; k++) s += String.fromCharCode(bytes[k]);
                return s;
            },
            readInt32LE: function (offset) {
                var o = offset || 0;
                return (bytes[o] | (bytes[o + 1] << 8) | (bytes[o + 2] << 16) | (bytes[o + 3] << 24)) | 0;
            }
        };
    }

    function cipher(name, key, iv, encrypt) {
        var C = lib();
        var algo = algoOf(name);
        if (algo.indexOf('AES') !== 0) throw new Error('AnMusic crypto: 暂只支持 AES，收到 ' + name);

        var mode = algo.indexOf('ECB') >= 0 ? C.mode.ECB : C.mode.CBC;
        var padding = algo.indexOf('NOPADDING') >= 0 ? C.pad.NoPadding : C.pad.Pkcs7;
        var k = toWA(key), v = toWA(iv);

        return {
            update: function (data, inputEncoding, outputEncoding) {
                var input = inputEncoding && typeof data === 'string' ? encOf(inputEncoding).parse(data) : toWA(data);
                var res = encrypt
                    ? C.AES.encrypt(input, k, { iv: v, mode: mode, padding: padding })
                    : C.AES.decrypt({ ciphertext: input }, k, { iv: v, mode: mode, padding: padding });
                var wa = encrypt ? res.ciphertext : res;
                return outputEncoding ? wa.toString(encOf(outputEncoding)) : bufferLike(wa);
            },
            final: function (outputEncoding) {
                var empty = lib().lib.WordArray.create();
                return outputEncoding ? '' : bufferLike(empty);
            }
        };
    }

    module.exports = {
        createHash: function (name) { return hasher(name); },
        createHmac: function (name, key) { return hmac(name, key); },
        randomBytes: randomBytes,
        createCipheriv: function (name, key, iv) { return cipher(name, key, iv, true); },
        createDecipheriv: function (name, key, iv) { return cipher(name, key, iv, false); },
        // 少数插件只是探测这些常量是否存在
        constants: { RSA_PKCS1_PADDING: 1 },
    };
})();
