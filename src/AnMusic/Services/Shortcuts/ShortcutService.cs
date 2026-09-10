using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using AnMusic.Services.Settings;

namespace AnMusic.Services.Shortcuts;

/// <summary>
/// 快捷键服务：维护「动作 → 按键」绑定（持久化到 settings.json），
/// 负责应用内按键匹配，以及可选的系统级全局热键注册（RegisterHotKey）。
/// </summary>
public sealed class ShortcutService
{
    private readonly UserSettingsService _settingsService;

    /// <summary>解析后的应用内绑定（规范按键串 → 动作 Id）。</summary>
    private readonly Dictionary<string, string> _resolved = [];

    /// <summary>已注册的全局热键：动作 Id → RegisterHotKey 的整数 id。</summary>
    private readonly Dictionary<string, int> _registeredGlobals = [];

    private readonly Dictionary<int, string> _globalIdToAction = [];

    private IntPtr _globalHostHandle;
    private bool _hookInstalled;
    private int _nextGlobalId = 0xA100;

    public ShortcutService(UserSettingsService settingsService)
    {
        _settingsService = settingsService;
        EnsureDefaults();
        Rebuild();
    }

    /// <summary>
    /// 补齐/修正绑定表：
    /// 1) 新增动作没有条目 → 写入默认按键；
    /// 2) 已存按键串无法解析（历史遗留写法）→ 回落到默认按键。
    /// </summary>
    private void EnsureDefaults()
    {
        var changed = false;
        _settingsService.Update(s =>
        {
            s.ShortcutBindings ??= [];
            foreach (var action in ShortcutActions.All)
            {
                if (!s.ShortcutBindings.TryGetValue(action.Id, out var gesture))
                {
                    s.ShortcutBindings[action.Id] = action.DefaultGesture;
                    changed = true;
                    continue;
                }

                // 空串 = 用户主动清除，保留；非空但解析不了则视为脏数据，回落默认
                if (!string.IsNullOrWhiteSpace(gesture) && !ShortcutKeys.TryParse(gesture, out _, out _))
                {
                    s.ShortcutBindings[action.Id] = action.DefaultGesture;
                    changed = true;
                }
            }
        });
        if (!changed) _settingsService.Save();
    }

    /// <summary>绑定发生变化（改键/重置/全局开关）时触发；UI 据此刷新显示。</summary>
    public event Action? BindingsChanged;

    /// <summary>动作被触发时（应用内或全局热键）携带动作 Id 触发。</summary>
    public event Action<string>? Invoked;

    /// <summary>设置页正在等待用户按下新按键（此期间的按键不执行动作）。</summary>
    public bool IsCapturing { get; set; }

    /// <summary>全局热键注册失败的动作 Id（通常被其他程序占用）。</summary>
    public IReadOnlySet<string> FailedGlobalActions => _failedGlobals;

    private readonly HashSet<string> _failedGlobals = [];

    /// <summary>快捷键总开关。</summary>
    public bool Enabled => _settingsService.Settings.ShortcutsEnabled;

    #region 绑定读写

    private Dictionary<string, string> Bindings =>
        _settingsService.Settings.ShortcutBindings ??= ShortcutActions.DefaultBindings();

    /// <summary>当前按键（规范串）；返回空串表示该项未绑定（已清除）。</summary>
    public string GetGesture(string actionId)
        => Bindings.TryGetValue(actionId, out var g) ? g ?? "" : "";

    /// <summary>是否全局生效。</summary>
    public bool IsGlobal(string actionId)
        => _settingsService.Settings.GlobalShortcutIds?.Contains(actionId) == true;

    /// <summary>设置按键；gesture 为空串表示清除绑定。</summary>
    public void SetGesture(string actionId, string gesture)
    {
        _settingsService.Update(s =>
        {
            s.ShortcutBindings ??= ShortcutActions.DefaultBindings();
            s.ShortcutBindings[actionId] = gesture ?? "";
        });
        Rebuild();
    }

    /// <summary>设置/取消全局生效（全局需含修饰键，否则注册会失败）。</summary>
    public void SetGlobal(string actionId, bool global)
    {
        _settingsService.Update(s =>
        {
            s.GlobalShortcutIds ??= [];
            if (global)
            {
                if (!s.GlobalShortcutIds.Contains(actionId)) s.GlobalShortcutIds.Add(actionId);
            }
            else s.GlobalShortcutIds.Remove(actionId);
        });
        RefreshGlobalHotkeys();
        BindingsChanged?.Invoke();
    }

    /// <summary>全部恢复默认。</summary>
    public void ResetAll()
    {
        _settingsService.Update(s =>
        {
            s.ShortcutBindings = ShortcutActions.DefaultBindings();
            s.GlobalShortcutIds = [];
        });
        Rebuild();
        RefreshGlobalHotkeys();
        BindingsChanged?.Invoke();
    }

