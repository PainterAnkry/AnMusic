using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AnMusic.Services.Settings;

/// <summary>
/// 用户设置读写服务：JSON 文件存储于 %AppData%\AnMusic\settings.json。
/// </summary>
public sealed class UserSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;

    public UserSettings Settings { get; private set; }

    public UserSettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AnMusic");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "settings.json");
        Settings = Load();
    }

    /// <summary>重新从磁盘加载。</summary>
    public UserSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new UserSettings();
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[UserSettings] 加载失败: {ex.Message}");
        }
        return new UserSettings();
    }

    /// <summary>保存当前设置到磁盘。</summary>
    public void Save()
    {
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[UserSettings] 保存失败: {ex.Message}");
        }
    }

    /// <summary>修改设置并立即保存。</summary>
    public void Update(Action<UserSettings> mutate)
    {
        mutate(Settings);
        Save();
    }
}
