using System.Globalization;
using System.Text.RegularExpressions;
using AnMusic.Models;

namespace AnMusic.Services.Lyrics;

/// <summary>
/// LRC 歌词解析器：支持 [mm:ss.xx]、多时间戳行、[offset:xxx] 偏移、纯文本行。
/// </summary>
public sealed class LrcParser : ILrcParser
{
    // 匹配 [mm:ss.xx] / [m:ss.xxx] / [mm:ss] 等
    private static readonly Regex TimeTagRegex = new(
        @"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]",
        RegexOptions.Compiled);

    // 匹配 [offset:+500] / [offset:-200]
    private static readonly Regex OffsetRegex = new(
        @"\[offset:([+-]?\d+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public LyricDocument Parse(string lrcText)
    {
        var doc = new LyricDocument();
        if (string.IsNullOrWhiteSpace(lrcText))
            return doc;

        bool hasTimelessLines = false;
        bool hasTimedLines = false;

        foreach (var rawLine in lrcText.Split('\n', '\r'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // 检查 offset 标签
            var offsetMatch = OffsetRegex.Match(line);
            if (offsetMatch.Success && double.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
            {
                doc.OffsetMs = offset;
                // offset 行可能只有 [offset:xxx] 无歌词文本
                if (line == offsetMatch.Value) continue;
            }

            // 提取所有时间标签
            var timeMatches = TimeTagRegex.Matches(line);
            if (timeMatches.Count > 0)
            {
                hasTimedLines = true;
                // 去掉时间标签后的文本
                var text = TimeTagRegex.Replace(line, "").Trim();
                foreach (Match tm in timeMatches)
                {
                    var ts = ParseTime(tm);
                    doc.Lines.Add(new LyricLine { Time = ts, Text = text });
                }
            }
            else if (!offsetMatch.Success)
            {
                // 纯文本行（无时间标签，非 offset 行）
                hasTimelessLines = true;
                doc.Lines.Add(new LyricLine { Time = TimeSpan.Zero, Text = line });
            }
        }

        // 按时间排序
        doc.Lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        doc.MarkSynced(hasTimedLines && !hasTimelessLines);

        return doc;
    }

    private static TimeSpan ParseTime(Match match)
    {
        int minutes = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        double frac = 0;
        if (match.Groups[3].Success)
        {
            var fracStr = match.Groups[3].Value;
            // 2位=百分秒, 3位=毫秒
            frac = fracStr.Length == 2
                ? int.Parse(fracStr, CultureInfo.InvariantCulture) * 10.0
                : int.Parse(fracStr, CultureInfo.InvariantCulture);
        }
        return TimeSpan.FromMilliseconds(minutes * 60000 + seconds * 1000 + frac);
    }
}
