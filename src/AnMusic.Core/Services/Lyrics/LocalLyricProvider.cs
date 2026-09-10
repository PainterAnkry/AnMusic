using System.IO;
using AnMusic.Models;

namespace AnMusic.Services.Lyrics;

/// <summary>
/// 在线歌词扩展点（仅接口，不内置实现，规避合规风险）。
/// 第三方可实现此接口接入在线歌词源。
/// </summary>
public interface ILyricProvider
{
    Task<LyricDocument?> FetchAsync(Track track, CancellationToken ct = default);
}

/// <summary>
/// 本地歌词 Provider：在曲目同目录查找同名 .lrc 文件。
/// </summary>
public sealed class LocalLyricProvider : ILyricProvider
{
    private readonly ILrcParser _parser;

    public LocalLyricProvider(ILrcParser parser)
    {
        _parser = parser;
    }

    public Task<LyricDocument?> FetchAsync(Track track, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(track.FilePath))
            return Task.FromResult<LyricDocument?>(null);

        var baseName = Path.GetFileNameWithoutExtension(track.FilePath);
        var dir = Path.GetDirectoryName(track.FilePath);
        if (string.IsNullOrEmpty(dir))
            return Task.FromResult<LyricDocument?>(null);

        // 尝试 .lrc 和 .LRC
        var lrcPath = Path.Combine(dir, baseName + ".lrc");
        if (!File.Exists(lrcPath))
        {
            lrcPath = Path.Combine(dir, baseName + ".LRC");
            if (!File.Exists(lrcPath))
                return Task.FromResult<LyricDocument?>(null);
        }

        try
        {
            var text = File.ReadAllText(lrcPath);
            return Task.FromResult<LyricDocument?>(_parser.Parse(text));
        }
        catch
        {
            return Task.FromResult<LyricDocument?>(null);
        }
    }
}
