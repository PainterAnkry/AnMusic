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
        PropertyNameCaseInsensitive = true,
        // 窗口/悬浮窗位置用 double.NaN 表示「未设置」，而默认序列化遇到 NaN 会抛异常，
        // 导致整次保存静默失败（设置改了却不落盘的元凶）。允许 NaN 字面量后即可正常读写。
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private readonly string _filePath;

    public UserSettings Settings { get; private set; }

    public UserSettingsService()
    {
        // 走 AppPaths 而非硬编码 %AppData%：安卓端数据根由宿主切到应用私有目录
        _filePath = Services.AppPaths.SettingsFile;
        Directory.CreateDirectory(Services.AppPaths.DataRoot);
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

    /// <summary>保存当前设置到磁盘（失败时写入日志文件，避免再次出现"设置静默丢失"）。</summary>
    public void Save()
    {
        try
        {
            File.WriteAllText(_filePath, JsonSerializer.Serialize(Settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[UserSettings] 保存失败: {ex.Message}");
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetDirectoryName(_filePath)!, "settings-save-error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}");
            }
            catch { /* 日志写入失败只能放弃 */ }
        }
    }

    /// <summary>修改设置并立即保存。</summary>
    public void Update(Action<UserSettings> mutate)
    {
        mutate(Settings);
        Save();
    }
}
