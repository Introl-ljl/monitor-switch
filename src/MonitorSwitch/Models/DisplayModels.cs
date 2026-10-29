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

    /// <summary>面板原生分辨率（目标模式 activeSize），同样为面板朝向。</summary>
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
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ModifiedAt { get; set; } = DateTime.Now;
    /// <summary>全局快捷键，如 "Ctrl+Alt+1"；null 表示未设置。</summary>
    public string? Hotkey { get; set; }
    public List<MonitorSnapshot> Monitors { get; set; } = new();

    public DisplayProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        CreatedAt = CreatedAt,
        ModifiedAt = ModifiedAt,
        Hotkey = Hotkey,
        Monitors = Monitors.Select(m => m.Clone()).ToList(),
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

    public static string FormatHz(double hz) =>
        Math.Abs(hz - Math.Round(hz)) < 0.01 ? $"{Math.Round(hz):0} Hz" : $"{hz:0.##} Hz";
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

    public string ConnectorName => Connector switch
    {
        0 => Strings.T("Display.Connector.Vga"),
        4 => Strings.T("Display.Connector.Dvi"),
        5 => Strings.T("Display.Connector.Hdmi"),
        6 => Strings.T("Display.Connector.Lvds"),
        10 or 11 => Strings.T("Display.Connector.DisplayPort"),
        12 or 13 => Strings.T("Display.Connector.Udi"),
        14 => Strings.T("Display.Connector.Sdtv"),
        15 => Strings.T("Display.Connector.Miracast"),
        1 => Strings.T("Display.Connector.SVideo"),
        2 => Strings.T("Display.Connector.Composite"),
        3 => Strings.T("Display.Connector.Component"),
        8 => Strings.T("Display.Connector.DJpn"),
        9 => Strings.T("Display.Connector.Sdi"),
        16 => Strings.T("Display.Connector.Internal"),
        _ => Strings.T("Display.Connector.Other"),
    };

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
        Hdr = HdrEnabled,
        PixelRate = PixelRate,
        HSyncNumerator = HSyncNum,
        HSyncDenominator = HSyncDen,
        VSyncNumerator = VSyncNum,
        VSyncDenominator = VSyncDen,
    };
}
