using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using MonitorSwitch.Services;

namespace MonitorSwitch.Views;

public partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        // 每次进入页面都重新读取，避免与托盘菜单等其它入口的状态不一致
        _loading = true;
        try
        {
            var s = SettingsService.Instance.Current;
            ThemeCombo.SelectedIndex = s.Theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0,
            };
            AutoStartToggle.IsChecked = AutoStartService.IsEnabled();
            StartMinimizedToggle.IsChecked = s.StartMinimized;
            CloseToTrayToggle.IsChecked = s.CloseToTray;
            RevertGuardToggle.IsChecked = s.RevertGuardEnabled;
            DataDirText.Text = AppPaths.DataDir;

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = version == null ? "v1.0.0" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
        finally
        {
            _loading = false;
        }
    }

    private void FlashSaved()
    {
        SavedBar.IsOpen = true;
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var theme = ThemeCombo.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "Auto" };
        SettingsService.Instance.Update(s => s.Theme = theme);
        ThemeService.Apply(theme);
        FlashSaved();
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        bool want = AutoStartToggle.IsChecked == true;
        bool ok = AutoStartService.SetEnabled(want);
        // 写入注册表可能失败（被安全软件拦截），回读真实状态并提示
        _loading = true;
        AutoStartToggle.IsChecked = AutoStartService.IsEnabled();
        _loading = false;
        if (!ok)
        {
            ShowSaved("无法修改开机自启动，可能被安全软件拦截。", true);
            return;
        }
        FlashSaved();
    }

    private void OnStartMinimizedToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Update(s => s.StartMinimized = StartMinimizedToggle.IsChecked == true);
        // 已开启自启动时同步更新 Run 项，让开机启动真正静默
        if (AutoStartService.IsEnabled())
            AutoStartService.SetEnabled(true);
        FlashSaved();
    }

    private void ShowSaved(string message, bool error)
    {
        SavedBar.Severity = error ? Wpf.Ui.Controls.InfoBarSeverity.Error : Wpf.Ui.Controls.InfoBarSeverity.Success;
        SavedBar.Title = error ? "操作失败" : "已保存";
        SavedBar.Message = message;
        SavedBar.IsOpen = true;
    }

    private void OnCloseToTrayToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Update(s => s.CloseToTray = CloseToTrayToggle.IsChecked == true);
        FlashSaved();
    }

    private void OnRevertGuardToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsService.Instance.Update(s => s.RevertGuardEnabled = RevertGuardToggle.IsChecked == true);
        FlashSaved();
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.DataDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.DataDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppPaths.Log("打开数据目录失败: " + ex.Message);
        }
    }
}
