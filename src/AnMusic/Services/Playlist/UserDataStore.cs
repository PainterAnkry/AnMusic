using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnMusic.Models;

namespace AnMusic.Services.Playlist;

/// <summary>
/// 用户数据持久化：我喜欢、歌单、最近播放、搜索历史。
/// 存储于 %LocalAppData%\AnMusic\userdata.json。
/// </summary>
public sealed class UserDataStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnMusic", "userdata.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public List<Models.Playlist> Playlists { get; set; } = [];
    public List<Track> Favorites { get; set; } = [];
    public List<Track> Recent { get; set; } = [];
    public List<string> SearchHistory { get; set; } = [];

    /// <summary>从磁盘加载（损坏或不存在时返回空实例）。</summary>
    public static UserDataStore Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new UserDataStore();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<UserDataStore>(json, JsonOptions) ?? new UserDataStore();
        }
        catch
        {
            return new UserDataStore();
        }
    }

    /// <summary>保存到磁盘（静默失败不打断 UI）。</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 持久化失败不阻塞 UI
        }
    }
}
