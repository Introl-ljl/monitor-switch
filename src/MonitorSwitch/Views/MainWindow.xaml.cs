using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorSwitch.Services;
using Wpf.Ui.Controls;

namespace MonitorSwitch.Views;

public partial class MainWindow : FluentWindow
{
    private bool _navigated;
    private Type? _lastPage;

    public MainWindow()
    {
        InitializeComponent();
        // XAML 里的 Title 只是设计期占位，实际文案随语言设置走
        Title = I18n.Strings.T("App.Title");
        RootNavigation.ItemInvoked += OnNavItemInvoked;
        // SelectionChanged 的参数类型是 RoutedEventArgs（WPF-UI 自定义事件），
        // 用 SelectionChangedEventArgs 订阅会静默失败。
        RootNavigation.SelectionChanged += OnNavSelectionChanged;
        Loaded += (_, _) =>
        {
            if (!_navigated)
            {
                _navigated = true;
                _lastPage = typeof(DisplayPage);
                RootNavigation.Navigate(typeof(DisplayPage));
            }
        };
        Closing += OnClosing;
    }

    /// <summary>
    /// WPF-UI 4.0.3 的 ItemInvoked 只提供 RoutedEventArgs，且 OriginalSource 可能不是
    /// 可视元素（键盘触发时为 null）。因此优先用 OriginalSource 向上找，
    /// 找不到就退回 sender.SelectedItem —— 点击导航项时选中项已经更新。
    /// </summary>
    private void OnNavItemInvoked(NavigationView sender, RoutedEventArgs args)
    {
        var item = args.OriginalSource is DependencyObject d ? FindAncestor<NavigationViewItem>(d) : null;
        item ??= sender.SelectedItem as NavigationViewItem;
        NavigateTo(item);
    }

    /// <summary>键盘 / 无障碍操作可能只触发 SelectionChanged，这里兜底。</summary>
    private void OnNavSelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is NavigationViewItem item) NavigateTo(item);
    }

    private void NavigateTo(NavigationViewItem? item)
    {
        var pageType = item?.TargetPageType;
        if (pageType == null) return;
        if (_lastPage == pageType) return;
        _lastPage = pageType;
        try
        {
            RootNavigation.Navigate(pageType);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"导航到 {pageType.Name} 失败: {ex.Message}");
        }
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            var parent = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
            if (parent == null && d is FrameworkElement fe) parent = fe.Parent;
            d = parent!;
        }
        return null;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 退出中，或 ReleaseMainWindow 正在执行“销毁窗口”：放行真正的 Close。
        // 若没有这条旁路，CloseToTray 时 e.Cancel 会把销毁流程自己发起的
        // Close() 也取消掉，窗口永远走不到 Closed，渲染管线内存无法归还。
        if (App.IsExiting || App.ReleasingWindow) return;

        if (SettingsService.Instance.Current.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            // 关到托盘后真正销毁窗口，把 WPF 渲染管线（D3D 驱动模块约 126MB）
            // 和控件模板占用的内存还给系统。否则内存会一直停在打开窗口时的高位，
            // 与“常驻托盘”这个使用场景的预期不符。
            App.ReleaseMainWindowStatic();
        }
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Focus();
        Topmost = true;
        Topmost = false;
    }
}
