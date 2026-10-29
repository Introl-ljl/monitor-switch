using Microsoft.Win32;

namespace MonitorSwitch.Services;

/// <summary>开机自启动（HKCU Run 键）。</summary>
public static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorSwitch";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string v || v.Length == 0) return false;
            // 仅当登记的可执行文件仍是当前程序时才算启用（避免移动/升级后残留项被误判）
            var registered = ExtractPath(v);
            var current = Environment.ProcessPath;
            if (string.IsNullOrEmpty(current)) return true;
            return string.IsNullOrEmpty(registered) ||
                   string.Equals(registered, current, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>启用/禁用自启动，返回操作后的实际状态。</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;

                // 勾选“启动时最小化到托盘”时带上 --minimized，避免开机弹窗
                var args = SettingsService.Instance.Current.StartMinimized ? " --minimized" : "";
                key.SetValue(ValueName, $"\"{exe}\"{args}");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("设置自启动失败: " + ex.Message);
        }
        return IsEnabled() == enabled;
    }

    /// <summary>从 "C:\path\app.exe" --minimized 中取出可执行文件路径。</summary>
    private static string ExtractPath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command.Trim('"');
        }
        int space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }
}
