using System.Collections.ObjectModel;
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

    /// <summary>歌词搜索关键词。</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>当前歌词行文本（供桌面歌词窗口绑定，无歌词时为空串）。</summary>
    public string CurrentLineText
    {
        get
        {
            var idx = CurrentIndex;
            if (idx < 0 || idx >= Lines.Count) return "";
            return Lines[idx].Text ?? "";
        }
    }

    /// <summary>当前歌词行译文（供桌面歌词窗口绑定，无译文时为空串）。</summary>
    public string CurrentLineTranslation
    {
        get
        {
            var idx = CurrentIndex;
            if (idx < 0 || idx >= Lines.Count) return "";
            return Lines[idx].Translation ?? "";
        }
    }

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

    partial void OnCurrentIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentLineText));
        OnPropertyChanged(nameof(CurrentLineTranslation));
    }

    /// <summary>从用户设置同步歌词样式（切歌/加载时刷新）。</summary>
    private void RefreshLyricStyle()
    {
        var s = _settingsService.Settings;
        LyricFontSize = Math.Clamp(s.LyricFontSize, 12, 24);
        LyricColorIndex = Math.Clamp(s.LyricColorIndex, 0, 5);
    }

    private string[]? _translations;

    /// <summary>
    /// 歌词翻译统一走 Core（两端共享同一份实现，并且会套用用户配置的代理）。
    /// 以前这里是内联实现 + 自建 HttpClient，导致翻译请求绕过代理设置。
    /// </summary>
    private readonly LyricTranslationService _translator = new();

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
        _trackTitle = track.Title;
        _trackArtist = track.Artist;
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

    /// <summary>切换歌词字号（14 → 18 → 22 → 14）。</summary>
    [RelayCommand]
    private void ToggleFontSize()
    {
        var next = LyricFontSize switch
        {
            < 16 => 18,
            < 20 => 22,
            _ => 14
        };
        LyricFontSize = next;
        _settingsService.Update(s => s.LyricFontSize = next);
    }

    /// <summary>循环切换歌词颜色（0=跟随主题 → 1=白 → 2=黑 → 3=粉 → 4=蓝 → 5=绿）。</summary>
    [RelayCommand]
    private void CycleColor()
    {
        var next = (LyricColorIndex + 1) % 6;
        LyricColorIndex = next;
        _settingsService.Update(s => s.LyricColorIndex = next);
    }

    /// <summary>当前曲目（手动歌词搜索时判断命中是否属于当前播放歌曲）。</summary>
    private string _trackTitle = "";
    private string _trackArtist = "";

    /// <summary>搜索歌词（回车触发）：按输入的歌名/“歌名 - 歌手”在线匹配歌词。</summary>
    [RelayCommand]
    private async Task SearchLyricsByNameAsync()
    {
        var query = SearchText?.Trim();
        if (string.IsNullOrEmpty(query))
            return;

        if (_onlineLyricProvider is not LrclibLyricProvider lrclib)
        {
            StatusText = "歌词搜索源不可用（LRCLIB 在线歌词未启用）";
            return;
        }

        // 支持 “歌名 - 歌手” 格式（其它输入整体作为歌名模糊匹配）
        var dash = query.LastIndexOf(" - ", StringComparison.Ordinal);
        var title = dash > 0 ? query[..dash].Trim() : query;
        var artist = dash > 0 ? query[(dash + 3)..].Trim() : null;

        StatusText = $"正在搜索“{query}”的歌词…";
        try
        {
            var hit = await lrclib.SearchBestAsync(title, artist);
            if (hit is null)
            {
                StatusText = $"未找到“{query}”的歌词";
                return;
            }

            var doc = _parser.Parse(hit.Lrc);
            if (doc is null || doc.Lines.Count == 0)
            {
                StatusText = $"“{query}”暂无可用歌词";
                return;
            }

            // 命中是否属于当前播放曲目（歌名+歌手宽松匹配都通过才视为同步装载）
            var isCurrent = _trackTitle.Length > 0
                && NameEquals(hit.Title, _trackTitle)
                && (hit.Artist.Length == 0 || NameEquals(hit.Artist, _trackArtist));

            Lines.Clear();
            CurrentIndex = -1;
            IsTranslated = false;
            _translations = null;
            HasLyrics = true;

            if (isCurrent)
            {
                // 与自动加载一致：跟随播放进度、可点击行跳转
                _document = doc;
                IsSynced = doc.IsSynced;
                StatusText = doc.IsSynced ? "在线歌词 (LRCLIB)" : "纯文本歌词（无时间戳）";
            }
            else
            {
                // 预览其他歌曲的歌词：不绑定播放进度，行点击不跳转播放
                _document = null;
                IsSynced = false;
                StatusText = $"预览歌词：{hit.Title} - {hit.Artist}（非当前播放歌曲）";
            }
            foreach (var line in doc.Lines)
                Lines.Add(line);
        }
        catch
        {
            StatusText = "歌词搜索失败（网络异常）";
        }
    }

    /// <summary>忽略大小写的宽松名称比较（允许括号版本/前后缀差异）。</summary>
    private static bool NameEquals(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        a = a.Trim().ToLowerInvariant();
        b = b.Trim().ToLowerInvariant();
        return a == b || (a.Length >= 3 && (a.Contains(b) || b.Contains(a)));
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
                _translations = await _translator.TranslateAsync(Lines.Select(l => l.Text).ToList());
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
