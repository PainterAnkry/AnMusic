using System.Text.Json;

namespace AnMusic.Services.Announcements;

/// <summary>一条公告（开发者写给用户的"私信"）。</summary>
/// <param name="Id">稳定标识（用来记住"已读"；改文案不要改 Id，否则用户会再看到一次）。</param>
/// <param name="Title">标题。</param>
/// <param name="Body">正文（纯文本，换行用 \n）。</param>
/// <param name="Level">级别：info / important / update，决定图标与配色。</param>
/// <param name="Date">日期文案（原样展示，如 2026-09-12）。</param>
/// <param name="MinVersion">只发给"不低于"该版本的用户（空 = 不限）。</param>
/// <param name="MaxVersion">只发给"不高于"该版本的用户（空 = 不限，常用于催老版本升级）。</param>
/// <param name="Url">详情链接（空 = 不显示"查看详情"）。</param>
/// <param name="UrlText">链接按钮文案。</param>
/// <param name="Pinned">是否置顶（重要公告用）。</param>
public sealed record Announcement(
    string Id,
    string Title,
    string Body,
    string Level = "info",
    string Date = "",
    string MinVersion = "",
    string MaxVersion = "",
    string Url = "",
    string UrlText = "",
    bool Pinned = false)
{
    /// <summary>左侧图标（按级别给，避免依赖额外图标字体）。</summary>
    public string Icon => Level switch
    {
        "important" => "⚠️",
        "update" => "🚀",
        "welcome" => "👋",
        _ => "📢",
    };

    /// <summary>是否重要公告（界面上给予强调色）。</summary>
    public bool IsImportant => Level is "important";
}

/// <summary>
/// 公告源解析：把仓库里（或自建服务器上）的 announcements.json 解析成公告列表。
/// </summary>
/// <remarks>
/// 设计原则是"绝不能把私信搞挂"：文件缺失、格式写错、字段类型不对，
/// 一律当作"没有公告"，只记日志，不影响版本升级提示与其它功能。
/// 只读 <c>messages</c> 数组，其余字段（例如自带的 _template 说明）会被忽略。
/// </remarks>
public static class AnnouncementFeed
{
    /// <summary>
    /// 解析公告 JSON。
    /// </summary>
    /// <param name="json">announcements.json 的内容。</param>
    /// <param name="currentVersion">当前客户端版本（用于版本定向）。</param>
    /// <param name="error">解析失败原因（成功为空串）。</param>
    /// <returns>命中的公告；顺序为：置顶在前，再按原文件顺序。</returns>
    public static IReadOnlyList<Announcement> Parse(string? json, string currentVersion, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(json)) return [];

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = $"公告文件格式错误：{ex.Message.Split('\n')[0]}";
            return [];
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("messages", out var messages) ||
                messages.ValueKind != JsonValueKind.Array)
                return [];

            var list = new List<Announcement>();
            foreach (var item in messages.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var title = Str(item, "title");
                var body = Str(item, "body");
                if (title.Length == 0 && body.Length == 0) continue; // 空条目跳过

                var id = Str(item, "id");
                if (id.Length == 0) id = title; // 没写 id 就用标题兜底，至少能做到"看过不再提醒"

                list.Add(new Announcement(
                    id,
                    title,
                    body,
                    Str(item, "level").ToLowerInvariant(),
                    Str(item, "date"),
                    Str(item, "minVersion"),
                    Str(item, "maxVersion"),
                    Str(item, "url"),
                    Str(item, "urlText"),
                    Bool(item, "pinned")));
            }

            // 版本定向：minVersion ≤ 当前版本 ≤ maxVersion 才发
            var hit = list.Where(a => InRange(a, currentVersion)).ToList();

            // 置顶的排前面（同一优先级内保持文件里的顺序，方便你按时间倒序写）
            return [.. hit.Where(a => a.Pinned), .. hit.Where(a => !a.Pinned)];
        }
    }

    /// <summary>这条公告是否应当发给该版本的用户。</summary>
    private static bool InRange(Announcement a, string currentVersion)
    {
        // "不低于 minVersion"：minVersion 比当前版本新 → 当前版本太老，不发
        if (a.MinVersion.Length > 0 && Update.UpdateFeed.IsNewer(a.MinVersion, currentVersion))
            return false;
        // "不高于 maxVersion"：当前版本比 maxVersion 新 → 已经过期，不发
        if (a.MaxVersion.Length > 0 && Update.UpdateFeed.IsNewer(currentVersion, a.MaxVersion))
            return false;
        return true;
    }

    private static string Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
