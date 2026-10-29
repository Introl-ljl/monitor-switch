using System.Runtime.InteropServices;

namespace MonitorSwitch.Cli;

/// <summary>
/// 让 WinExe 进程在 CLI 模式下能把结果写到调用方的控制台。
///
/// 本项目是 WinExe，进程默认不分配控制台。分三种情况：
/// 1. stdout 已被调用方重定向（PowerShell 里 <c>&gt; file.txt</c>、被其它程序以
///    管道捕获）：不做任何事，.NET 的 <see cref="Console.Out"/> 会直接写到
///    继承来的句柄上。若此时附着控制台，输出会改道屏幕、丢失重定向内容。
/// 2. 从 PowerShell / cmd / Windows Terminal 直接运行：AttachConsole 附着到
///    父进程的控制台；.NET 打开标准输出时发现句柄无效，会自行回退打开
///    CONOUT$，于是中文与 ASCII 都能正常出现在终端里。
/// 3. 从资源管理器双击 / 运行对话框启动：没有父控制台，附着失败——输出被
///    静默丢弃，退出码承担全部结果语义。
/// </summary>
public static class ConsoleAttach
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    public static void TryAttach()
    {
        try
        {
            var std = GetStdHandle(STD_OUTPUT_HANDLE);
            if (std != IntPtr.Zero && std != new IntPtr(-1))
            {
                // 已重定向：保持 .NET 默认行为，把 Console.Out 写到该句柄。
                // 没有控制台时设置编码会抛异常，这里不碰它（文件/管道字节按 UTF-8 写出
                // 由 StreamWriter 的默认编码决定，中文在 PowerShell 7 / json 读取方下无碍）。
                return;
            }

            if (!AttachConsole(ATTACH_PARENT_PROCESS)) return;

            // 设置 OutputEncoding 会把控制台代码页切到 UTF-8，并让运行时按新编码
            // 重建输出流（此时 .NET 会回退打开 CONOUT$），终端里不乱码。
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // 没有控制台就什么都不输出，退出码承担全部结果语义
        }
    }
}
