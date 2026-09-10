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
        if (!string.IsNullOrWhiteSpace(link)) sb.AppendLine($"链接：{link}");
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
        "bilibili" => $"https://www.bilibili.com/video/{track.Id}",
        "netease" => $"https://music.163.com/#/song?id={track.Id}",
        "qqmusic" => $"https://y.qq.com/n/ryqq/songDetail/{track.Id}",
        "local-file" => track.FilePath ?? "",
        _ => "" // 第三方 .js 插件源没有统一的网页地址，只分享歌曲信息
    };

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
