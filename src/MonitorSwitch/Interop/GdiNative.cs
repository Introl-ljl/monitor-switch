using System.Runtime.InteropServices;

namespace MonitorSwitch.Interop;

/// <summary>GDI / User32 显示相关 P/Invoke（枚举显示模式、监视器信息、DPI）。</summary>
public static class GdiNative
{
    public const uint DM_BITSPERPEL = 0x00040000;
    public const uint DM_PELSWIDTH = 0x00080000;
    public const uint DM_PELSHEIGHT = 0x00100000;
    public const uint DM_DISPLAYFREQUENCY = 0x00400000;
    public const uint DM_POSITION = 0x00000020;
    public const uint DM_DISPLAYORIENTATION = 0x00000080;

    public const int ENUM_CURRENT_SETTINGS = -1;
    public const int ENUM_REGISTRY_SETTINGS = -2;

    public const uint MONITOR_DEFAULTTONEAREST = 2;
    /// <summary>该点不在任何显示器上时返回 NULL（用于校验记忆坐标是否还有效）。</summary>
    public const uint MONITOR_DEFAULTTONULL = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEXW
    {
        public int CbSize;
        public RECT RcMonitor;
        public RECT RcWork;
        public uint DwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string SzDevice;
    }

    /// <summary>
    /// DEVMODEW（Sequential 布局 = C 自然对齐，总大小 220 字节）。
    /// 联合体（dmPosition / dmDisplayOrientation / 打印机字段）用 4 个 uint 占位：
    /// 联合体首字段是 dmPosition（x@0 y@4），dmDisplayOrientation 位于联合体偏移 8
    /// （即 Union2），dmDisplayFixedOutput 位于偏移 12（Union3）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DmDeviceName;
        public ushort DmSpecVersion;
        public ushort DmDriverVersion;
        public ushort DmSize;
        public ushort DmDriverExtra;
        public uint DmFields;
        public uint DmUnion0;
        public uint DmUnion1;
        public uint DmUnion2;
        public uint DmUnion3;
        public short DmColor;
        public short DmDuplex;
        public short DmYResolution;
        public short DmTTOption;
        public short DmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DmFormName;
        public ushort DmLogPixels;
        public uint DmBitsPerPel;
        public uint DmPelsWidth;
        public uint DmPelsHeight;
        public uint DmDisplayFlags;
        public uint DmDisplayFrequency;
        public uint DmICMMethod;
        public uint DmICMIntent;
        public uint DmMediaType;
        public uint DmDitherType;
        public uint DmReserved1;
        public uint DmReserved2;
        public uint DmPanningWidth;
        public uint DmPanningHeight;

        public int PositionX => (int)DmUnion0;
        public int PositionY => (int)DmUnion1;

