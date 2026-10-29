using System.Windows;
using System.Windows.Input;

namespace MonitorSwitch.Controls;

/// <summary>
/// 点击后捕获下一次按键组合的快捷键输入框（如 Ctrl+Alt+1）。
/// </summary>
public delegate void HotkeyCapturedEventHandler(object sender, string? hotkey);

public sealed class HotkeyBox : Wpf.Ui.Controls.Button
{
    public event HotkeyCapturedEventHandler? HotkeyCaptured;

    private bool _capturing;

    public HotkeyBox()
    {
        MinWidth = 130;
        MinHeight = 24;
        Padding = new Thickness(8, 2, 8, 2);
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Click += OnCaptureClick;
        PreviewKeyDown += OnPreviewKeyDown;
        LostKeyboardFocus += OnLostKeyboardFocus;
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        _capturing = true;
        Content = "按下快捷键…";
        // 按钮本身能获得键盘焦点，但需要显式请求，否则收不到 PreviewKeyDown
        Focusable = true;
        Focus();
        Keyboard.Focus(this);
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_capturing) return;
        // 捕获期间失焦：恢复原显示，不提交
        _capturing = false;
        RestoreContent();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 仅按下修饰键时等待组合
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.None)
            return;

        if (key == Key.Escape)
        {
            _capturing = false;
            Content = "(无)";
            HotkeyCaptured?.Invoke(this, null);
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            _capturing = false;
            Content = "(无)";
            HotkeyCaptured?.Invoke(this, null);
            return;
        }

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            Content = "需包含 Ctrl/Alt/Shift/Win";
            return;
        }

        if (!TryKeyText(key, out var keyText))
        {
            Content = "不支持该按键";
            return;
        }

        var text = "";
        if (mods.HasFlag(ModifierKeys.Control)) text += "Ctrl+";
        if (mods.HasFlag(ModifierKeys.Alt)) text += "Alt+";
        if (mods.HasFlag(ModifierKeys.Shift)) text += "Shift+";
        if (mods.HasFlag(ModifierKeys.Windows)) text += "Win+";
        text += keyText;

        _capturing = false;
        Content = text;
        HotkeyCaptured?.Invoke(this, text);
    }

    private string? _lastHotkey;

    public void SetHotkey(string? hotkey)
    {
        _lastHotkey = hotkey;
        if (_capturing) return;
        Content = string.IsNullOrWhiteSpace(hotkey) ? "(无)" : hotkey;
    }

    private void RestoreContent()
    {
        Content = string.IsNullOrWhiteSpace(_lastHotkey) ? "(无)" : _lastHotkey;
    }

    private static bool TryKeyText(Key key, out string text)
    {
        // 数字键：主键盘 D0-D9 与数字小键盘 NumPad0-9
        if (key is >= Key.D0 and <= Key.D9)
        {
            text = ((int)(key - Key.D0)).ToString();
            return true;
        }
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            text = ((int)(key - Key.NumPad0)).ToString();
            return true;
        }
        if (key is >= Key.A and <= Key.Z)
        {
            text = key.ToString();
            return true;
        }
        if (key is >= Key.F1 and <= Key.F24)
        {
            text = key.ToString();
            return true;
        }
        text = "";
        return false;
    }
}
