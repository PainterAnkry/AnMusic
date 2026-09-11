using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using AnMusic.Services.Announcements;
using AnMusic.Services.Settings;
using AnMusic.Services.Update;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.ViewModels;

/// <summary>私信面板里的一条消息。</summary>
public sealed record InboxMessage
{
    /// <summary>左侧图标（emoji，避免额外图标字体依赖）。</summary>
    public required string Icon { get; init; }

    public required string Title { get; init; }

    /// <summary>正文（版本升级消息里是 Release 说明的纯文本摘要）。</summary>
    public string Body { get; init; } = "";

    /// <summary>右上角时间/来源文案。</summary>
    public string TimeText { get; init; } = "";

    /// <summary>是否未读（标题前显示小圆点）。</summary>
    public bool IsUnread { get; init; }

    /// <summary>这条消息是否可以"下载并安装"。</summary>
    public bool CanInstall { get; init; }

    /// <summary>详情链接（空则不显示链接按钮）。</summary>
    public string ReleaseUrl { get; init; } = "";

    /// <summary>详情按钮文案。</summary>
    public string LinkText { get; init; } = "查看完整说明";

    /// <summary>重要公告（界面上用强调色描边 + 标题变色）。</summary>
    public bool IsImportant { get; init; }

    /// <summary>是否显示"重新检查"按钮（检查失败时）。</summary>
    public bool CanRetry { get; init; }

    public bool HasActions => CanInstall || CanRetry || ReleaseUrl.Length > 0;
}

/// <summary>私信内容的纯计算结果（不依赖任何服务，便于单测）。</summary>
public sealed record InboxContent(IReadOnlyList<InboxMessage> Messages, bool HasUnread, string Summary);

/// <summary>
/// 标题栏「私信」：应用内通知中心，目前承载版本升级提示。
/// </summary>
/// <remarks>
/// 为什么单独一个 VM：通知是全局的（标题栏常驻），而设置页只是其中的一个展示位。
/// 这里只负责"查到什么、要不要亮红点"，真正的下载安装仍然复用设置页那套流程
/// （<see cref="SettingsViewModel.PrepareUpdate"/> + <c>DownloadAndInstallUpdateCommand</c>），
/// 避免把 30 多行下载/进度/启动安装的逻辑写第二遍。
/// 内容生成（<see cref="Compose"/>）是纯函数，逻辑与网络/服务解耦，可以直接单测。
/// </remarks>
public sealed partial class InboxViewModel : ObservableObject
{
    private const string Repo = "PainterAnkry/AnMusic";

    /// <summary>启动后自动检查更新的延迟：先让界面起来，再打这一次网络请求。</summary>
    private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromSeconds(3);

    private readonly UserSettingsService _settings;
    private readonly SettingsViewModel _settingsViewModel;

    /// <summary>检查到的最新版本（空 = 还没查到）。</summary>
    private string _latestVersion = "";

    /// <summary>本次取到的发布信息（null = 取失败）。</summary>
    private UpdateInfo? _release;

    /// <summary>版本信息获取失败原因（成功为 null）。</summary>
    private string? _releaseError;

    /// <summary>本次取到的公告（已按版本过滤）。</summary>
    private IReadOnlyList<Announcement> _announcements = [];

    /// <summary>公告获取失败原因（成功为空串；"没有公告文件"不算失败）。</summary>
    private string _announcementError = "";

    public InboxViewModel(UserSettingsService settings, SettingsViewModel settingsViewModel)
    {
        _settings = settings;
        _settingsViewModel = settingsViewModel;
    }

    /// <summary>消息列表（新的在前）。</summary>
    public ObservableCollection<InboxMessage> Messages { get; } = [];

    /// <summary>内置公告源：仓库根目录的 announcements.json（走 GitHub contents API 取原始内容）。</summary>
    /// <remarks>
    /// 想换成自建服务器 / Gitee 镜像时，在设置页填「公告源地址」即可，无需改代码。
    /// GitHub 未认证接口对本机 IP 有 60 次/小时的限制，一启动只取一次，够用。
    /// </remarks>
    private const string BuiltinAnnouncementUrl =
        "https://api.github.com/repos/PainterAnkry/AnMusic/contents/announcements.json";

