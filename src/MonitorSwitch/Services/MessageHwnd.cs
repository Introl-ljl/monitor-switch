using System.Runtime.InteropServices;

namespace MonitorSwitch.Services;

/// <summary>
/// 隐藏的顶层消息窗口（接收托盘消息 / WM_DISPLAYCHANGE / WM_HOTKEY / 广播消息）。
///
/// 为什么不用 WPF 的 <c>HwndSource</c>：
/// HwndSource 是 WPF 的渲染宿主，创建它就会初始化 D3D 渲染管线。实测仅创建
/// 一个消息窗口，进程就会加载 nvgpucomp64.dll(79MB) + nvd3dumx.dll(39MB)
/// 等显卡驱动模块，常驻内存从约 37MB 涨到约 100MB。
/// 托盘常驻场景完全不需要任何渲染能力，因此这里改用纯 Win32 消息窗口：
/// RegisterClassEx + CreateWindowEx + WndProc。
/// 消息由 WPF Dispatcher 的同一套 Win32 消息泵派发，无需自建循环。
///
/// 为什么不能用 HWND_MESSAGE（仅消息窗口）：仅消息窗口不参与窗口枚举，
/// 系统**不向它投递广播消息** —— WM_DISPLAYCHANGE、TaskbarCreated、
/// WM_SETTINGCHANGE 全都收不到（Win32 文档明确）。因此这里创建的是
/// WS_POPUP 隐藏顶层窗口：从不 Show 就不会出现在任务栏/Alt-Tab/屏幕上，
/// 但广播消息照常到达。
/// </summary>
public sealed class MessageHwnd : IDisposable
{
    private const uint WS_POPUP = 0x80000000;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint CbSize;
        public uint Style;
        public IntPtr LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public IntPtr HInstance;
        public IntPtr HIcon;
        public IntPtr HCursor;
        public IntPtr HbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? LpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string LpszClassName;
        public IntPtr HIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    /// <summary>返回值作为窗口过程结果（通常 IntPtr.Zero 表示交给 DefWindowProc）。</summary>
    public event Func<int, IntPtr, IntPtr, IntPtr>? MessageReceived;

    // 必须用字段持有委托：只传指针给系统而让委托被 GC 回收，
    // 后续消息派发会跳到已释放的存根上，直接崩溃。
    private readonly WndProcDelegate _proc;
    private readonly string _className;
    private readonly IntPtr _hInstance;
    private IntPtr _hwnd;
    private bool _disposed;

    public IntPtr Handle => _hwnd;

    public MessageHwnd()
    {
        _className = "MonitorSwitchMessages_" + Guid.NewGuid().ToString("N")[..8];
        _hInstance = GetModuleHandleW(null);
        _proc = WndProc;

        var wc = new WNDCLASSEXW
        {
            CbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            HInstance = _hInstance,
            LpszClassName = _className,
        };

        if (RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("RegisterClassEx 失败，错误码 " + Marshal.GetLastWin32Error());

        // 隐藏顶层窗口（父窗口为 null）：不显示就不进任务栏，但广播消息可达。
        // HWND_MESSAGE 的仅消息窗口收不到 WM_DISPLAYCHANGE 等广播，见类注释。
        _hwnd = CreateWindowExW(0, _className, "MonitorSwitch", WS_POPUP, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            UnregisterClassW(_className, _hInstance);
            throw new InvalidOperationException("CreateWindowEx 失败，错误码 " + err);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 异常屏障：这里的异常会穿透原生回调成为进程级崩溃（托盘应用直接消失），
        // 订阅者内部的问题必须就地拦截并记录。
        try
        {
            var handler = MessageReceived;
            if (handler != null)
            {
                var result = handler((int)msg, wParam, lParam);
                if (result != IntPtr.Zero) return result;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"消息窗口处理 0x{msg:X4} 时异常: " + ex);
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        UnregisterClassW(_className, _hInstance);
        GC.KeepAlive(_proc);
    }
}
