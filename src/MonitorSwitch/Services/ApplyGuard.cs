using System.Windows;
using MonitorSwitch.I18n;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

/// <summary>
/// 应用配置后的“保留 / 还原”保护：15 秒内无确认则自动还原到之前的配置。
/// </summary>
public sealed class ApplyGuard
{
    public static ApplyGuard Instance { get; } = new();

    /// <summary>倒计时秒数（与 Windows 设置保持一致）。</summary>
    public const int RevertSeconds = 15;

    private RevertWindow? _window;
    private DisplayProfile? _previous;
    private bool _reverting;

    public async Task<ApplyResult> ApplyAsync(DisplayProfile profile)
    {
        // 捕获与“是否真的变了”的判断都是完整的 CCD 枚举（多屏慢驱动下上百毫秒），
        // 放到线程池，避免热键/托盘路径在 UI 线程上卡顿；ShowRevert 自行归队 UI 线程。
        var before = await Task.Run(CaptureSafe);
        var result = await Task.Run(() => DisplayService.Instance.ApplyProfile(profile));
        if (!result.Success) return result;

        await MaybeShowRevertAsync(before);
        return result;
    }

    /// <summary>切换投影模式（带还原保护）。</summary>
    public async Task<ApplyResult> ApplyTopologyAsync(QuickTopology topology)
    {
        var before = await Task.Run(CaptureSafe);
        var result = await Task.Run(() => DisplayService.Instance.ApplyTopology(topology));
        if (!result.Success) return result;

        await MaybeShowRevertAsync(before);
        return result;
    }

    private static DisplayProfile CaptureSafe()
    {
        try { return DisplayService.Instance.CaptureCurrent(); }
        catch (Exception ex)
        {
            AppPaths.Log("捕获当前配置失败: " + ex.Message);
            return new DisplayProfile();
        }
    }

    private async Task MaybeShowRevertAsync(DisplayProfile before)
    {
        if (!SettingsService.Instance.Current.RevertGuardEnabled) return;
        if (before.Monitors.Count == 0) return;

        // 配置没有实际变化时不需要打扰用户
        bool changed;
        try { changed = !await Task.Run(() => DisplayService.Instance.MatchesCurrent(before)); }
        catch { changed = true; }
        if (!changed) return;

        ShowRevert(before);
    }

    public void KeepChanges()
    {
        _previous = null;
        CloseWindow();
    }

    /// <summary>释放还原确认条窗口（关闭到托盘时调用，避免它继续占着 WPF 渲染资源）。</summary>
    public void ReleaseWindow() => CloseWindow();

    public void RevertNow()
    {
        if (_reverting) return;
        _reverting = true;
        try
        {
            var p = _previous;
            _previous = null;
            CloseWindow();
            if (p == null) return;

            // 还原可能触发 WM_DISPLAYCHANGE，放到线程池避免阻塞 UI
            _ = Task.Run(() =>
            {
                var r = DisplayService.Instance.ApplyProfile(p);
                if (!r.Success) AppPaths.Log("自动还原失败: " + r.ErrorMessage);
                else AppPaths.Log("已自动还原到应用前的配置");
            });
        }
        finally
        {
            _reverting = false;
        }
    }

    private void CloseWindow()
    {
        var w = _window;
        _window = null;
        try { w?.Close(); } catch { }
    }

    private void ShowRevert(DisplayProfile before)
    {
        // 关旧窗、记 _previous、建新窗必须在同一次 UI 线程调度内原子完成：
        // 并发 apply（CLI 中继 + 托盘）若交错，先建的窗口会成为孤儿，
        // 其倒计时到点会用“最后一份” _previous 触发错误的还原。
        var app = System.Windows.Application.Current;
        if (app is null) return;

        app.Dispatcher.Invoke(() =>
        {
            if (App.IsExiting) return;

            CloseWindow();
            _previous = before;

            // 还原确认条是 WPF 窗口，需要 WPF-UI 资源；托盘常驻期间这些资源是延迟加载的
            ThemeService.EnsureUiResources();
            ThemeService.Apply(SettingsService.Instance.Current.Theme);
            _window = new RevertWindow();
            _window.Show();
            _window.Activate();
        });
    }
}

/// <summary>屏幕底部居中的还原确认条。</summary>
public sealed class RevertWindow : FluentWindow
{
    private readonly DispatcherTimer _timer;
    private readonly System.Windows.Controls.TextBlock _countdown;
    private int _secondsLeft = ApplyGuard.RevertSeconds;
    private bool _finished;

    public RevertWindow()
    {
        Title = "MonitorSwitch";
        Width = 520;
        // WPF-UI 的 FluentWindow 默认 MinWidth/MinHeight 为 460/320，
        // 会让这条只有一行内容的确认条撑出大片空白，这里显式放开。
        MinWidth = 0;
        MinHeight = 0;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Acrylic;
        // FluentWindow 的模板（ClientAreaBorder + AdornerDecorator）会打断 WPF 的
        // ClearType 传播链，导致文字退化为灰度抗锯齿、看起来发虚。显式开启即可恢复。
        RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);

        var title = new System.Windows.Controls.TextBlock
        {
            Text = Strings.T("Revert.Title"),
            FontSize = 14,
            FontWeight = FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

        _countdown = new System.Windows.Controls.TextBlock
        {
            Text = Strings.F("Revert.Countdown", _secondsLeft),
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
        };
        _countdown.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        var keepBtn = new Wpf.Ui.Controls.Button
        {
            Content = Strings.T("Revert.Keep"),
            Appearance = ControlAppearance.Primary,
            MinWidth = 96,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var revertBtn = new Wpf.Ui.Controls.Button
        {
            Content = Strings.T("Revert.Revert"),
            MinWidth = 76,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        keepBtn.Click += (_, _) => Finish(ApplyGuard.Instance.KeepChanges);
        revertBtn.Click += (_, _) => Finish(ApplyGuard.Instance.RevertNow);

        var textCol = new StackPanel { Margin = new Thickness(6, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        textCol.Children.Add(title);
        textCol.Children.Add(_countdown);

        var grid = new Grid { Margin = new Thickness(20, 14, 16, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(textCol, 0);
        Grid.SetColumn(keepBtn, 1);
        Grid.SetColumn(revertBtn, 2);
        grid.Children.Add(textCol);
        grid.Children.Add(keepBtn);
        grid.Children.Add(revertBtn);

        Content = grid;

        // SizeToContent 时 ActualWidth 在 SourceInitialized 阶段还为 0，等布局完成再定位
        Loaded += (_, _) => PlaceBottomCenter();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            if (_secondsLeft <= 0)
            {
                Finish(ApplyGuard.Instance.RevertNow);
            }
            else
            {
                _countdown.Text = Strings.F("Revert.Countdown", _secondsLeft);
            }
        };
        _timer.Start();
    }

    private void Finish(Action action)
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        action();
    }

    private void PlaceBottomCenter()
    {
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        double width = ActualWidth > 0 ? ActualWidth : Width;
        Left = wa.Left + Math.Max(0, (wa.Width - width) / 2);
        Top = Math.Max(wa.Top, wa.Bottom - ActualHeight - 24);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}


