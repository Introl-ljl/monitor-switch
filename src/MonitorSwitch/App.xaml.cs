using System.Windows;
using MonitorSwitch.Cli;
using MonitorSwitch.I18n;
using System.Windows.Controls;
using MonitorSwitch.Interop;
using MonitorSwitch.Models;
using MonitorSwitch.Services;
using MonitorSwitch.Views;

namespace MonitorSwitch;

public partial class App : Application
{
    private static bool _exiting;
    public static bool IsExiting => _exiting;

    private readonly SingleInstance _single = new();
    private readonly TrayService _tray = new();
    private MessageHwnd? _messages;
    private HotkeyService? _hotkeys;
    private IntPtr _trayIconHandle;
    private MainWindow? _mainWindow;
    private TrayFlyoutWindow? _flyout;
    private ContextMenu? _trayMenu;
    private CliPipeServer? _cliPipe;

    /// <summary>主窗口实例（关闭后由 MainWindow 自己置空，避免复用已关闭的窗口）。</summary>
    public MainWindow? MainWindowInstance
    {
        get => _mainWindow;
        set => _mainWindow = value;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            AppPaths.Log("UI 线程未处理异常: " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppPaths.Log("未处理异常: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppPaths.Log("未观察的任务异常: " + args.Exception);
            args.SetObserved();
        };

        // CLI 模式必须最先截获：不进单实例、不碰托盘，命令执行完立即退出。
        // 放在 TryBegin 之前，否则二次调用会误触发“唤起已有实例”。
        //
        // 执行放到独立线程：OnStartup 所在的主线程带着 WPF 的 Dispatcher 同步上下文，
        // 在这里同步等待含 await 的 CLI 命令（如 displays）会把 await 延续卡死在
        // 本线程上造成死锁。挪到普通线程后，await 延续在线程池恢复，直连模式下
        // OnUi 的 Dispatcher.Invoke 也能投递到仍在泵消息的主线程；最后用
        // Environment.Exit 带出退出码（托盘尚未初始化，无需走优雅关闭流程）。
        if (CliHost.IsCliInvocation(e.Args))
        {
            var cli = new Thread(() => Environment.Exit(CliHost.Run(e.Args)))
            {
                IsBackground = false,
            };
            cli.SetApartmentState(ApartmentState.MTA);
            cli.Start();
            return;
        }

        _single.ActivateRequested += ShowMainWindow;
        if (!_single.TryBegin())
        {
            Shutdown();
            return;
        }

        SettingsService.Instance.Load();
        ProfileStore.Instance.Load();
        // 语言必须先于任何界面文案生效（日志内容不参与本地化）
        I18n.Localization.Instance.Apply(SettingsService.Instance.Current.Language);
        ThemeService.Apply(SettingsService.Instance.Current.Theme);

        _messages = new MessageHwnd();
        _messages.MessageReceived += OnMessage;

        _hotkeys = new HotkeyService();
        _hotkeys.Attach(_messages.Handle);
        _hotkeys.HotkeyPressed += OnHotkeyPressed;
        _hotkeys.Refresh(ProfileStore.Instance.Profiles);
        ProfileStore.Instance.Changed += () => _hotkeys?.Refresh(ProfileStore.Instance.Profiles);
        SettingsService.Instance.SettingsChanged += OnSettingsChanged;

        _trayIconHandle = LoadTrayIcon();
        _tray.Init(_messages.Handle, _trayIconHandle, Strings.T("App.Title"));
        _tray.LeftClick += ToggleFlyout;
        _tray.RightClick += ShowTrayMenu;

        // CLI 管道服务：MonitorSwitch.exe <命令> 优先经它中继到本进程执行，
        // 以复用模板存储、全局快捷键与还原保护；应用未运行时 CLI 会直连执行。
        _cliPipe = new CliPipeServer(req => CliHost.ExecuteAsync(req, relayed: true));
        _cliPipe.Start();

        var minimized = SettingsService.Instance.Current.StartMinimized
                        || e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        if (!minimized)
            ShowMainWindow();

        // 测试辅助：--show-flyout 启动时直接打开托盘面板
        if (e.Args.Any(a => a.Equals("--show-flyout", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.InvokeAsync(ToggleFlyout);
    }

    private void OnSettingsChanged()
    {
        // 主题可能被托盘或设置页修改，统一在这里响应
        ThemeService.Apply(SettingsService.Instance.Current.Theme);

        // 语言切换：索引器绑定会自动刷新，但窗口标题是普通字符串，
        // 需要显式同步一次；托盘提示同理。
        if (I18n.Localization.Instance.Apply(SettingsService.Instance.Current.Language))
        {
            var title = Strings.T("App.Title");
            if (_mainWindow is not null) _mainWindow.Title = title;
            _tray.SetTip(title);
        }
    }

    /// <summary>
    /// 读取托盘图标。
    ///
    /// 原先用 <c>System.Drawing.Icon.ExtractAssociatedIcon</c>，它会连带加载
    /// System.Drawing.Common → windows.storage.dll(8.7MB) + WindowsCodecs(2.2MB)
    /// 等一整串模块，实测让托盘常驻多占约 17MB。
    /// 这里改用纯 Win32 的 LoadImage，只读一个图标资源，不引入任何额外依赖。
    /// </summary>
    private static IntPtr LoadTrayIcon()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return IntPtr.Zero;

        const uint IMAGE_ICON = 1;
        const uint LR_LOADFROMFILE = 0x00000010;
        const uint LR_DEFAULTSIZE = 0x00000040;

        var h = ShellNative.LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE);
        if (h != IntPtr.Zero) return h;

        // 退路：直接从当前进程的可执行模块里取图标
        var module = ShellNative.GetModuleHandleW(null);
        h = ShellNative.LoadImage(module, "#32512", IMAGE_ICON, 0, 0, LR_DEFAULTSIZE);
        if (h == IntPtr.Zero) AppPaths.Log("读取托盘图标失败，使用默认图标");
        return h;
    }

    private IntPtr OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case ShellNative.WM_DISPLAYCHANGE:
                DisplayService.Instance.RaiseDisplaysChanged();
                break;
            case (int)ShellNative.WM_APP_TRAY:
                _tray.OnTrayMessage(lParam.ToInt32());
                break;
            case ShellNative.WM_HOTKEY:
                _hotkeys?.HandleWmHotkey(wParam.ToInt32());
                break;
        }
        return IntPtr.Zero;
    }

