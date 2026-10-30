namespace MonitorSwitch.Services;

/// <summary>单实例保护：第二次启动时唤起已有实例。</summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>互斥体与唤醒事件的名称（CLI 用互斥体快速判断应用是否在运行）。</summary>
    public const string MutexName = @"Local\MonitorSwitch_Instance";
    public const string ActivateEventName = @"Local\MonitorSwitch_Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activate;
    private System.Threading.RegisteredWaitHandle? _wait;

    public event Action? ActivateRequested;

    public bool TryBegin()
    {
        _mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
                evt.Set();
            }
            catch { }
            return false;
        }

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _wait = ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, _) => ActivateRequested?.Invoke(),
            null,
            -1,
            executeOnlyOnce: false);
        return true;
    }

    public void Dispose()
    {
        // 必须先注销等待：直接关闭事件句柄而 wait 还在册属于未定义行为，
        // 退出竞态下回调可能再触发一次 ActivateRequested → 已关闭的 Dispatcher
        _wait?.Unregister(null);
        _wait = null;
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _activate?.Dispose();
    }
}
