using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MonitorSwitch.Services;

namespace MonitorSwitch.Cli;

/// <summary>
/// CLI 命名管道的帧协议与客户端。
///
/// 协议：4 字节小端长度 + UTF-8 JSON 正文（请求与响应同构）。
/// 管道名按会话隔离（单实例互斥体是 Local\ 前缀，同一终端会话唯一），
/// 不需要额外的 ACL 处理：默认 DACL 仅允许本用户连接，而同用户进程
/// 本就拥有与宿主应用完全相同的权限，不构成额外攻击面。
/// </summary>
internal static class CliPipe
{
    public static string Name { get; } = $"monitor-switch-cli-{Process.GetCurrentProcess().SessionId}";

    private const uint MaxFrameBytes = 8 * 1024 * 1024;

    public static async Task WriteFrameAsync(Stream stream, string payload, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        if (body.Length > MaxFrameBytes) throw new IOException("CLI frame too large");
        var head = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(head, (uint)body.Length);
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<string> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var head = new byte[4];
        await ReadExactlyAsync(stream, head, ct).ConfigureAwait(false);
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (len > MaxFrameBytes) throw new IOException("CLI frame too large");
        var body = new byte[len];
        await ReadExactlyAsync(stream, body, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(body);
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        await stream.ReadExactlyAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 客户端：把命令发给常驻实例并取回结果。
    /// 返回 null 表示管道不可达（应用未在运行 / 管道异常），调用方应回退直连模式。
    /// </summary>
    public static async Task<CliResponse?> TryCallAsync(CliRequest request)
    {
        // 先看单实例互斥体：应用没在运行时管道必然不存在，而 .NET 的 ConnectAsync
        // 对“管道不存在”也要等满超时才抛 TimeoutException（内部会重试 FILE_NOT_FOUND），
        // 不先排除的话每次直连都要白等 1.5 秒。
        if (!IsAppRunning()) return null;

        // 互斥体在说明应用在运行：管道被上一条命令占住（单实例管道）或应用正在启动
        // 时 ConnectAsync 会超时，这些窄窗口值得短重试。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(1500).ConfigureAwait(false);

                var json = JsonSerializer.Serialize(request, CliJson.Options);
                using var opCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await WriteFrameAsync(pipe, json, opCts.Token).ConfigureAwait(false);
                var respJson = await ReadFrameAsync(pipe, opCts.Token).ConfigureAwait(false);
                return JsonSerializer.Deserialize<CliResponse>(respJson, CliJson.Options)
                       ?? new CliResponse { Ok = false, ExitCode = 1, Error = "empty response" };
            }
            catch (Exception ex) when (attempt < 2 && (ex is IOException || ex is TimeoutException))
            {
                await Task.Delay(300).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 未连通或对端中断：回退直连。若请求已发出但响应丢失，命令可能已被
                // 应用执行；显示器操作重复执行是幂等的，风险可接受（见 docs/cli.md）。
                AppPaths.Log("CLI 中继不可用，回退直连: " + ex.Message);
                return null;
            }
            finally
            {
                try { pipe?.Dispose(); } catch { }
            }
        }
        AppPaths.Log("CLI 中继多次重试失败，回退直连");
        return null;
    }

    /// <summary>单实例互斥体是否仍被持有（= 托盘应用在本会话运行中）。</summary>
    private static bool IsAppRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(SingleInstance.MutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch
        {
            // 打不开（权限等异常）时保守地认为可能在运行，尝试中继
            return true;
        }
    }
}

/// <summary>
/// 常驻实例侧的管道服务：循环接收 CLI 客户端，把命令交给宿主执行后回写结果。
/// </summary>
public sealed class CliPipeServer : IDisposable
{
    private readonly Func<CliRequest, Task<CliResponse>> _execute;
    private CancellationTokenSource? _cts;

    public CliPipeServer(Func<CliRequest, Task<CliResponse>> execute) => _execute = execute;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    CliPipe.Name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // 单个请求的处理窗口（含命令执行时间），防止挂死的客户端占住管道
                using var perClient = CancellationTokenSource.CreateLinkedTokenSource(ct);
                perClient.CancelAfter(TimeSpan.FromSeconds(90));
                await HandleClientAsync(pipe, perClient.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AppPaths.Log("CLI 管道服务异常: " + ex.Message);
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { break; }
            }
            finally
            {
                try { pipe?.Dispose(); } catch { }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var reqJson = await CliPipe.ReadFrameAsync(pipe, ct).ConfigureAwait(false);

        CliRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<CliRequest>(reqJson, CliJson.Options);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(pipe, "invalid request: " + ex.Message, ct).ConfigureAwait(false);
            return;
        }
        if (request is null)
        {
            await WriteErrorAsync(pipe, "empty request", ct).ConfigureAwait(false);
            return;
        }

        CliResponse response;
        try
        {
            response = await _execute(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"CLI {request.Command} 执行异常: " + ex);
            response = new CliResponse { Ok = false, ExitCode = 1, Error = ex.Message };
        }

        await CliPipe.WriteFrameAsync(pipe, JsonSerializer.Serialize(response, CliJson.Options), ct)
            .ConfigureAwait(false);
    }

    private async Task WriteErrorAsync(NamedPipeServerStream pipe, string error, CancellationToken ct)
    {
        var response = new CliResponse { Ok = false, ExitCode = 2, Error = error };
        await CliPipe.WriteFrameAsync(pipe, JsonSerializer.Serialize(response, CliJson.Options), ct)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
    }
}