    private void OnHotkeyPressed(string profileId)
    {
        var profile = ProfileStore.Instance.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile == null) return;
        _ = ApplyGuard.Instance.ApplyAsync(profile);
    }

    public void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_exiting) return;
            EnsureUiReady();
            if (_mainWindow == null)
            {
                _mainWindow = new MainWindow();
                _mainWindow.Closed += (_, _) => _mainWindow = null;
            }
            _mainWindow.ShowAndActivate();
        });
    }

    /// <summary>
    /// 关闭到托盘时销毁主窗口实例，并尽量把 WPF 渲染管线占用的内存还给系统。
    ///
    /// 为什么必须销毁：WPF 一旦创建过窗口就会加载 D3D 驱动模块（本机约 126MB）
    /// 并常驻控件模板，仅 Hide() 不会释放。用户“关闭窗口”后的预期是回到托盘轻量状态。
    /// 销毁后下次打开会重建窗口（约 300ms），换来的是常驻占用回到低位。
    /// </summary>
    public void ReleaseMainWindow()
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_exiting) return;

            try { _mainWindow?.Close(); } catch { }
            _mainWindow = null;

            // 托盘面板与还原确认条同样持有 WPF 窗口，一并释放
            try { _flyout?.Close(); } catch { }
            _flyout = null;
            ApplyGuard.Instance.ReleaseWindow();

            // 强制回收：WPF 的渲染资源多为托管对象持有，GC 后才能归还
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>供 MainWindow 在 Closing 时调用的静态入口。</summary>
    public static void ReleaseMainWindowStatic() => (Current as App)?.ReleaseMainWindow();

    /// <summary>
    /// 首次显示任何 WPF 窗口前，把 WPF-UI 资源与主题加载好。
    ///
    /// 托盘常驻阶段刻意不加载这些资源（见 ThemeService.EnsureUiResources 的说明），
    /// 否则整套控件模板会提前进内存，常驻占用降不到目标值。
    /// </summary>
    private static void EnsureUiReady()
    {
        ThemeService.EnsureUiResources();
        ThemeService.Apply(SettingsService.Instance.Current.Theme);
    }

    private void ToggleFlyout()
    {
        Dispatcher.Invoke(() =>
        {
            if (_exiting) return;
            if (_flyout is { IsVisible: true })
            {
                _flyout.HideWithFade();
                return;
            }
            EnsureUiReady();
            _flyout ??= new TrayFlyoutWindow();
            _flyout.Open();
        });
    }

    private void ShowTrayMenu()
    {
        Dispatcher.Invoke(() =>
        {
            if (_exiting) return;
            EnsureUiReady();
            // 先关掉旧菜单，避免重复打开残留
            if (_trayMenu != null) _trayMenu.IsOpen = false;
            _trayMenu = BuildTrayMenu();
            _trayMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            _trayMenu.IsOpen = true;
        });
    }

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();

        var profiles = ProfileStore.Instance.Profiles;
        if (profiles.Count > 0)
        {
            foreach (var p in profiles)
            {
                var profile = p;
                var item = new MenuItem { Header = profile.Name, Tag = profile };
                item.Click += async (_, _) => await ApplyGuard.Instance.ApplyAsync(profile);
                menu.Items.Add(item);
            }
            menu.Items.Add(new Separator());
        }

        var open = new MenuItem { Header = Strings.T("Tray.OpenMain") };
        open.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(open);

        var autostart = new MenuItem
        {
            Header = Strings.T("Tray.MenuAutoStart"),
            IsCheckable = true,
            IsChecked = AutoStartService.IsEnabled(),
        };
        autostart.Click += (_, _) => AutoStartService.SetEnabled(autostart.IsChecked);
        menu.Items.Add(autostart);

        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = Strings.T("Tray.MenuExit") };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);

        return menu;
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;

        Dispatcher.Invoke(() =>
        {
            try { if (_trayMenu != null) _trayMenu.IsOpen = false; } catch { }
            try { _flyout?.Close(); } catch { }
            try { _mainWindow?.Close(); } catch { }
            _cliPipe?.Dispose();
            _tray.Dispose();
            _hotkeys?.Dispose();
            _messages?.Dispose();
            if (_trayIconHandle != IntPtr.Zero)
            {
                ShellNative.DestroyIcon(_trayIconHandle);
                _trayIconHandle = IntPtr.Zero;
            }
            _single.Dispose();
            Shutdown();
        });
    }
}
