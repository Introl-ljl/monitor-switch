using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using MonitorSwitch.Interop;
using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

/// <summary>在每块屏幕左上角短暂显示大号编号（同 Windows“标识”）。</summary>
public static class IdentifyService
{
    private static readonly List<Window> _active = new();
    private const double BoxSize = 170;
    private const int OverlayMs = 2200;

    public static void Identify(IEnumerable<LiveDisplay> displays)
    {
        var list = displays.ToList();
        var app = Application.Current;
        if (app is null) return;

        app.Dispatcher.Invoke(() =>
        {
            // 再次点击“标识”时先清掉上一轮，避免叠加
            foreach (var w in _active.ToList())
                Close(w);

            foreach (var d in list)
            {
                if (d.MonitorRect.Right <= d.MonitorRect.Left) continue;
                ShowOverlay(d);
            }
        });
    }

    /// <summary>关闭所有尚未消失的编号浮层（窗口关闭 / 退出时调用）。</summary>
    public static void CloseAll()
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() =>
        {
            foreach (var w in _active.ToList()) Close(w);
        });
    }

    private static void Close(Window w)
    {
        _active.Remove(w);
        try { w.Close(); } catch { }
    }

    private static void ShowOverlay(LiveDisplay d)
    {
        var text = new TextBlock
        {
            Text = d.Number > 0 ? d.Number.ToString() : "?",
            FontSize = 84,
            FontWeight = FontWeights.Normal,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var border = new Border
        {
            CornerRadius = new CornerRadius(18),
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x10, 0x10, 0x10)),
            Child = text,
        };
        var win = new Window
        {
            Content = border,
            Width = BoxSize,
            Height = BoxSize,
            // WPF 的 Window 默认 MinWidth/MinHeight 会覆盖 Width/Height，
            // 不显式清零会得到 460×320 的大白块（SetWindowPos 之后才被纠正，导致闪烁）。
            MinWidth = 0,
            MinHeight = 0,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Opacity = 0,
            // 显式给到左上角，避免以 (0,0) 闪现后再跳到目标屏幕
            Left = -10000,
            Top = -10000,
        };

        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            // 点击穿透 + 不激活 + 不出现在 Alt+Tab
            const int GWL_EXSTYLE = -20;
            const int WS_EX_TRANSPARENT = 0x20;
            const int WS_EX_TOOLWINDOW = 0x80;
            const int WS_EX_NOACTIVATE = 0x08000000;
            int ex = User32GetWindowLong(hwnd, GWL_EXSTYLE);
            User32SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            // 用显示器矩形（物理像素）定位。浮层尺寸保持 170×170 物理像素，
            // 不要按缩放放大——缩放只影响 DIP 与像素的换算，与浮层本身无关。
            const uint SWP_NOACTIVATE = 0x0010;
            const uint SWP_NOZORDER = 0x0004;
            User32SetWindowPos(hwnd, IntPtr.Zero,
                d.MonitorRect.Left + 48, d.MonitorRect.Top + 48,
                (int)BoxSize, (int)BoxSize,
                SWP_NOACTIVATE | SWP_NOZORDER);
        };

        _active.Add(win);
        win.Show();

        win.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(OverlayMs),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_active.Contains(win)) return;
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (_, _) => Close(win);
            win.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        };
        timer.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int User32GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int User32SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool User32SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

