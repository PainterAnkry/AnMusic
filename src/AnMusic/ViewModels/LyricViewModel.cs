using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using AnMusic.Models;
using AnMusic.Services.Audio;
using AnMusic.Services.Lyrics;
using AnMusic.Services.Providers;
using AnMusic.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.ViewModels;

/// <summary>
/// 歌词 ViewModel：加载歌词（本地 → 可选在线 LRCLIB）、同步当前行、点击行跳转。
/// </summary>
public partial class LyricViewModel : ObservableObject
{
    private readonly IAudioEngine _engine;
    private readonly LocalLyricProvider _lyricProvider;
    private readonly IOnlineLyricProvider? _onlineLyricProvider;
    private readonly ILrcParser _parser;
    private readonly UserSettingsService _settingsService;
    private LyricDocument? _document;

    public ObservableCollection<LyricLine> Lines { get; } = [];

    [ObservableProperty]
    private int _currentIndex = -1;

    [ObservableProperty]
    private bool _hasLyrics;

    [ObservableProperty]
    private bool _isSynced;

    [ObservableProperty]
    private string _statusText = "暂无歌词";

    [ObservableProperty]
    private bool _isTranslated;

    [ObservableProperty]
    private bool _isTranslating;

    [ObservableProperty]
    private double _lyricFontSize = 14;

    [ObservableProperty]
    private int _lyricColorIndex;

    /// <summary>歌词正文画刷：0=跟随主题，其余为固定色。</summary>
    public Brush LyricForeground
    {
        get
        {
            var hex = LyricColorIndex switch
            {
                1 => "#FFFFFF",
                2 => "#222222",
                3 => "#FF6699",
                4 => "#3399FF",
                5 => "#66CC99",
                _ => null
            };
            if (hex is null)
                return Application.Current.TryFindResource("FgMuted") as Brush ?? Brushes.Gray;
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { return Brushes.Gray; }
        }
    }

    /// <summary>翻译行字号（正文 -2）。</summary>
    public double LyricTranslationFontSize => Math.Max(10, LyricFontSize - 2);

    partial void OnLyricFontSizeChanged(double value) => OnPropertyChanged(nameof(LyricTranslationFontSize));
    partial void OnLyricColorIndexChanged(int value) => OnPropertyChanged(nameof(LyricForeground));

