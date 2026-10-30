using System.IO;
namespace MonitorSwitch.Services;

/// <summary>应用数据目录与日志。</summary>
public static class AppPaths
{
    /// <summary>
    /// 数据目录。
    ///
    /// 不用 <c>Environment.GetFolderPath(SpecialFolder.LocalApplicationData)</c>：
    /// 该方法会走 Known Folder 解析，连带加载 windows.storage.dll(8.7MB) +
    /// WindowsCodecs(2.2MB) + IconCodecService 等一整串模块（实测探针已验证）。
    /// 托盘常驻场景只需要一个路径字符串，直接读环境变量即可，零额外依赖。
    /// 环境变量缺失时再退回 GetFolderPath。
    /// </summary>
    public static readonly string DataDir = ResolveDataDir();

    private static string ResolveDataDir()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            // 极少数受限环境下环境变量可能缺失，退回 API
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        return Path.Combine(localAppData, "MonitorSwitch");
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string ProfilesFile => Path.Combine(DataDir, "profiles.json");
    public static string LogFile => Path.Combine(DataDir, "log.txt");

    /// <summary>UI 线程、线程池（还原/CLI 管道）会并发写日志；AppendAllText 默认共享读，
    /// 撞车抛 IOException 被吞 → 丢日志，截断与追加也可能互相踩，统一串行化。</summary>
    private static readonly object LogLock = new();

    public static void Log(string message)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
                // 日志超过 256KB 时截断
                if (new FileInfo(LogFile).Length > 256 * 1024)
                    File.WriteAllText(LogFile, string.Empty);
            }
        }
        catch { /* 日志失败不影响运行 */ }
    }
}
