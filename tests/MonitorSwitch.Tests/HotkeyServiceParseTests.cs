using Xunit;
using MonitorSwitch.Interop;
using MonitorSwitch.Services;

namespace MonitorSwitch.Tests;

public class HotkeyServiceParseTests
{
    [Theory]
    [InlineData("Ctrl+Alt+1", 0x31u)]
    [InlineData("ctrl+alt+a", 0x41u)]
    [InlineData("Win+Shift+F5", 0x74u)]
    [InlineData("Control+F24", 0x87u)]
    [InlineData("Alt+NumPad1", 0x61u)]
    [InlineData("Ctrl+Space", 0x20u)]
    [InlineData("Ctrl+Semicolon", 0xBAu)]
    [InlineData("Win+Equal", 0xBBu)]
    [InlineData("Ctrl+Comma", 0xBCu)]
    [InlineData("Alt+Minus", 0xBDu)]
    [InlineData("Shift+Period", 0xBEu)]
    [InlineData("Ctrl+Slash", 0xBFu)]
    [InlineData("Win+Backquote", 0xC0u)]
    [InlineData("Ctrl+LeftBracket", 0xDBu)]
    [InlineData("Ctrl+Backslash", 0xDCu)]
    [InlineData("Ctrl+RightBracket", 0xDDu)]
    [InlineData("Alt+Quote", 0xDEu)]
    public void ValidCombosParse(string text, uint expectedVk)
    {
        Assert.True(HotkeyService.TryParse(text, out var mods, out var vk));
        Assert.Equal(expectedVk, vk);
        Assert.NotEqual(0u, mods);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]           // 无修饰键
    [InlineData("Ctrl")]        // 只有修饰键
    [InlineData("Ctrl+")]       // 空键位
    [InlineData("Hyper+1")]     // 未知修饰键
    [InlineData("Ctrl+F25")]    // F 键越界
    [InlineData("Ctrl+F0")]
    [InlineData("Ctrl+FF")]
    [InlineData("Ctrl+中")]      // 非 ASCII
    public void InvalidCombosRejected(string? text)
    {
        Assert.False(HotkeyService.TryParse(text, out _, out _));
    }

    [Fact]
    public void ModifiersAccumulate()
    {
        Assert.True(HotkeyService.TryParse("Ctrl+Alt+Shift+Win+1", out var mods, out _));
        Assert.Equal(ShellNative.MOD_CONTROL | ShellNative.MOD_ALT | ShellNative.MOD_SHIFT | ShellNative.MOD_WIN, mods);
    }
}