    /// <summary>从用户设置同步歌词样式（切歌/加载时刷新）。</summary>
    private void RefreshLyricStyle()
    {
        var s = _settingsService.Settings;
        LyricFontSize = Math.Clamp(s.LyricFontSize, 12, 24);
        LyricColorIndex = Math.Clamp(s.LyricColorIndex, 0, 5);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static LyricViewModel()
    {
        // 部分翻译接口要求浏览器 UA
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
    }

    private string[]? _translations;

    public LyricViewModel(IAudioEngine engine, LocalLyricProvider lyricProvider, ILrcParser parser,
        UserSettingsService settingsService, IEnumerable<IOnlineLyricProvider> onlineLyricProviders)
    {
        _engine = engine;
        _lyricProvider = lyricProvider;
        _parser = parser;
        _settingsService = settingsService;
        _onlineLyricProvider = onlineLyricProviders.FirstOrDefault();

        _engine.PositionChanged += OnPositionChanged;
    }

    public async Task LoadLyricsAsync(Track track)
    {
        RefreshLyricStyle();
        Lines.Clear();
        CurrentIndex = -1;
        _document = null;
        HasLyrics = false;
        IsSynced = false;
        IsTranslated = false;
        _translations = null;
        StatusText = "暂无歌词";

        var doc = await _lyricProvider.FetchAsync(track);

        // 本地未命中且开启在线歌词时，走 LRCLIB
        if (doc is null
            && _settingsService.Settings.EnableOnlineLyrics
            && _onlineLyricProvider is not null)
        {
            StatusText = "正在搜索在线歌词 (LRCLIB)…";
            try
            {
                var lrc = await _onlineLyricProvider.GetLrcAsync(track.Title, track.Artist);
                if (!string.IsNullOrEmpty(lrc))
                    doc = _parser.Parse(lrc);
                if (doc is not null)
                    StatusText = "在线歌词 (LRCLIB)";
            }
            catch
            {
                // 在线歌词获取失败时静默回退到"未找到歌词"
            }
        }

        if (doc is null || doc.Lines.Count == 0)
        {
            if (string.IsNullOrEmpty(StatusText) || StatusText == "正在搜索在线歌词 (LRCLIB)…")
                StatusText = "未找到歌词";
            return;
        }

        _document = doc;
        HasLyrics = true;
        IsSynced = doc.IsSynced;
        if (StatusText != "在线歌词 (LRCLIB)")
            StatusText = doc.IsSynced ? "" : "纯文本歌词（无时间戳）";

        foreach (var line in doc.Lines)
            Lines.Add(line);
    }

    /// <summary>点击歌词行跳转到该行时间。</summary>
    [RelayCommand]
    private void SeekToLine(LyricLine line)
    {
        if (!IsSynced) return;
        _engine.Seek(line.Time);
    }

    /// <summary>切换歌词翻译显示（机器翻译为中文，双行对照）。</summary>
    [RelayCommand]
    private async Task ToggleTranslationAsync()
    {
        if (!HasLyrics || Lines.Count == 0) return;

        if (IsTranslated)
        {
            foreach (var line in Lines)
                line.Translation = null;
            IsTranslated = false;
            return;
        }

        if (_translations is null)
        {
            var prevStatus = StatusText;
            IsTranslating = true;
            StatusText = "正在翻译歌词…";
            try
            {
                _translations = await TranslateAsync(Lines.Select(l => l.Text));
            }
            catch
            {
                StatusText = "翻译失败，请检查网络";
                return;
            }
            finally
            {
                IsTranslating = false;
            }
            StatusText = prevStatus;
        }

        for (var i = 0; i < Lines.Count; i++)
        {
            var t = i < _translations.Length ? _translations[i] : null;
            Lines[i].Translation = string.IsNullOrWhiteSpace(t) ? null : t;
        }
        IsTranslated = true;
    }

    /// <summary>调用在线翻译接口批量翻译，返回与行数对齐的译文数组。优先 MyMemory（国内可用、免密钥），失败回退 Google（海外）。</summary>
    private static async Task<string[]> TranslateAsync(IEnumerable<string> lines)
    {
        var text = string.Join("\n", lines);
        try
        {
            return await TranslateViaMyMemoryAsync(text);
        }
        catch
        {
            // MyMemory 失败（额度用完/网络异常），回退 Google gtx
            return await TranslateViaGoogleAsync(text);
        }
    }

    /// <summary>MyMemory 翻译：免费免密钥，单次限长约 500 字符，按行分块请求；自动检测源语言并保留换行。</summary>
    private static async Task<string[]> TranslateViaMyMemoryAsync(string text)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (sb.Length > 0 && sb.Length + line.Length + 1 > 450)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());

        var parts = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var url = "https://api.mymemory.translated.net/get?langpair=Autodetect%7Czh-CN&q="
                      + Uri.EscapeDataString(chunk);
            using var resp = await Http.GetAsync(url);
            resp.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            var root = json.RootElement;

            if (root.TryGetProperty("quotaFinished", out var quota) && quota.ValueKind == JsonValueKind.True)
                throw new HttpRequestException("MyMemory 今日免费额度已用完");
            var status = root.GetProperty("responseStatus");
            if (status.ValueKind != JsonValueKind.Number || status.GetInt32() != 200)
                throw new HttpRequestException("MyMemory 翻译失败");

            parts.Add(root.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "");
        }

        return string.Join("\n", parts)
            .Replace("\r\n", "\n")
            .Split('\n');
    }

    /// <summary>Google 翻译免费接口（海外可用）。</summary>
    private static async Task<string[]> TranslateViaGoogleAsync(string text)
    {
        var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=zh-CN&dt=t&q="
                  + Uri.EscapeDataString(text);
        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var json = await JsonDocument.ParseAsync(stream);

        var sb = new StringBuilder();
        foreach (var seg in json.RootElement[0].EnumerateArray())
            sb.Append(seg[0].GetString());

        return sb.ToString()
            .Replace("\r\n", "\n")
            .Split('\n');
    }

    private void OnPositionChanged(object? sender, TimeSpan position)
    {
        if (_document is null || !IsSynced) return;

        var newIndex = _document.LineIndexAt(position);
        if (newIndex != CurrentIndex)
        {
            Application.Current?.Dispatcher.Invoke(() => CurrentIndex = newIndex);
        }
    }
}
