using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MonitorSwitch.I18n;
using MonitorSwitch.Interop;

namespace MonitorSwitch.Models;

/// <summary>单个显示器的配置快照（可序列化，作为模板的最小单元）。</summary>
public sealed class MonitorSnapshot
{
    /// <summary>目标设备路径，跨重启稳定的显示器标识。</summary>
    public string TargetDevicePath { get; set; } = "";
    public string MonitorName { get; set; } = "";
    public string AdapterName { get; set; } = "";
    /// <summary>源 GDI 设备名（\\.\DISPLAY1），用于应用时优先匹配同一源。</summary>
    public string SourceGdiDeviceName { get; set; } = "";
    public uint Connector { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>桌面坐标（虚拟桌面坐标系，物理像素）。</summary>
    public int PositionX { get; set; }
    public int PositionY { get; set; }

    /// <summary>
    /// CCD 源模式分辨率。注意：CCD 的源模式始终以“面板原生朝向”表示，
    /// 90°/270° 旋转时与桌面上的宽高互换（例如竖屏 2560×1600 面板对应源模式 2560×1600，
    /// 而桌面占用为 1600×2560）。
    /// </summary>
    public uint SourceWidth { get; set; }
    public uint SourceHeight { get; set; }

    /// <summary>
    /// 应用时写入目标模式 activeSize 的分辨率（面板朝向）。
    /// 通常等于面板原生分辨率；用户通过草稿/CLI 改分辨率后即为请求分辨率。
    /// </summary>
    public uint PanelWidth { get; set; }
    public uint PanelHeight { get; set; }

    public uint RefreshNumerator { get; set; } = 60;
    public uint RefreshDenominator { get; set; } = 1;
    public double RefreshHz { get; set; } = 60;

    /// <summary>1=横向 2=纵向(90°) 3=横向(180°) 4=纵向(270°)。</summary>
    public uint Rotation { get; set; } = 1;
    public uint Scaling { get; set; } = 1;
    public bool Hdr { get; set; }

    // 目标时序（用于精确还原刷新率）
    public ulong PixelRate { get; set; }
    public uint HSyncNumerator { get; set; }
    public uint HSyncDenominator { get; set; } = 1;
    public uint VSyncNumerator { get; set; }
    public uint VSyncDenominator { get; set; } = 1;
    /// <summary>扫描线序（1=逐行）。捕获自隔行时序的模板应用时需原样回写。</summary>
    public uint ScanLineOrdering { get; set; } = 1;

    /// <summary>
    /// 兜底保存文件中本程序不认识的字段：序列化时原样写回，避免升级/降级时
    /// 新增字段被一次普通保存静默剥离（配合 SchemaVersion 使用）。
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public MonitorSnapshot Clone() => (MonitorSnapshot)MemberwiseClone();

    /// <summary>旋转后桌面上的宽度。</summary>
    [JsonIgnore]
    public uint DesktopWidth => Rotation is 2 or 4 ? SourceHeight : SourceWidth;

    /// <summary>旋转后桌面上的高度。</summary>
    [JsonIgnore]
    public uint DesktopHeight => Rotation is 2 or 4 ? SourceWidth : SourceHeight;

    [JsonIgnore]
    public bool IsPortrait => Rotation is 2 or 4;

    [JsonIgnore]
    public string ResolutionText => $"{DesktopWidth} × {DesktopHeight}";
}

/// <summary>一份可一键恢复的显示器配置模板。</summary>
public sealed class DisplayProfile
{
    /// <summary>当前 profiles.json 的结构版本。升级结构时递增并在加载处做迁移。</summary>
    public const int CurrentSchemaVersion = 1;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ModifiedAt { get; set; } = DateTime.Now;
    /// <summary>全局快捷键，如 "Ctrl+Alt+1"；null 表示未设置。</summary>
    public string? Hotkey { get; set; }
    public List<MonitorSnapshot> Monitors { get; set; } = new();

    /// <summary>兜底保存文件中本程序不认识的字段，序列化时原样写回（防止降级丢数据）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public DisplayProfile Clone() => new()
    {
        Id = Id,
        SchemaVersion = SchemaVersion,
        Name = Name,
        CreatedAt = CreatedAt,
        ModifiedAt = ModifiedAt,
        Hotkey = Hotkey,
        Monitors = Monitors.Select(m => m.Clone()).ToList(),
        ExtensionData = ExtensionData is null ? null : new Dictionary<string, JsonElement>(ExtensionData),
    };

    [JsonIgnore]
    public string Summary
    {
        get
        {
            var act = Monitors.Where(m => m.Active).ToList();
            return act.Count switch
            {
                0 => Strings.T("Profiles.SummaryEmpty"),
                1 => Strings.F("Profiles.SummaryOne", act[0].ResolutionText, FormatHz(act[0].RefreshHz)),
                _ => Strings.F("Profiles.SummaryMany", act.Count, act[0].ResolutionText),
            };
        }
    }

