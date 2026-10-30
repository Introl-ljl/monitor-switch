using System.Text.Json;
using System.Text.Json.Serialization;

namespace MonitorSwitch.Models;

public sealed class AppSettings
{
    /// <summary>当前 settings.json 的结构版本，与 DisplayProfile.CurrentSchemaVersion 各自独立递增。</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Auto / Light / Dark</summary>
    public string Theme { get; set; } = "Auto";

    /// <summary>界面语言：auto（跟随系统）/ en / zh-CN。</summary>
    public string Language { get; set; } = "auto";

    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    /// <summary>应用配置后显示“保留 / 还原”确认，15 秒后自动还原。</summary>
    public bool RevertGuardEnabled { get; set; } = true;

    /// <summary>
    /// 托盘面板上次被拖动到的位置（物理像素）。
    /// 为 null 表示用户还没拖动过，此时按光标位置自动摆放。
    /// 显示器布局变化后该位置可能已无效，使用前会做一次可见性校验。
    /// </summary>
    public double? FlyoutLeft { get; set; }
    public double? FlyoutTop { get; set; }

    /// <summary>兜底保存文件中本程序不认识的字段，序列化时原样写回（防止降级丢数据）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
