namespace MonitorSwitch.Services;

/// <summary>单实例保护：第二次启动时唤起已有实例。</summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>互斥体与唤醒事件的名称（CLI 用互斥体快速判断应用是否在运行）。</summary>
    public const string MutexName = @"Local\MonitorSwitch_Instance";
    public const string ActivateEventName = @"Local\MonitorSwitch_Activate";

    private Mutex? _mutex;
    private EventWaitHandle? _activate;

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
        _ = ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, _) => ActivateRequested?.Invoke(),
            null,
            -1,
            executeOnlyOnce: false);
        return true;
    }

    public void Dispose()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _activate?.Dispose();
    }
}
