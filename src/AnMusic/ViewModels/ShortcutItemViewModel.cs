using System.Windows.Input;
using AnMusic.Services.Shortcuts;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnMusic.ViewModels;

/// <summary>
/// 设置页「快捷键」列表的一行：动作名 + 当前按键 + 全局开关 + 改键状态。
/// </summary>
public partial class ShortcutItemViewModel : ObservableObject
{
    private readonly ShortcutService _service;
    private readonly ShortcutAction _action;

    public ShortcutItemViewModel(ShortcutService service, ShortcutAction action)
    {
        _service = service;
        _action = action;
        _isGlobal = service.IsGlobal(action.Id);
        Refresh();
    }

    public string ActionId => _action.Id;
    public string Name => _action.Name;
    public string Tip => _action.Tip;

    /// <summary>当前按键的展示文本（未设置时显示「未设置」）。</summary>
    [ObservableProperty]
    private string _gestureText = "未设置";

    /// <summary>是否处于「等待按下新按键」的捕获状态。</summary>
    [ObservableProperty]
    private bool _isCapturing;

    /// <summary>当前绑定为空。</summary>
    [ObservableProperty]
    private bool _isUnset;

    /// <summary>异常提示（按键冲突 / 全局注册失败），空串表示正常。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    private string _warning = "";

    /// <summary>是否有异常提示（界面据此标红）。</summary>
    public bool HasWarning => !string.IsNullOrEmpty(Warning);

    [ObservableProperty]
    private bool _isGlobal;

    partial void OnIsGlobalChanged(bool value)
    {
        _service.SetGlobal(_action.Id, value);
        Refresh();
    }

    /// <summary>重新读取服务中的绑定并刷新展示（捕获中不覆盖提示文本）。</summary>
    public void Refresh()
    {
        if (IsCapturing)
        {
            Warning = "";
            return;
        }

        var gesture = _service.GetGesture(_action.Id);
        IsUnset = string.IsNullOrWhiteSpace(gesture);
        GestureText = IsUnset ? "未设置" : ShortcutKeys.Display(gesture);

        Warning = _service.FailedGlobalActions.Contains(_action.Id)
            ? "全局热键注册失败：可能已被其他程序占用"
            : "";
    }

    /// <summary>提交捕获到的新按键；返回提示文本（成功或失败原因）。</summary>
    public string ApplyCapture(Key key, ModifierKeys modifiers)
    {
        StopCapture();
        var gesture = ShortcutKeys.Build(key, modifiers);

        if (FindConflict(gesture) is { } owner)
        {
            Warning = $"与「{owner}」重复，未修改";
            GestureText = IsUnset ? "未设置" : ShortcutKeys.Display(_service.GetGesture(_action.Id));
            return Warning;
        }

        _service.SetGesture(_action.Id, gesture);
        Refresh();
        if (IsGlobal && modifiers == ModifierKeys.None)
            Warning = "全局生效需要包含 Ctrl / Alt / Shift 修饰键";

        return $"「{Name}」已设为 {ShortcutKeys.Display(gesture)}";
    }

    /// <summary>清除本项绑定。</summary>
    public string Clear()
    {
        StopCapture();
        _service.SetGesture(_action.Id, "");
        Refresh();
        return $"已清除「{Name}」的按键";
    }

    /// <summary>恢复本项默认按键。</summary>
    public string ResetDefault()
    {
        StopCapture();
        _service.ResetOne(_action.Id);
        Refresh();
        var owner = _service.GetGesture(_action.Id);
        if (!string.IsNullOrWhiteSpace(owner) && FindConflict(owner) is { } other)
        {
            Warning = $"默认按键与「{other}」重复，请手动改键";
            return Warning;
        }
        return $"「{Name}」已恢复默认：{ShortcutKeys.Display(_service.GetGesture(_action.Id))}";
    }

    /// <summary>捕获期间的临时提示。</summary>
    public void BeginCapture()
    {
        IsCapturing = true;
        GestureText = "按下新按键…";
        Warning = "";
    }

    /// <summary>结束捕获（取消或提交）并恢复文本。</summary>
    public void StopCapture()
    {
        IsCapturing = false;
        var gesture = _service.GetGesture(_action.Id);
        IsUnset = string.IsNullOrWhiteSpace(gesture);
        GestureText = IsUnset ? "未设置" : ShortcutKeys.Display(gesture);
    }

    /// <summary>该按键是否与其他动作冲突（返回冲突动作名）。</summary>
    private string? FindConflict(string gesture)
    {
        var typed = ShortcutKeys.TryParse(gesture, out var key, out var mods)
            ? ShortcutKeys.Build(key, mods)
            : gesture;

        foreach (var other in ShortcutActions.All)
        {
            if (other.Id == _action.Id) continue;
            var g = _service.GetGesture(other.Id);
            if (string.IsNullOrWhiteSpace(g)) continue;
            if (ShortcutKeys.Normalize(g) == typed) return other.Name;
        }
        return null;
    }
}