    public static string FormatHz(double hz)
    {
        // InvariantCulture：保证 CLI 的机器可读输出在逗号小数区域（如 de-DE）下
        // 仍可被 ParseRefresh 无损回读，界面与脚本看到同一格式。
        return Math.Abs(hz - Math.Round(hz)) < 0.01
            ? Math.Round(hz).ToString("0", CultureInfo.InvariantCulture) + " Hz"
            : hz.ToString("0.##", CultureInfo.InvariantCulture) + " Hz";
    }
}

public enum QuickTopology
{
    InternalOnly,
    Clone,
    Extend,
    ExternalOnly,
}

public sealed class ApplyResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public List<string> Warnings { get; init; } = new();

    public static ApplyResult Ok(List<string>? warnings = null) => new() { Success = true, Warnings = warnings ?? new List<string>() };
    public static ApplyResult Fail(string message) => new() { Success = false, ErrorMessage = message };
}

/// <summary>运行时的活动显示器信息（不序列化）。</summary>
public sealed class LiveDisplay
{
    public int Number { get; set; }
    public string TargetDevicePath { get; set; } = "";
    public string MonitorName { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string SourceGdiName { get; set; } = "";
    public uint Connector { get; set; }

    /// <summary>桌面坐标（物理像素）。</summary>
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>桌面上的宽高（已按旋转换算）。</summary>
    public uint W { get; set; }
    public uint H { get; set; }

    /// <summary>CCD 源模式（面板朝向）。</summary>
    public uint SourceW { get; set; }
    public uint SourceH { get; set; }

    public double RefreshHz { get; set; }
    public uint RefreshNum { get; set; } = 60;
    public uint RefreshDen { get; set; } = 1;
    public uint Rotation { get; set; } = 1;
    public uint Scaling { get; set; }
    public uint ScanLineOrdering { get; set; } = 1;
    public double ScalePercent { get; set; } = 100;
    public bool HdrSupported { get; set; }
    public bool HdrEnabled { get; set; }
    public bool IsPrimary { get; set; }
    public GdiNative.RECT MonitorRect { get; set; }
    public List<(uint W, uint H)> Resolutions { get; } = new();
    public List<double> RefreshRates { get; } = new();

    // 时序快照
    public uint PanelW { get; set; }
    public uint PanelH { get; set; }
    public ulong PixelRate { get; set; }
    public uint HSyncNum { get; set; }
    public uint HSyncDen { get; set; } = 1;
    public uint VSyncNum { get; set; }
    public uint VSyncDen { get; set; } = 1;

    public string DisplayName => string.IsNullOrWhiteSpace(MonitorName)
        ? Strings.F("Display.NameFormat", Number)
        : MonitorName;

    /// <summary>
    /// 连接器类型 → 文案键。与 wingdi.h 的 DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY 对齐：
    /// OTHER=-1（uint 形式为 0xFFFFFFFF）、HD15=1、SVIDEO=2、COMPOSITE=3、COMPONENT=4、
    /// DVI=5、HDMI=6、LVDS=7、D_JPN=8、SDI=9、DISPLAYPORT_EXT=10、DISPLAYPORT_EMB=11、
    /// UDI_EXT=12、UDI_EMB=13、SDTVDONGLE=14、MIRACAST=15、INDIRECT_WIRED=16、
    /// INDIRECT_VIRTUAL=17、DISPLAYPORT_USB_TUNNELLED=18。
    /// internal 供测试工程断言映射。
    /// </summary>
    internal static string ConnectorResourceKey(uint tech) => tech switch
    {
        1 => "Display.Connector.Vga",
        2 => "Display.Connector.SVideo",
        3 => "Display.Connector.Composite",
        4 => "Display.Connector.Component",
        5 => "Display.Connector.Dvi",
        6 => "Display.Connector.Hdmi",
        7 => "Display.Connector.Lvds",
        8 => "Display.Connector.DJpn",
        9 => "Display.Connector.Sdi",
        10 or 11 => "Display.Connector.DisplayPort",
        12 or 13 => "Display.Connector.Udi",
        14 => "Display.Connector.Sdtv",
        15 => "Display.Connector.Miracast",
        16 or 17 => "Display.Connector.Virtual",
        18 => "Display.Connector.DisplayPort",
        _ => "Display.Connector.Other", // 含 OTHER(-1)、0 及未来新增值
    };

    public string ConnectorName => Strings.T(ConnectorResourceKey(Connector));

    public string ResolutionText => $"{W} × {H}";

    public MonitorSnapshot ToSnapshot() => new()
    {
        TargetDevicePath = TargetDevicePath,
        MonitorName = MonitorName,
        AdapterName = AdapterName,
        SourceGdiDeviceName = SourceGdiName,
        Connector = Connector,
        Active = true,
        PositionX = X,
        PositionY = Y,
        SourceWidth = SourceW,
        SourceHeight = SourceH,
        PanelWidth = PanelW,
        PanelHeight = PanelH,
        RefreshNumerator = RefreshNum,
        RefreshDenominator = RefreshDen,
        RefreshHz = RefreshHz,
        Rotation = Rotation,
        Scaling = Scaling,
        ScanLineOrdering = ScanLineOrdering,
        Hdr = HdrEnabled,
        PixelRate = PixelRate,
        HSyncNumerator = HSyncNum,
        HSyncDenominator = HSyncDen,
        VSyncNumerator = VSyncNum,
        VSyncDenominator = VSyncDen,
    };
}
