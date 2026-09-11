using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using AnMusic.Models;
using AnMusic.Services.Playlist;
using AnMusic.Services.Providers;
using AnMusic.Services.Providers.Bilibili;
using AnMusic.Services.Providers.JsPlugin;
using AnMusic.Services.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AnMusic.ViewModels;

/// <summary>
/// MainViewModel 的分享当前歌曲部分（partial 拆分，便于维护）。
/// </summary>
public partial class MainViewModel
{

    /// <summary>生成当前歌曲的分享文本：歌名/歌手/专辑 + 来源 + 可打开的原站链接。</summary>
    public string BuildShareText()
    {
        if (_playbackBar.CurrentTrack is not { } track) return "";

        var provider = _registry.Find(track.ProviderId);
        var source = provider?.DisplayName ?? track.ProviderId;
        var link = BuildShareLink(track);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"♪ {track.Title}");
        if (!string.IsNullOrWhiteSpace(track.Artist)) sb.AppendLine($"歌手：{track.Artist}");
        if (!string.IsNullOrWhiteSpace(track.Album)) sb.AppendLine($"专辑：{track.Album}");
        sb.AppendLine($"来源：{source}");
        // AnMusic 自己的链接放在最前面：粘回搜索框即可打开，也可注册协议后直接点击
        sb.AppendLine($"AnMusic 链接：{Services.ShareLink.Build(track)}");
        if (!string.IsNullOrWhiteSpace(link)) sb.AppendLine($"网页链接：{link}");
        sb.Append("—— 来自 AnMusic");
        return sb.ToString();
    }

    /// <summary>各音源的分享链接：优先用曲目自带的原站链接，其次按音源规则拼，本地文件用路径。</summary>
    private static string BuildShareLink(Track track)
    {
        if (!string.IsNullOrWhiteSpace(track.SourceUrl)) return track.SourceUrl;
        return BuildShareLinkByProvider(track);
    }

    private static string BuildShareLinkByProvider(Track track) => track.ProviderId switch
    {
        "bilibili" => BiliVideoUrl(track.Id),
        "netease" => $"https://music.163.com/#/song?id={track.Id}",
        "qqmusic" => $"https://y.qq.com/n/ryqq/songDetail/{track.Id}",
        "local-file" => track.FilePath ?? "",
        _ => "" // 第三方 .js 插件源没有统一的网页地址，只分享歌曲信息
    };

    /// <summary>B 站视频页链接：分P 曲目的 Id 形如 <c>BV1xx_p3</c>，要还原成 <c>?p=3</c>。</summary>
    private static string BiliVideoUrl(string id)
    {
        var (bvid, page) = BiliTrackId.Parse(id);
        return BiliTrackId.ToVideoUrl(bvid, page);
    }

    /// <summary>
    /// 打开一条 AnMusic 分享链接：优先按 id 命中（本地文件直接用路径），
    /// 在线源没有按 id 查询的接口时，用链接里的歌名/歌手搜索并播放最匹配的一首。
    /// </summary>
    public async Task<bool> OpenSharedLinkAsync(string? uri)
    {
        var target = Services.ShareLink.TryParse(uri);
        if (target is null)
        {
            Views.UiDialog.Warn("这不是有效的 AnMusic 分享链接");
            return false;
        }

        // 本地曲目：链接里的 id 就是文件路径
        if (target.ProviderId is "local-file" or "")
        {
            var path = !string.IsNullOrWhiteSpace(target.Id) ? target.Id : target.SourceUrl;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                await PlayTrackAsync(new Track { Id = path, Title = target.Title, Artist = target.Artist, FilePath = path, ProviderId = "local-file" });
                return true;
            }
            if (!string.IsNullOrWhiteSpace(path))
            {
                Views.UiDialog.Warn("分享的本地歌曲已不在原路径：" + Environment.NewLine + path);
                return false;
            }
        }

        var provider = _registry.Find(target.ProviderId);
        if (provider is null)
        {
            Views.UiDialog.Warn($"本机没有「{target.ProviderId}」音源，无法打开该分享链接。" +
                                Environment.NewLine + "可在 设置 → 音源 中安装对应插件后重试。");
            return false;
        }

        SearchStatus = $"正在打开分享的歌曲：{target.Title}";
        try
        {
            // 用「歌名 歌手」搜索，优先取 id 完全一致的结果
            var keyword = string.Join(' ', new[] { target.Title, target.Artist }.Where(x => !string.IsNullOrWhiteSpace(x)));
            var results = await provider.SearchAsync(keyword);
            var hit = results.FirstOrDefault(t => string.Equals(t.Id, target.Id, StringComparison.OrdinalIgnoreCase))
                      ?? results.FirstOrDefault();

            if (hit is null)
            {
                Views.UiDialog.Warn($"没有在「{provider.DisplayName}」找到这首歌：{keyword}");
                SearchStatus = "";
                return false;
            }

            await PlayTrackAsync(hit);
            SearchStatus = $"已打开分享的歌曲：{hit.Title}";
            return true;
        }
        catch (Exception ex)
        {
            Views.UiDialog.Error("打开分享链接失败", ex);
            return false;
        }
    }

    /// <summary>打开中转文件里的链接（第二个实例通过 anmusic:// 启动时使用）。</summary>
    public async Task OpenPendingSharedLinkAsync()
    {
        if (Services.ShareLink.TakePendingOpen() is { } uri)
            await OpenSharedLinkAsync(uri);
    }

    /// <summary>分享当前歌曲：复制到剪贴板并弹窗展示分享内容。</summary>
    public void ShareCurrentTrack()
    {
        if (_playbackBar.CurrentTrack is null)
        {
            Views.UiDialog.Info("还没有正在播放的歌曲，先播放一首再分享吧");
            return;
        }

        var text = BuildShareText();
        var copied = false;
        try
        {
            System.Windows.Clipboard.SetText(text);
            copied = true;
        }
        catch { /* 剪贴板被其他程序占用时仍展示内容供手动复制 */ }

        Views.TextDialogWindow.Show("AnMusic", copied ? "分享歌曲（已复制到剪贴板）" : "分享歌曲", text);
        if (copied) SearchStatus = $"已复制分享内容：{_playbackBar.CurrentTrack.Title}";
    }
}