        public static DEVMODEW Create()
        {
            var dm = new DEVMODEW
            {
                DmDeviceName = string.Empty.PadRight(32),
                DmFormName = string.Empty.PadRight(32),
                DmSpecVersion = 0x0400,
                DmDriverVersion = 0x0400,
            };
            dm.DmSize = (ushort)Marshal.SizeOf<DEVMODEW>();
            dm.DmDriverExtra = 0;
            return dm;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplaySettingsExW(string lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICEW
    {
        public int Cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;

        public static DISPLAY_DEVICEW Create() => new()
        {
            Cb = Marshal.SizeOf<DISPLAY_DEVICEW>(),
            DeviceName = "",
            DeviceString = "",
            DeviceID = "",
            DeviceKey = "",
        };
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    /// <summary>
    /// 按 GDI 设备名（\\.\DISPLAY1）取显卡友好名。
    /// CCD 的 DISPLAYCONFIG_ADAPTER_NAME 只提供设备路径，用户可读的显卡名需要走 GDI。
    /// </summary>
    public static string GetAdapterFriendlyName(string gdiDeviceName)
    {
        if (string.IsNullOrEmpty(gdiDeviceName)) return string.Empty;
        for (uint i = 0; i < 16; i++)
        {
            var dd = DISPLAY_DEVICEW.Create();
            if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
            if (string.Equals(dd.DeviceName, gdiDeviceName, StringComparison.OrdinalIgnoreCase))
                return (dd.DeviceString ?? string.Empty).Trim();
        }
        return string.Empty;
    }

    /// <summary>取某显示器适配器下挂载的监视器 PnP 名（EDID 派生），用于友好名兜底。</summary>
    public static string GetMonitorPnpName(string gdiDeviceName)
    {
        if (string.IsNullOrEmpty(gdiDeviceName)) return string.Empty;
        var dd = DISPLAY_DEVICEW.Create();
        if (EnumDisplayDevicesW(gdiDeviceName, 0, ref dd, 0))
            return (dd.DeviceString ?? string.Empty).Trim();
        return string.Empty;
    }

    /// <summary>
    /// 取某显示器的 EDID 派生设备 ID 片段（如 "BOE0B38"）。
    /// 注意必须读 <c>DeviceID</c>（形如 MONITOR\BOE0B38\{...}\0003），
    /// <c>DeviceString</c> 是友好名（如 "Generic PnP Monitor"），两者不能混用。
    /// </summary>
    public static string GetMonitorDeviceId(string gdiDeviceName)
    {
        if (string.IsNullOrEmpty(gdiDeviceName)) return string.Empty;
        var dd = DISPLAY_DEVICEW.Create();
        if (EnumDisplayDevicesW(gdiDeviceName, 0, ref dd, 0))
            return ParseMonitorId(dd.DeviceID ?? string.Empty);
        return string.Empty;
    }

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("Shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hMonitor, uint dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// 读取某显示器的实际缩放百分比。
    ///
    /// 首选 Shcore.GetDpiForMonitor(MDT_EFFECTIVE_DPI)：PerMonitorV2 进程下它返回
    /// 真实的当前 DPI，是唯一可靠的“绝对百分比”来源。部分环境实测恒返回 96
    /// （原因未明，可能与驱动/会话状态有关），此时退回注册表 PerMonitorSettings：
    /// DpiValue 是相对“推荐缩放”的档位偏移（0 = 推荐值，每档约 ±25%），而推荐值
    /// 没有公开 API，因此注册表路径只能给出近似值（推荐 100% 的显示器准确）。
    /// 该字段仅用于展示，不参与应用配置的任何计算。
    /// </summary>
    public static double GetMonitorScalePercent(string gdiDeviceName, uint effectiveDpi)
    {
        if (effectiveDpi > 96)
            return Math.Round(effectiveDpi * 100.0 / 96);

        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Control Panel\Desktop\PerMonitorSettings");
            if (root is null) return 100;

            // EDID 派生 ID（如 BOE0B38）与注册表子键名（BOE0B383_14_07E6_04^…）前缀匹配
            var id = GetMonitorDeviceId(gdiDeviceName);
            if (string.IsNullOrEmpty(id)) return 100;

            foreach (var name in root.GetSubKeyNames())
            {
                if (!name.StartsWith(id, StringComparison.OrdinalIgnoreCase)) continue;
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("DpiValue") is not int dv) continue;
                double pct = 100 + dv * 25;
                if (pct is >= 50 and <= 500) return pct;
            }
        }
        catch { }
        return 100;
    }

    /// <summary>从 "MONITOR\BOE0B38\{...}\0003" 中取出 "BOE0B38"。</summary>
    private static string ParseMonitorId(string pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId)) return "";
        var parts = pnpDeviceId.Split('\\');
        return parts.Length >= 2 ? parts[1] : "";
    }

    public static (uint W, uint H, double Refresh) GetRegistryPreferred(string gdiDeviceName)
    {
        var dm = DEVMODEW.Create();
        if (EnumDisplaySettingsExW(gdiDeviceName, ENUM_REGISTRY_SETTINGS, ref dm, 0))
            return (dm.DmPelsWidth, dm.DmPelsHeight, dm.DmDisplayFrequency);
        return (0, 0, 0);
    }

    /// <summary>枚举某显示器所有支持的模式（按 32bpp），返回 去重后的分辨率列表 与 每个分辨率可用的刷新率。</summary>
    public static List<(uint W, uint H, uint Bpp, uint Freq)> EnumerateModes(string gdiDeviceName)
    {
        var result = new List<(uint, uint, uint, uint)>();
        var seen = new HashSet<string>();
        for (int i = 0; ; i++)
        {
            var dm = DEVMODEW.Create();
            if (!EnumDisplaySettingsExW(gdiDeviceName, i, ref dm, 0))
                break;
            var key = $"{dm.DmPelsWidth}x{dm.DmPelsHeight}@{dm.DmDisplayFrequency}:{dm.DmBitsPerPel}";
            if (seen.Add(key))
                result.Add((dm.DmPelsWidth, dm.DmPelsHeight, dm.DmBitsPerPel, dm.DmDisplayFrequency));
            if (i > 512) break;
        }
        return result;
    }
}
