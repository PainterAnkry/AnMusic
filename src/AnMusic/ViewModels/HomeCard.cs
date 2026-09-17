using System.Windows.Input;
using AnMusic.Models;

namespace AnMusic.ViewModels;

/// <summary>
/// 主页上的一个入口卡片（横幅 / 歌单 / 快捷入口共用）。
/// </summary>
/// <remarks>
/// 主页只做"把现有功能摆出来"，所以卡片要么指向一个既有命令，要么交给界面层打开
/// 现成的窗口/弹层（<see cref="ActionKey"/>，如均衡器、一起听）。
/// 封面绑定的是曲目对象而不是路径：封面是后台异步缓存的，绑曲目才能自动刷新。
/// </remarks>
public sealed class HomeCard
{
    /// <summary>左侧/角标图标（emoji，避免额外图标字体依赖）。</summary>
    public required string Icon { get; init; }

    public required string Title { get; init; }

    /// <summary>一句说明（用现有数据算出来，例如"收藏的 12 首"）。</summary>
    public string Description { get; init; } = "";

    /// <summary>封面来源曲目（可为空，空则用默认封面）。</summary>
    public Track? CoverTrack { get; init; }

    /// <summary>
    /// 歌单卡片绑定的歌单（自定义封面 / 最近添加的歌曲封面都由它提供）。
    /// </summary>
    public Playlist? Playlist { get; init; }

    /// <summary>内置默认封面的打包地址（无自有封面时显示，保证卡片不空）。</summary>
    public string DefaultCover { get; init; } = "";

    /// <summary>点击执行的既有命令。</summary>
    public ICommand? Command { get; init; }

    public object? CommandParameter { get; init; }

    /// <summary>是否是"新建歌单"这类虚线占位卡。</summary>
    public bool IsPlaceholder { get; init; }
}
