using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using MonitorSwitch.Interop;
using MonitorSwitch.Models;
using System.Windows.Input;
using MonitorSwitch.Services;
using MonitorSwitch.ViewModels;
using Wpf.Ui.Controls;

namespace MonitorSwitch.Views;

public partial class TrayFlyoutWindow : FluentWindow
{
    private bool _busy;
    private bool _opening;
    private bool _dragging;
    private int _fadeGeneration;

    public TrayFlyoutWindow()
    {
        InitializeComponent();
        // 拖动期间窗口会被短暂夺焦，Deactivated 会误判成“点到别处”把面板关掉
        DragHandle.MouseLeftButtonDown += OnDragHandleMouseDown;
        Deactivated += (_, _) =>
        {
            if (_opening || _busy || _dragging) return;
            HideWithFade();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                HideWithFade();
                e.Handled = true;
            }
        };
        ContentRendered += (_, _) => PlaceNearTray();
    }

    /// <summary>
    /// 拖动顶部标题区移动面板。
    ///
    /// 为什么用手写 DragMove 而不是 WindowChrome.CaptionHeight：
    /// 该窗口是 FluentWindow + ExtendsContentIntoTitleBar=True，WPF-UI 自己的
    /// 模板已经把客户区当标题栏处理，再叠一层 WindowChrome 不会生效（实测拖动无反应）。
    /// DragMove 走的是 Win32 WM_NCLBUTTONDOWN(HTCAPTION)，稳定可靠。
    ///
    /// 注意 _dragging 的复位时机：DragMove 是阻塞调用，拖动过程中窗口会失去激活状态，
    /// Deactivated 会在此期间触发。如果一返回就立即复位标志，Deactivated 会排在
    /// 这次事件之后才处理，看到的就是“已复位”，于是把面板关掉（实测拖一下就消失）。
    /// 因此改为在输入队列空闲后再复位，确保那次 Deactivated 先被跳过。
    /// </summary>
    private void OnDragHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        double startLeft = Left, startTop = Top;
        _dragging = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标在 DragMove 前就松开时会抛异常，忽略即可
        }
        finally
        {
            e.Handled = true;
            // 让排队中的 Deactivated 先看到 _dragging == true 而被跳过，之后再复位
            Dispatcher.BeginInvoke(new Action(() => _dragging = false),
                System.Windows.Threading.DispatcherPriority.Input);

            // 只有位置真的变了才记录，避免单击标题栏空白处也覆盖记忆位置
            if (Math.Abs(Left - startLeft) > 0.5 || Math.Abs(Top - startTop) > 0.5)
                SavePosition();
        }
    }

    /// <summary>
    /// 把当前窗口左上角写回设置（存物理像素，便于不同 DPI 的显示器间复用）。
    /// </summary>
    private void SavePosition()
    {
        try
        {
            if (!IsLoaded) return;
            double scale = GetDpiScale();
            SettingsService.Instance.Update(s =>
            {
                s.FlyoutLeft = Left * scale;
                s.FlyoutTop = Top * scale;
            });
        }
        catch (Exception ex)
        {
            AppPaths.Log("保存托盘面板位置失败: " + ex.Message);
        }
    }

    /// <summary>当前窗口所在显示器的 DPI 缩放（DIP → 物理像素）。</summary>
    private double GetDpiScale()
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        return Math.Max(0.5, dpi.DpiScaleX);
    }

    public void Open()
    {
        // 上一次淡出可能还在进行中：作废它的完成回调，否则它会在 Show() 之后把窗口重新藏起来，
        // 窗口就停在了屏幕外的初始坐标上（表现为面板“打不开”）。
        _fadeGeneration++;
        BeginAnimation(OpacityProperty, null);
        _opening = true;

        RefreshContent();

        // 先放到屏幕外，避免渲染前闪现在 (0,0)
        Left = -10000;
        Top = -10000;
        Opacity = 0;
        Show();

        // 尺寸确定后立即定位，再淡入；不依赖 ContentRendered（它只在首次渲染时触发一次）
        UpdateLayout();
        PlaceNearTray();
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(OpacityProperty, fadeIn);

        Activate();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // 重新定位：SizeToContent 的高度要等一次完整布局才稳定
            PlaceNearTray();
            Activate();
            _opening = false;
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    public void HideWithFade()
    {
        if (!IsVisible) return;
        int gen = ++_fadeGeneration;
        var fadeOut = new DoubleAnimation(Opacity <= 0 ? 1 : Opacity, 0, TimeSpan.FromMilliseconds(110));
        fadeOut.Completed += (_, _) =>
        {
            // 期间若又被打开，本次淡出作废
            if (gen != _fadeGeneration) return;
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    private void RefreshContent()
    {
        try
        {
            var cur = DisplayService.Instance.GetDisplays();
            var vms = ProfileStore.Instance.Profiles
                .Select(p => new ProfileCardVM(p, DisplayService.Instance.MatchesCurrent(p, cur)))
                .ToList();
            ProfileList.ItemsSource = vms;

            var empty = vms.Count == 0;
            EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            ProfileList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

            // 单屏时投影模式没有意义
            var showTopology = cur.Count > 1;
            TopologyRow.Visibility = showTopology ? Visibility.Visible : Visibility.Collapsed;
            TopologyTitle.Visibility = showTopology ? Visibility.Visible : Visibility.Collapsed;

            VersionText.Text = "v" + (System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3) ?? "1.0.0");
        }
        catch (Exception ex)
        {
            AppPaths.Log("托盘面板刷新失败: " + ex);
            ShowError("读取显示器信息失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 摆放面板位置。
    /// 优先使用上次拖动留下的记忆位置；若该位置已经不在任何显示器内
    /// （拔掉显示器、改了分辨率/缩放后可能发生），回退到跟随光标。
    /// </summary>
    private void PlaceNearTray()
    {
        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : 400;

        if (TryRestoreRememberedPosition(width, height)) return;

        if (!ShellNative.GetCursorPos(out var cursor)) return;

        var mi = new GdiNative.MONITORINFOEXW();
        mi.CbSize = System.Runtime.InteropServices.Marshal.SizeOf<GdiNative.MONITORINFOEXW>();
        var hmon = GdiNative.MonitorFromPoint(new GdiNative.POINT { X = cursor.X, Y = cursor.Y }, GdiNative.MONITOR_DEFAULTTONEAREST);

        // 工作区已是物理像素，而 WPF 的 Left/Top 使用 DIP：先按该屏幕 DPI 换算
        uint dpi = 96;
        if (GdiNative.GetMonitorInfoW(hmon, ref mi))
        {
            try { GdiNative.GetDpiForMonitor(hmon, 0, out dpi, out _); } catch { }
        }
        double scale = Math.Max(0.5, dpi / 96.0);

        double workLeft = mi.RcWork.Left / scale;
        double workRight = mi.RcWork.Right / scale;
        double workTop = mi.RcWork.Top / scale;
        double workBottom = mi.RcWork.Bottom / scale;

        // 水平：以托盘图标（光标）为中心，但限制在工作区内
        double left = cursor.X / scale - width / 2;
        left = Math.Clamp(left, workLeft + 8, Math.Max(workLeft + 8, workRight - width - 8));

        // 垂直：靠任务栏一侧。光标在工作区下方 → 面板在光标上方；否则在上方任务栏 → 面板在光标下方
        double top;
        if (cursor.Y / scale > workBottom)
            top = workBottom - height - 10;
        else
            top = workTop + 10;
        top = Math.Clamp(top, workTop + 8, Math.Max(workTop + 8, workBottom - height - 8));

        Left = left;
        Top = top;
    }

    /// <summary>
    /// 尝试把窗口放回记忆位置。
    ///
    /// 记忆值以物理像素保存，这里换算成 DIP 前要按“目标位置所在显示器”的 DPI，
    /// 否则在多显示器混合缩放下会偏出去。校验规则：
    /// 只要窗口左上角附近有一点落在某个显示器的工作区内就算有效，
    /// 避免用整块矩形判断导致窗口贴着边缘时被误判为无效。
    /// </summary>
    private bool TryRestoreRememberedPosition(double width, double height)
    {
        var settings = SettingsService.Instance.Current;
        if (settings.FlyoutLeft is not double savedLeft || settings.FlyoutTop is not double savedTop)
            return false;

        // 找到记忆位置所在的显示器，用它的 DPI 做换算
        var target = new GdiNative.POINT
        {
            X = (int)Math.Round(savedLeft + 20),
            Y = (int)Math.Round(savedTop + 20),
        };
        var hmon = GdiNative.MonitorFromPoint(target, GdiNative.MONITOR_DEFAULTTONULL);
        if (hmon == IntPtr.Zero)
        {
            // 记忆位置已不在任何显示器上（例如外接屏被拔掉），丢弃它
            AppPaths.Log("托盘面板记忆位置已失效，回退到光标定位");
            ClearRememberedPosition();
            return false;
        }

        uint dpi = 96;
        try { GdiNative.GetDpiForMonitor(hmon, 0, out dpi, out _); } catch { }
        double scale = Math.Max(0.5, dpi / 96.0);

        double left = savedLeft / scale;
        double top = savedTop / scale;

        // 防止记忆值把面板推出可见区域
        var mi = new GdiNative.MONITORINFOEXW();
        mi.CbSize = System.Runtime.InteropServices.Marshal.SizeOf<GdiNative.MONITORINFOEXW>();
        if (GdiNative.GetMonitorInfoW(hmon, ref mi))
        {
            double workLeft = mi.RcWork.Left / scale;
            double workRight = mi.RcWork.Right / scale;
            double workTop = mi.RcWork.Top / scale;
            double workBottom = mi.RcWork.Bottom / scale;
            left = Math.Clamp(left, workLeft, Math.Max(workLeft, workRight - width));
            top = Math.Clamp(top, workTop, Math.Max(workTop, workBottom - height));
        }

        Left = left;
        Top = top;
        return true;
    }

    private static void ClearRememberedPosition()
    {
        try
        {
            SettingsService.Instance.Update(s =>
            {
                s.FlyoutLeft = null;
                s.FlyoutTop = null;
            });
        }
        catch { }
    }

    // ---------------------------------------------------------------- 操作

    private async void OnApplyProfileRow(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        if (sender is not Border { Tag: ProfileCardVM vm }) return;

        _busy = true;
        try
        {
            var result = await ApplyGuard.Instance.ApplyAsync(vm.Profile);
            if (!result.Success)
            {
                AppPaths.Log("托盘应用失败: " + result.ErrorMessage);
                ShowError("应用“" + vm.Profile.Name + "”失败：" + result.ErrorMessage);
                return;
            }
            HideWithFade();
        }
        catch (Exception ex)
        {
            AppPaths.Log("托盘应用异常: " + ex);
            ShowError("应用失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = DisplayService.Instance.CaptureCurrent();
            if (profile.Monitors.Count == 0)
            {
                ShowError("未检测到显示器，无法创建模板。");
                return;
            }
            int i = ProfileStore.Instance.Profiles.Count + 1;
            while (ProfileStore.Instance.Profiles.Any(p => p.Name == $"配置 {i}")) i++;
            profile.Name = $"配置 {i}";
            ProfileStore.Instance.Add(profile);
            RefreshContent();
        }
        catch (Exception ex)
        {
            AppPaths.Log("托盘新建模板失败: " + ex);
            ShowError("创建模板失败：" + ex.Message);
        }
    }

    private void OnOpenMain(object sender, RoutedEventArgs e)
    {
        HideWithFade();
        ((App)Application.Current).ShowMainWindow();
    }

    private async void OnTopology(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Wpf.Ui.Controls.Button { Tag: string tag }) return;

        _busy = true;
        try
        {
            var topology = tag switch
            {
                "InternalOnly" => QuickTopology.InternalOnly,
                "Clone" => QuickTopology.Clone,
                "ExternalOnly" => QuickTopology.ExternalOnly,
                _ => QuickTopology.Extend,
            };
            var result = await ApplyGuard.Instance.ApplyTopologyAsync(topology);
            if (!result.Success)
            {
                AppPaths.Log("托盘投影切换失败: " + result.ErrorMessage);
                ShowError("切换投影模式失败：" + result.ErrorMessage);
                return;
            }
            HideWithFade();
        }
        catch (Exception ex)
        {
            AppPaths.Log("托盘投影切换异常: " + ex);
            ShowError("切换失败：" + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowError(string message)
    {
        ErrorBar.Title = "操作失败";
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}

