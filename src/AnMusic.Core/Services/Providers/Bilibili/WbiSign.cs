using System.Security.Cryptography;
using System.Text;

namespace AnMusic.Services.Providers.Bilibili;

/// <summary>
/// B 站 wbi 签名（社区公开算法）：
/// nav 接口取 img_key/sub_key → 固定混淆表重排取前 32 位为 mixinKey
/// → 参数按 key 排序拼接 → MD5(query + mixinKey) 得 w_rid。
/// </summary>
public static class WbiSign
{
    private static readonly int[] MixinKeyEncTab =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40,
        61, 26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11,
        36, 20, 34, 44, 52
    ];

    /// <summary>由 img_key + sub_key 生成 32 位 mixinKey。</summary>
    public static string GetMixinKey(string imgKey, string subKey)
    {
        var raw = imgKey + subKey;
        var sb = new StringBuilder(32);
        foreach (var i in MixinKeyEncTab)
        {
            if (i < raw.Length)
                sb.Append(raw[i]);
        }
        return sb.ToString(0, 32);
    }

    /// <summary>对参数做 wbi 签名，返回带 wts/w_rid 的完整 query 串。</summary>
    public static string Sign(Dictionary<string, string> parameters, string mixinKey)
    {
        parameters["wts"] = DateTimeOffset.Now.ToUnixTimeSeconds().ToString();

        // 按 key 排序；value 过滤 "!'()*" 字符后 URL 编码
        var query = string.Join("&",
            parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv =>
                {
                    var value = new string(kv.Value.Where(c => !"!'()*".Contains(c)).ToArray());
                    return $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(value)}";
                }));

        var wRid = Convert.ToHexStringLower(
            MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey)));
        return query + "&w_rid=" + wRid;
    }
}
