using System.Linq;
using System.Windows.Input;
using AnMusic.Services.Shortcuts;

namespace AnMusic.Tests;

/// <summary>
/// 快捷键按键串的解析 / 规范化 / 展示，以及默认键位的合法性。
/// （这类问题靠肉眼很难发现：默认键串写错会导致快捷键静默失效。）
/// </summary>
public class ShortcutKeysTests
{
    [Theory]
    [InlineData("Space", Key.Space, ModifierKeys.None)]
    [InlineData("Ctrl+L", Key.L, ModifierKeys.Control)]
    [InlineData("Ctrl+Shift+L", Key.L, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData("Alt+Left", Key.Left, ModifierKeys.Alt)]
    [InlineData("Ctrl+OemComma", Key.OemComma, ModifierKeys.Control)]
    [InlineData("Ctrl+Alt+Win+Shift+F5", Key.F5,
        ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows | ModifierKeys.Shift)]
    public void TryParse_识别合法键串(string gesture, Key expectedKey, ModifierKeys expectedModifiers)
    {
        Assert.True(ShortcutKeys.TryParse(gesture, out var key, out var modifiers), gesture);
        Assert.Equal(expectedKey, key);
        Assert.Equal(expectedModifiers, modifiers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Comma")]        // 逗号必须写 OemComma
    [InlineData("Ctrl+,")]       // 历史 bug：这种写法解析不出按键
    [InlineData("Meta+K")]       // 不支持的修饰键
    public void TryParse_拒绝非法键串(string gesture)
        => Assert.False(ShortcutKeys.TryParse(gesture, out _, out _));

    [Fact]
    public void Build_按固定修饰键顺序生成规范串()
    {
        var gesture = ShortcutKeys.Build(Key.L, ModifierKeys.Shift | ModifierKeys.Control);
        Assert.Equal("Ctrl+Shift+L", gesture);
    }

    [Theory]
    [InlineData("ctrl+l", "Ctrl+L")]
    [InlineData("CONTROL+L", "Ctrl+L")]
    [InlineData("Shift+ctrl+L", "Ctrl+Shift+L")]
    public void Normalize_统一大小写与顺序(string input, string expected)
        => Assert.Equal(expected, ShortcutKeys.Normalize(input));

    [Theory]
    [InlineData("Space", "空格")]
    [InlineData("Ctrl+Left", "Ctrl+←")]
    [InlineData("Ctrl+OemComma", "Ctrl+,")]
    [InlineData("Shift+Right", "Shift+→")]
    [InlineData("Return", "回车")]
    [InlineData("Ctrl+D1", "Ctrl+1")]
    public void Display_给出可读文本(string gesture, string expected)
        => Assert.Equal(expected, ShortcutKeys.Display(gesture));

    [Fact]
    public void Display_未设置时给提示()
    {
        Assert.Equal("未设置", ShortcutKeys.Display(""));
        Assert.Equal("未设置", ShortcutKeys.Display(null));
    }

    [Theory]
    [InlineData(Key.LeftCtrl, true)]
    [InlineData(Key.RightAlt, true)]
    [InlineData(Key.LWin, true)]
    [InlineData(Key.None, true)]
    [InlineData(Key.Space, false)]
    [InlineData(Key.A, false)]
    public void IsModifierKey_区分修饰键(Key key, bool expected)
        => Assert.Equal(expected, ShortcutKeys.IsModifierKey(key));
}

/// <summary>默认键位表本身要能通过解析，且互不冲突。</summary>
public class ShortcutActionsTests
{
    [Fact]
    public void 所有默认键位都能被解析()
    {
        foreach (var action in ShortcutActions.All)
        {
            Assert.True(ShortcutKeys.TryParse(action.DefaultGesture, out var key, out _),
                $"{action.Id} 的默认键位无法解析：{action.DefaultGesture}");
            Assert.NotEqual(Key.None, key);
        }
    }

    [Fact]
    public void 默认键位不重复()
    {
        var gestures = ShortcutActions.All
            .Select(a => ShortcutKeys.Normalize(a.DefaultGesture))
            .ToList();
        Assert.Equal(gestures.Count, gestures.Distinct().Count());
    }

    [Fact]
    public void 动作Id不重复且都有名称说明()
    {
        Assert.Equal(ShortcutActions.All.Count, ShortcutActions.All.Select(a => a.Id).Distinct().Count());
        Assert.All(ShortcutActions.All, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
            Assert.False(string.IsNullOrWhiteSpace(a.Tip));
        });
    }

    [Fact]
    public void DefaultBindings_覆盖全部动作()
    {
        var bindings = ShortcutActions.DefaultBindings();
        Assert.Equal(ShortcutActions.All.Count, bindings.Count);
        Assert.All(ShortcutActions.All, a => Assert.True(bindings.ContainsKey(a.Id)));
    }
}