    /// <summary>已读公告 Id 的保留条数。</summary>
    private const int MaxSeenIds = 100;

    [ObservableProperty]
    private bool _isChecking;

    /// <summary>是否允许手动触发检查（检查中禁用按钮）。</summary>
    public bool CanCheck => !IsChecking;

    partial void OnIsCheckingChanged(bool value) => OnPropertyChanged(nameof(CanCheck));

    /// <summary>面板标题右侧的概要文案。</summary>
    [ObservableProperty]
    private string _summaryText = "正在检查…";

    /// <summary>面板底部的时间/状态说明。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>当前版本号（与设置页同源：程序集信息）。</summary>
    public string CurrentVersion => _settingsViewModel.CurrentVersion;

    /// <summary>是否有未读（标题栏按钮上亮小红点）。</summary>
    [ObservableProperty]
    private bool _hasUnread;

    /// <summary>标题栏按钮的提示文案。</summary>
    public string ToolTipText => HasUnread ? "私信（有新的版本通知）" : "私信";

    partial void OnHasUnreadChanged(bool value) => OnPropertyChanged(nameof(ToolTipText));

    /// <summary>启动后延迟检查一次（失败不打扰用户，红点不亮）。</summary>
    public async Task CheckOnStartupAsync()
    {
        try
        {
            await Task.Delay(StartupCheckDelay);
            await CheckAsync();
        }
        catch (Exception ex)
        {
            // 启动时的静默检查：任何异常都只进日志
            Services.AppPaths.LogError("启动检查更新", ex);
        }
    }

    /// <summary>查询最新版本 + 公告，并据此刷新私信内容。</summary>
    [RelayCommand]
    public async Task CheckAsync()
    {
        if (IsChecking) return;
        IsChecking = true;
        SummaryText = "正在检查…";
        try
        {
            // 两条来源互不影响：一条失败不该让另一条也不显示
            await Task.WhenAll(FetchReleaseAsync(), FetchAnnouncementsAsync());
            Rebuild();
        }
        catch (Exception ex)
        {
            Services.AppPaths.LogError("检查私信", ex);
            _release = null;
            _releaseError = "检查更新失败，请检查网络或代理设置";
            Rebuild();
        }
        finally
        {
            IsChecking = false;
            StatusText = $"上次检查 {DateTime.Now:HH:mm}";
        }
    }

    /// <summary>取最新版本信息（失败时只记录原因，不抛异常）。</summary>
    private async Task FetchReleaseAsync()
    {
        _release = null;
        _releaseError = null;
        try
        {
            var http = Services.Net.HttpService.Client; // 统一出口：含 UA / 超时 / 代理
            using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (!resp.IsSuccessStatusCode)
            {
                _releaseError = $"查询失败（HTTP {(int)resp.StatusCode}）";
                return;
            }

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            if (UpdateFeed.ParseRelease(doc.RootElement) is not { } info)
            {
                _releaseError = "没有从发布信息里读到版本号";
                return;
            }
            _release = info;
        }
        catch (Exception ex)
        {
            Services.AppPaths.LogError("检查更新", ex);
            _releaseError = "检查更新失败，请检查网络或代理设置";
        }
    }

