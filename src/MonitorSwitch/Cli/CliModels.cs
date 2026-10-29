using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorSwitch.Cli;

/// <summary>一条 CLI 命令（在直连进程与常驻实例之间原样传递）。</summary>
public sealed class CliRequest
{
    /// <summary>命令谓词，如 "apply" / "set"。</summary>
    public string Command { get; set; } = "";

    /// <summary>去掉命令词与 --flag 后的位置参数。</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>长选项："--json" → ["json"]="true"，"--replace" 同理。</summary>
    public Dictionary<string, string> Flags { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public bool Json => Flags.ContainsKey("json");
}

/// <summary>CLI 命令的执行结果。Message 是已经按应用语言本地化的人类可读文本，
/// Data 是给 --json 模式的结构化载荷，两者都可以为空。</summary>
public sealed class CliResponse
{
    public bool Ok { get; set; }
    /// <summary>进程退出码：0 成功，1 执行失败，2 用法错误。</summary>
    public int ExitCode { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public List<string> Warnings { get; set; } = new();
    public object? Data { get; set; }
}

/// <summary>参数用法错误（对应退出码 2）。</summary>
public sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message) { }
}

/// <summary>CLI 专用的 JSON 序列化配置： camelCase + 宽松反序列化，方便脚本与跨进程往返。</summary>
internal static class CliJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
