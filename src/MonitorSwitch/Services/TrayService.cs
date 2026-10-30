using static MonitorSwitch.Interop.ShellNative;
using System.Runtime.InteropServices;
using MonitorSwitch.Interop;

namespace MonitorSwitch.Services;

/// <summary>系统托盘图标（Shell_NotifyIcon 经典模式）。</summary>
public sealed class TrayService : IDisposable
{
    private NOTIFYICONDATAW _nid;
    private bool _added;

    public event Action? LeftClick;
    public event Action? RightClick;

    public void Init(IntPtr hwnd, IntPtr hIcon, string tip)
    {
        _nid = new NOTIFYICONDATAW
        {
            CbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            HWnd = hwnd,
            UId = 1,
            UFlags = ShellNative.NIF_MESSAGE | ShellNative.NIF_ICON | ShellNative.NIF_TIP,
            UCallbackMessage = ShellNative.WM_APP_TRAY,
            HIcon = hIcon,
            SzTip = tip,
        };
        _added = ShellNative.Shell_NotifyIcon(ShellNative.NIM_ADD, ref _nid);
        if (!_added) AppPaths.Log("托盘图标添加失败");
    }

    /// <summary>更新托盘提示文字（切换语言后调用）。</summary>
    public void SetTip(string tip)
    {
        if (!_added) return;
        _nid.SzTip = tip;
        ShellNative.Shell_NotifyIcon(ShellNative.NIM_MODIFY, ref _nid);
    }

    /// <summary>
    /// 收到 TaskbarCreated（Explorer 重启）后重建托盘图标。
    /// Explorer 重启会清掉所有 Shell_NotifyIcon 图标，不重加的话应用仍在运行
    /// 却没有任何入口。失败时保持 _added=false，避免后续 Modify 打到无效句柄。
    /// </summary>
    public void ReAdd()
    {
        if (_nid.HWnd == IntPtr.Zero) return;
        ShellNative.Shell_NotifyIcon(ShellNative.NIM_DELETE, ref _nid);
        _added = ShellNative.Shell_NotifyIcon(ShellNative.NIM_ADD, ref _nid);
        AppPaths.Log(_added ? "Explorer 重启，托盘图标已重建" : "托盘图标重建失败");
    }

    /// <summary>托盘气泡通知（如“快捷键注册失败”这类无窗口时的用户反馈）。</summary>
    public void ShowBalloon(string title, string message)
    {
        if (!_added) return;
        var nid = _nid;
        nid.UFlags |= ShellNative.NIF_INFO;
        nid.SzInfoTitle = title;
        nid.SzInfo = message;
        ShellNative.Shell_NotifyIcon(ShellNative.NIM_MODIFY, ref nid);
    }

    public void OnTrayMessage(int msg)
    {
        switch (msg)
        {
            case ShellNative.WM_LBUTTONUP:
                LeftClick?.Invoke();
                break;
            case ShellNative.WM_RBUTTONUP:
                RightClick?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_added)
            ShellNative.Shell_NotifyIcon(ShellNative.NIM_DELETE, ref _nid);
        _added = false;
    }
}