    /// <summary>取公告（失败时只记录原因；「还没有公告文件」属正常情况，不提示）。</summary>
    private async Task FetchAnnouncementsAsync()
    {
        _announcements = [];
        _announcementError = "";

        var url = _settings.Settings.AnnouncementUrl;
        if (string.IsNullOrWhiteSpace(url)) url = BuiltinAnnouncementUrl;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // GitHub contents API：要原始文件内容，而不是它默认返回的 base64 JSON
            if (url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github.raw");

            using var resp = await Services.Net.HttpService.Client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
                    _announcementError = $"公告获取失败（HTTP {(int)resp.StatusCode}）";
                return;
            }

            var json = await resp.Content.ReadAsStringAsync();
            _announcements = AnnouncementFeed.Parse(json, CurrentVersion, out var error);
            _announcementError = error;
        }
        catch (Exception ex)
        {
            Services.AppPaths.LogError("获取公告", ex, url);
            _announcementError = "公告获取失败";
        }
    }

    /// <summary>
    /// 按一份发布信息生成私信内容（纯函数：不碰网络、不碰设置，便于单测）。
    /// </summary>
    /// <param name="info">最新 Release 解析结果。</param>
    /// <param name="currentVersion">当前版本号。</param>
    /// <param name="seenVersion">已经看过通知的版本号（没看过传空串）。</param>
    public static InboxContent Compose(UpdateInfo info, string currentVersion, string seenVersion)
    {
        var newer = UpdateFeed.IsNewer(info.LatestVersion, currentVersion);
        var unread = newer && !string.Equals(seenVersion, info.LatestVersion, StringComparison.Ordinal);

        if (!newer)
        {
            return new InboxContent(
            [
                new InboxMessage
                {
                    Icon = "✅",
                    Title = "已是最新版本",
                    Body = $"当前版本 v{currentVersion}，暂时没有可更新的内容。",
                    TimeText = FormatPublished(info.PublishedAt),
                }
            ], false, "暂无新消息");
        }

        var message = new InboxMessage
        {
            Icon = "🚀",
            Title = $"发现新版本 v{info.LatestVersion}",
            Body = ComposeUpgradeBody(info, currentVersion),
            TimeText = FormatPublished(info.PublishedAt),
            IsUnread = unread,
            CanInstall = info.AssetUrl is not null,
            ReleaseUrl = info.HtmlUrl,
        };
        return new InboxContent([message], unread, unread ? "1 条新消息" : $"1 条消息 · 已看过 v{info.LatestVersion}");
    }

    /// <summary>把"版本信息 + 公告"合成为私信列表。</summary>
    private void Rebuild()
    {
        var messages = new List<InboxMessage>();

        // 1) 版本升级（有新版）或"已是最新版本"
        if (_release is { } release)
        {
            _latestVersion = release.LatestVersion;
            messages.AddRange(Compose(release, CurrentVersion, _settings.Settings.LastSeenUpdateVersion).Messages);
            if (UpdateFeed.IsNewer(release.LatestVersion, CurrentVersion))
                _settingsViewModel.PrepareUpdate(release); // 让设置页的「下载并安装」同步可用
        }

        // 2) 公告（开发者主动发的消息）
        messages.AddRange(ComposeAnnouncements(_announcements, _settings.Settings.SeenAnnouncementIds ?? []));

        // 3) 拿不到版本信息时讲清楚原因；公告能拿到就照常显示
        if (_release is null)
        {
            messages.Insert(0, new InboxMessage
            {
                Icon = "📭",
                Title = "版本信息获取失败",
                Body = (_releaseError ?? "未知原因") + "\n可点下方「检查更新」重试；已配置代理的话请确认代理可用。",
                CanRetry = true,
            });
        }
        if (_announcementError.Length > 0)
        {
            messages.Add(new InboxMessage
            {
                Icon = "📭",
                Title = "公告获取失败",
                Body = _announcementError + "\n（不影响版本更新提示）",
            });
        }

        Messages.Clear();
        foreach (var m in messages) Messages.Add(m);

        var unreadCount = messages.Count(m => m.IsUnread);
        HasUnread = unreadCount > 0;
        SummaryText = unreadCount > 0
            ? $"{unreadCount} 条新消息"
            : messages.Count == 0
                ? "暂无新消息"
                : $"{messages.Count} 条消息 · 都已看过";
    }

    /// <summary>
    /// 公告 → 私信消息（纯函数：不碰网络、不碰设置，便于单测）。
    /// </summary>
    /// <param name="announcements">已按版本过滤好的公告。</param>
    /// <param name="seenIds">已读过的公告 Id。</param>
    public static IReadOnlyList<InboxMessage> ComposeAnnouncements(
        IReadOnlyList<Announcement> announcements,
        IReadOnlyCollection<string> seenIds)
    {
        var list = new List<InboxMessage>(announcements.Count);
        foreach (var a in announcements)
        {
            list.Add(new InboxMessage
            {
                Icon = a.Icon,
                Title = a.Title,
                Body = ToPlainText(a.Body), // 公告正文也允许写 Markdown，清洗规则与更新说明一致
                TimeText = a.Date,
                IsUnread = !seenIds.Contains(a.Id),
                ReleaseUrl = a.Url,
                LinkText = a.UrlText.Length > 0 ? a.UrlText : "查看详情",
                IsImportant = a.IsImportant,
            });
        }
        return list;
    }

    /// <summary>升级消息正文：版本对比 + Release 说明摘要。</summary>
    private static string ComposeUpgradeBody(UpdateInfo info, string currentVersion)
    {
        var head = $"当前 v{currentVersion} → 最新 v{info.LatestVersion}";
        var notes = ToPlainText(info.Notes);
        if (notes.Length == 0)
            return head + "\n（该版本没有填写更新说明，可点「查看完整说明」到 GitHub 查看。）";
        return head + "\n\n更新内容：\n" + notes;
    }

    /// <summary>标记为已读（点开私信面板时调用）：小红点消失，并记住看过哪个版本 / 哪些公告。</summary>
    public void MarkRead()
    {
        HasUnread = false;

        var seenIds = new List<string>(_settings.Settings.SeenAnnouncementIds ?? []);
        var newlySeen = _announcements.Select(a => a.Id).Where(id => !seenIds.Contains(id)).ToList();
        if (newlySeen.Count > 0)
        {
            seenIds.InsertRange(0, newlySeen);
            // 只保留最近若干条，长期使用也不会无限增长
            if (seenIds.Count > MaxSeenIds) seenIds.RemoveRange(MaxSeenIds, seenIds.Count - MaxSeenIds);
            _settings.Update(s => s.SeenAnnouncementIds = seenIds);
        }

        if (_latestVersion.Length > 0)
            _settings.Update(s => s.LastSeenUpdateVersion = _latestVersion);

        // InboxMessage 是只读对象：替换成已读副本，界面上的未读小圆点随之消失
        for (var i = 0; i < Messages.Count; i++)
        {
            if (Messages[i].IsUnread) Messages[i] = Messages[i] with { IsUnread = false };
        }

        if (Messages.All(m => !m.IsUnread) && Messages.Count > 0)
            SummaryText = $"{Messages.Count} 条消息 · 都已看过";
    }

    /// <summary>下载并安装：复用设置页的更新流程（含进度与安装确认）。</summary>
    [RelayCommand]
    private void Install()
    {
        if (_settingsViewModel.DownloadAndInstallUpdateCommand.CanExecute(null))
            _settingsViewModel.DownloadAndInstallUpdateCommand.Execute(null);
    }

    /// <summary>用系统浏览器打开 Release 页面。</summary>
    [RelayCommand]
    private void OpenRelease(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Services.AppPaths.LogError("打开更新说明", ex, url);
        }
    }

    /// <summary>Release 说明 → 纯文本摘要（去掉 Markdown 标记、压缩空行、限长）。</summary>
    private static string ToPlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var raw in lines)
        {
            var line = raw.Trim();

            // 表格行整行丢掉：面板宽度放不下表格，留着只会变成一排竖线
            if (line.StartsWith('|')) continue;

            // 标题 / 引用 / 列表符号去掉，保留文字本身
            line = line.TrimStart('#', '>', '-', '*', ' ').TrimEnd();
            if (line.Length == 0)
            {
                // 连续空行压成一个
                if (kept.Count > 0 && kept[^1].Length > 0) kept.Add("");
                continue;
            }

            kept.Add(StripInlineMarks(line));
        }

        while (kept.Count > 0 && kept[^1].Length == 0) kept.RemoveAt(kept.Count - 1);
        var text = string.Join("\n", kept);
        const int max = 420; // 面板高度有限，超长部分让用户点"查看完整说明"
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    /// <summary>去掉行内 Markdown：**粗体** / `代码` / [文字](链接) → 纯文字。</summary>
    private static string StripInlineMarks(string line)
    {
        line = BoldMark().Replace(line, "$1");
        line = CodeMark().Replace(line, "$1");
        line = LinkMark().Replace(line, "$1");
        return line.Replace("**", "").Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial System.Text.RegularExpressions.Regex BoldMark();

    [System.Text.RegularExpressions.GeneratedRegex(@"`([^`]+)`")]
    private static partial System.Text.RegularExpressions.Regex CodeMark();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial System.Text.RegularExpressions.Regex LinkMark();

    /// <summary>ISO 时间 → "M月d日 发布"。</summary>
    private static string FormatPublished(string iso)
        => DateTimeOffset.TryParse(iso, out var t) ? $"{t.LocalDateTime:M月d日} 发布" : "";
}