    /// <summary>单项恢复默认。</summary>
    public void ResetOne(string actionId)
    {
        var def = ShortcutActions.All.FirstOrDefault(a => a.Id == actionId);
        if (def is null) return;
        SetGesture(actionId, def.DefaultGesture);
    }

    /// <summary>开关总开关。</summary>
    public void SetEnabled(bool enabled)
    {
        _settingsService.Update(s => s.ShortcutsEnabled = enabled);
        if (enabled) RefreshGlobalHotkeys();
        else UnregisterAllGlobals();
        BindingsChanged?.Invoke();
    }

    /// <summary>该按键串是否已被其他动作占用；返回占用者名称，未被占用返回 null。</summary>
    public string? FindConflict(string actionId, string gesture)
    {
        if (string.IsNullOrEmpty(gesture)) return null;
        foreach (var (id, g) in Bindings)
        {
            if (id == actionId) continue;
            if (string.IsNullOrEmpty(g)) continue;
            if (string.Equals(g, gesture, StringComparison.OrdinalIgnoreCase))
                return ShortcutActions.All.FirstOrDefault(a => a.Id == id)?.Name ?? id;
        }
        return null;
    }

    /// <summary>重建解析表（按键串 → 动作）。</summary>
    private void Rebuild()
    {
        _resolved.Clear();
        foreach (var (id, gesture) in Bindings)
        {
            if (string.IsNullOrWhiteSpace(gesture)) continue;
            _resolved[ShortcutKeys.Normalize(gesture)] = id;
        }
    }

    #endregion

    #region 应用内匹配

    /// <summary>
    /// 尝试把一次按键匹配到动作。
    /// 文本输入框聚焦时，不带 Ctrl/Alt/Win 的组合（含仅 Shift）一律放行给输入框，
    /// 避免空格、方向键、Shift+方向键等正常编辑操作被吃掉。
    /// </summary>
    public bool TryMatch(Key key, ModifierKeys modifiers, bool isTextInputFocused, out string? actionId)
    {
        actionId = null;
        if (!Enabled || IsCapturing) return false;
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return false;

        if (isTextInputFocused &&
            (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == ModifierKeys.None)
            return false;

        // Ctrl+A 之类系统/控件自带组合不抢：仅当用户显式绑定了带修饰键的组合时才处理
        var gesture = ShortcutKeys.Build(key, modifiers);
        if (_resolved.TryGetValue(gesture, out var id))
        {
            actionId = id;
            return true;
        }
        return false;
    }

    /// <summary>直接触发一个动作（供全局热键/代码调用）。</summary>
    public void Invoke(string actionId)
    {
        if (!Enabled) return;
        Invoked?.Invoke(actionId);
    }

    #endregion

    #region 全局热键（RegisterHotKey）

    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>挂上承载全局热键的窗口句柄（主窗口句柄即可，窗口隐藏时依然有效）。</summary>
    public void AttachGlobalHost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        _globalHostHandle = hwnd;
        if (!_hookInstalled && HwndSource.FromHwnd(hwnd) is { } source)
        {
            source.AddHook(WndProc);
            _hookInstalled = true;
        }
        RefreshGlobalHotkeys();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            var id = wParam.ToInt32();
            if (_globalIdToAction.TryGetValue(id, out var actionId))
            {
                Invoke(actionId);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>按当前设置重新注册全部全局热键（先全部注销，避免残留）。</summary>
    public void RefreshGlobalHotkeys()
    {
        UnregisterAllGlobals();
        _failedGlobals.Clear();
        if (_globalHostHandle == IntPtr.Zero || !Enabled) return;

        foreach (var action in ShortcutActions.All)
        {
            if (!IsGlobal(action.Id)) continue;
            var gesture = GetGesture(action.Id);
            if (string.IsNullOrWhiteSpace(gesture)) continue;
            if (!ShortcutKeys.TryParse(gesture, out var key, out var modifiers)) continue;
            if (modifiers == ModifierKeys.None)
            {
                // 无修饰键的全局热键会抢走整个系统的按键，直接判定失败并提示
                _failedGlobals.Add(action.Id);
                continue;
            }
            if (key == Key.None) continue;

            var id = _nextGlobalId++;
            if (RegisterHotKey(_globalHostHandle, id, ToWin32Modifiers(modifiers) | ModNoRepeat,
                    (uint)KeyInterop.VirtualKeyFromKey(key)))
            {
                _registeredGlobals[action.Id] = id;
                _globalIdToAction[id] = action.Id;
            }
            else
            {
                _failedGlobals.Add(action.Id); // 通常是被其他程序占用
            }
        }
    }

    private void UnregisterAllGlobals()
    {
        if (_globalHostHandle != IntPtr.Zero)
        {
            foreach (var id in _registeredGlobals.Values)
            {
                try { UnregisterHotKey(_globalHostHandle, id); } catch { /* 已失效忽略 */ }
            }
        }
        _registeredGlobals.Clear();
        _globalIdToAction.Clear();
    }

    private static uint ToWin32Modifiers(ModifierKeys modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= ModWin;
        return result;
    }

    #endregion
}
