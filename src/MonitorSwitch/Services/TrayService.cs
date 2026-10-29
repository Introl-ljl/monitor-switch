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
