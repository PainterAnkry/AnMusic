using AnMusic.Models;

namespace AnMusic.Services.Lyrics;

/// <summary>
/// LRC 歌词解析器抽象。
/// </summary>
public interface ILrcParser
{
    LyricDocument Parse(string lrcText);
}
