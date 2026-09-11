using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using AnMusic.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnMusic.Android.ViewModels;

/// <summary>导入任务记录（m3u 解析产物）。</summary>
public sealed partial class ImportTaskItem : ObservableObject
{
    public required string SourcePath { get; init; }
    public required string DisplayName { get; init; }
    public required long SizeBytes { get; init; }
    [ObservableProperty] private string _statusText = "等待";

    public string SizeText
    {
        get
        {
            var kb = SizeBytes / 1024.0;
            if (kb < 1024) return $"{kb:F1} KB";
            var mb = kb / 1024.0;
            return $"{mb:F2} MB";
        }
    }
}

/// <summary>
/// 导入音乐：复用 LibraryViewModel 的扫描（系统目录 / 用户添加目录 / m3u 解析）。
/// </summary>
/// <remarks>
/// 设计原则：导入 =「告诉应用新曲源在哪」+「触发一次扫描」两步。
/// 不重复扫描逻辑，全部走 LibraryViewModel.ScanCommand（里面已经处理了
/// 权限 / MediaStore / 去重 / 元数据 / 封面补全的完整流水线）。
/// </remarks>
public sealed partial class ImportViewModel : ObservableObject
{
    private readonly LibraryViewModel _library;

    /// <summary>用户添加的额外扫描目录（启动扫描时会合并进系统目录一起扫描）。</summary>
    public ObservableCollection<string> ExtraDirs { get; } = [];

    public ObservableCollection<ImportTaskItem> Tasks { get; } = [];

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _m3uText = string.Empty;

    public string HelpText => "AnMusic 的导入有三种方式：\n\n" +
                               "1. 系统扫描：扫描 /Music、/Download 等系统目录里的音频，自动提取封面与元数据入库。\n\n" +
                               "2. 自定义目录：把你存放音乐的文件夹路径加进来，下次扫描也会一并处理。\n\n" +
                               "3. m3u 解析：把 .m3u / .m3u8 文件内容粘到文本框（每行一个文件绝对路径），应用会立刻入库。\n\n" +
                               "已下载的在线缓冲音频在「下载管理」页可以一键整理。";

    public ImportViewModel(LibraryViewModel library)
    {
        _library = library;
        // 已存在的用户目录先列出来
        foreach (var dir in _library.UserExtraScanDirs)
            ExtraDirs.Add(dir);
    }

    #region 系统扫描

    [RelayCommand]
    private async Task ScanSystemAsync()
    {
        if (IsBusy || _library.IsScanning) return;
        StatusText = "正在扫描系统音乐目录…";
        await _library.ScanCommand.ExecuteAsync(null);
        StatusText = _library.ScanStatus;
    }

    [RelayCommand]
    private void RemoveExtraDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        _library.UserExtraScanDirs.Remove(dir);
        ExtraDirs.Remove(dir);
        StatusText = $"已移除 {dir}";
    }

    /// <summary>用文件选择器挑一个目录追加到扫描列表。SAF / DirectoryPicker 跨平台支持有限，
    /// 这里让用户输入绝对路径并保存。</summary>
    [RelayCommand]
    private async Task AddExtraDirPromptAsync()
    {
        // MAUI 自带 FolderPicker 在 net10.0 还不稳定，这里用文本提示代替
        // 用户在桌面端能直接选目录，在手机上多走一步粘贴路径
        var input = await Application.Current?.MainPage?.DisplayPromptAsync(
            "添加扫描目录",
            "输入设备上音乐文件夹的绝对路径：\n（例：/sdcard/Music/AnMusic）",
            "添加", "取消", "/sdcard/Music/")!;
        if (string.IsNullOrWhiteSpace(input)) return;

        var dir = input.Trim();
        if (_library.UserExtraScanDirs.Contains(dir))
        {
            StatusText = "该目录已经在扫描列表里";
            return;
        }

        if (!Directory.Exists(dir))
        {
            StatusText = "目录不存在，请检查路径";
            return;
        }

        _library.UserExtraScanDirs.Add(dir);
        ExtraDirs.Add(dir);
        StatusText = $"已加入：{dir}（下次扫描生效）";
    }

    #endregion

    #region m3u 解析

    [RelayCommand]
    private void ParseM3u()
    {
        var text = M3uText?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            StatusText = "请先把 .m3u 内容贴到上方文本框";
            return;
        }

        try
        {
            int found = 0, missing = 0, audio = 0;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().Trim('\r');
                if (line.Length == 0 || line.StartsWith("#")) continue;

                var path = line;
                if (File.Exists(path))
                {
                    var ext = Path.GetExtension(path);
                    if (AudioExtensions.Contains(ext)) audio++;
                    var info = new FileInfo(path);
                    Tasks.Add(new ImportTaskItem
                    {
                        SourcePath = path,
                        DisplayName = info.Name,
                        SizeBytes = info.Length,
                        StatusText = "已发现",
                    });
                    found++;
                }
                else
                {
                    missing++;
                }
            }
            StatusText = $"解析完成：{found} 个文件存在（{audio} 个音频），{missing} 个不存在";
        }
        catch (Exception ex)
        {
            StatusText = $"解析失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ApplyImportedAsync()
    {
        if (Tasks.Count == 0) return;
        if (IsBusy || _library.IsScanning) return;

        StatusText = $"扫描 {Tasks.Count} 个文件所在的目录…";
        try
        {
            // 简化策略：把每个文件所在目录加入扫描列表，再触发一次系统扫描
            var extraDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in Tasks)
            {
                var dir = Path.GetDirectoryName(t.SourcePath);
                if (!string.IsNullOrEmpty(dir)) extraDirs.Add(dir);
            }
            foreach (var d in extraDirs)
                if (!_library.UserExtraScanDirs.Contains(d))
                    _library.UserExtraScanDirs.Add(d);

            await _library.ScanCommand.ExecuteAsync(null);

            foreach (var t in Tasks) t.StatusText = "已入库";
            StatusText = "已全部入库，到「我的音乐」查看";
        }
        catch (Exception ex)
        {
            StatusText = $"入库失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearTasks() => Tasks.Clear();

    /// <summary>支持的音频扩展名。</summary>
    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma"
    };

    #endregion
}