namespace MonitorSwitch.Services;

/// <summary>单实例保护：第二次启动时唤起已有实例。</summary>
public sealed class SingleInstance : IDisposable
{
    private Mutex? _mutex;
    private EventWaitHandle? _activate;

    public event Action? ActivateRequested;

    public bool TryBegin()
    {
        _mutex = new Mutex(true, @"Local\MonitorSwitch_Instance", out var created);
        if (!created)
        {
            try
            {
                using var evt = EventWaitHandle.OpenExisting(@"Local\MonitorSwitch_Activate");
                evt.Set();
            }
            catch { }
            return false;
        }

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MonitorSwitch_Activate");
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
