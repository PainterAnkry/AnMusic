using System.Windows.Input;

namespace AnMusic.Services.Shortcuts;

/// <summary>
/// 按键串与 Key/ModifierKeys 的相互转换。
/// 规范串格式：修饰键按 Ctrl+Alt+Shift+Win 顺序，最后是 Key 枚举名，例如 "Ctrl+Shift+L"、"Space"。
/// </summary>
public static class ShortcutKeys
{
    /// <summary>由按键与修饰键生成规范串。</summary>
    public static string Build(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>解析规范串；解析失败返回 false。</summary>
    public static bool TryParse(string? gesture, out Key key, out ModifierKeys modifiers)
    {
        key = Key.None;
        modifiers = ModifierKeys.None;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        var tokens = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return false;

        for (var i = 0; i < tokens.Length - 1; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win" or "windows": modifiers |= ModifierKeys.Windows; break;
                default: return false; // 未知修饰键
            }
        }

        if (!Enum.TryParse(tokens[^1], ignoreCase: true, out key) || key == Key.None) return false;
        return true;
    }

    /// <summary>归一化：把任意写法统一成规范串（解析失败时原样返回）。</summary>
    public static string Normalize(string gesture)
        => TryParse(gesture, out var key, out var modifiers) ? Build(key, modifiers) : gesture.Trim();

    /// <summary>用于界面展示的友好文本（空格 / ← / 回车 等）。</summary>
    public static string Display(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return "未设置";
        if (!TryParse(gesture, out var key, out var modifiers)) return gesture;

        var text = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) text.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) text.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) text.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) text.Add("Win");
        text.Add(KeyName(key));
        return string.Join("+", text);
    }

    /// <summary>单个按键的展示名。</summary>
    public static string KeyName(Key key) => key switch
    {
        Key.Space => "空格",
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Return => "回车",
        Key.Escape => "Esc",
        Key.Back => "退格",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Tab => "Tab",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "小键盘" + (int)(key - Key.NumPad0),
        _ => key.ToString()
    };

    /// <summary>是否为纯修饰键（捕获时需忽略，等待真正的按键）。</summary>
    public static bool IsModifierKey(Key key) => key
        is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None;
}
