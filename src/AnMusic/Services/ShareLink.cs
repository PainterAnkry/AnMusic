using System.IO;
using AnMusic.Models;
using Microsoft.Win32;

namespace AnMusic.Services;

/// <summary>解析出的分享目标（歌名/歌手用于在不支持按 id 查询的音源上回退搜索）。</summary>
public sealed record ShareTarget(string ProviderId, string Id, string Title, string Artist, string SourceUrl);

/// <summary>
/// AnMusic 自己的分享链接（自定义协议 anmusic://）：
/// 生成可被本软件识别的曲目链接、解析链接、注册 Windows 协议处理器，
/// 以及"第二个实例把链接转交给已运行实例"的文件中转。
/// </summary>
public static class ShareLink
{
    public const string Scheme = "anmusic";
    private const string ProtocolKey = @"Software\Classes\" + Scheme;

    /// <summary>待打开链接的中转文件（单实例模式下由新实例写入，旧实例读取）。</summary>
    private static readonly string PendingFile = Path.Combine(AppPaths.DataRoot, "pending-open.txt");

    #region 生成 / 解析

    /// <summary>生成曲目分享链接：anmusic://song?provider=..&amp;id=..&amp;title=..&amp;artist=..&amp;url=..</summary>
    public static string Build(Track track)
    {
        var query = new List<string>
        {
            "provider=" + Uri.EscapeDataString(track.ProviderId ?? ""),
            "id=" + Uri.EscapeDataString(track.Id ?? "")
        };
        if (!string.IsNullOrWhiteSpace(track.Title)) query.Add("title=" + Uri.EscapeDataString(track.Title));
        if (!string.IsNullOrWhiteSpace(track.Artist)) query.Add("artist=" + Uri.EscapeDataString(track.Artist));
        if (!string.IsNullOrWhiteSpace(track.SourceUrl)) query.Add("url=" + Uri.EscapeDataString(track.SourceUrl));
        return $"{Scheme}://song?" + string.Join("&", query);
    }

    /// <summary>文本是否是 AnMusic 分享链接（用于搜索框/命令行识别）。</summary>
    public static bool IsShareLink(string? text)
        => !string.IsNullOrWhiteSpace(text) &&
           text.Trim().StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析分享链接；不是本软件链接或缺少必要字段时返回 null。</summary>
    public static ShareTarget? TryParse(string? text)
    {
        if (!IsShareLink(text)) return null;

        try
        {
            var uri = new Uri(text!.Trim());
            if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)) return null;

            var query = ParseQuery(uri.Query);
            var provider = query.GetValueOrDefault("provider", "");
            var id = query.GetValueOrDefault("id", "");
            var title = query.GetValueOrDefault("title", "");
            var artist = query.GetValueOrDefault("artist", "");
            var url = query.GetValueOrDefault("url", "");

            // 兼容 anmusic:///?provider=... 与 anmusic://play?provider=... 等写法
            if (string.IsNullOrEmpty(provider) && string.IsNullOrEmpty(id) && string.IsNullOrEmpty(title))
                return null;

            return new ShareTarget(provider, id, title, artist, url);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx <= 0) continue;
            var key = pair[..idx];
            var value = Uri.UnescapeDataString(pair[(idx + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    #endregion

    #region Windows 协议注册（HKCU，无需管理员）

    /// <summary>
    /// 把 anmusic:// 注册到当前用户（HKCU\Software\Classes），
    /// 让浏览器/聊天工具里的分享链接点击后能直接打开 AnMusic。
    /// </summary>
    public static void RegisterProtocol()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using var key = Registry.CurrentUser.CreateSubKey(ProtocolKey);
            if (key is null) return;

            var command = $"\"{exePath}\" \"%1\"";
            var needsWrite = key.GetValue(null) as string != "URL:AnMusic 分享链接"
                             || key.GetValue("URL Protocol") as string != ""
                             || Registry.CurrentUser.OpenSubKey(ProtocolKey + @"\shell\open\command")?.GetValue(null) as string != command;
            if (!needsWrite) return; // 已注册且路径未变，避免每次启动都写注册表

            key.SetValue(null, "URL:AnMusic 分享链接");
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon?.SetValue(null, $"\"{exePath}\",0");
            using var open = key.CreateSubKey(@"shell\open");
            using var cmd = open?.CreateSubKey("command");
            cmd?.SetValue(null, command);
            key.SetValue("FriendlyTypeName", "AnMusic 分享链接");
        }
        catch (Exception ex)
        {
            AppPaths.LogError("注册 anmusic:// 协议", ex);
        }
    }

    #endregion

    #region 单实例之间的链接转交

    /// <summary>把链接写入中转文件（第二个实例启动时调用，随后唤醒已运行实例）。</summary>
    public static void QueuePendingOpen(string uri)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.WriteAllText(PendingFile, uri);
        }
        catch (Exception ex)
        {
            AppPaths.LogError("写入待打开链接", ex, uri);
        }
    }

    /// <summary>取出中转文件里的链接（取到即删除，避免重复打开）。</summary>
    public static string? TakePendingOpen()
    {
        try
        {
            if (!File.Exists(PendingFile)) return null;
            var uri = File.ReadAllText(PendingFile).Trim();
            try { File.Delete(PendingFile); } catch { /* 删除失败下次启动会重试打开，可接受 */ }
            return string.IsNullOrWhiteSpace(uri) ? null : uri;
        }
        catch (Exception ex)
        {
            AppPaths.LogError("读取待打开链接", ex);
            return null;
        }
    }

    #endregion
}
