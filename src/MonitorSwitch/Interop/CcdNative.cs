using System.Runtime.InteropServices;

namespace MonitorSwitch.Interop;

/// <summary>
/// Windows CCD API (Client Configuration Display) P/Invoke。
/// 路径/模式数组一律使用原始字节缓冲区 + 运行时步长检测：
/// 不同 Windows 版本的 DISPLAYCONFIG_MODE_INFO 尺寸不同
/// （文档为 48 字节，Windows 11 26200 实测为 56 字节——TARGET_MODE 末尾多 8 字节），
/// 且 pixelRate 为 UINT64 而非 RATIONAL。
/// </summary>
public static class CcdNative
{
    // ---------- QueryDisplayConfig 标志 ----------
    public const uint QDC_ALL_PATHS = 0x00000001;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    public const uint QDC_DATABASE_CURRENT = 0x00000004;
    public const uint QDC_VIRTUAL_MODE_AWARE = 0x00000010;
    public const uint QDC_INCLUDE_HMD = 0x00000020;

    // ---------- SetDisplayConfig 标志 ----------
    public const uint SDC_TOPOLOGY_INTERNAL = 0x00000001;
    public const uint SDC_TOPOLOGY_CLONE = 0x00000002;
    public const uint SDC_TOPOLOGY_EXTEND = 0x00000004;
    public const uint SDC_TOPOLOGY_EXTERNAL = 0x00000008;
    public const uint SDC_TOPOLOGY_SUPPLIED = 0x00000010;
    public const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    public const uint SDC_VALIDATE = 0x00000040;
    public const uint SDC_APPLY = 0x00000080;
    public const uint SDC_SAVE_TO_DATABASE = 0x00000200;
    public const uint SDC_ALLOW_CHANGES = 0x00000400;
    public const uint SDC_FORCE_MODE_ENUMERATION = 0x00001000;
    public const uint SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000;
    public const uint SDC_VIRTUAL_MODE_AWARE = 0x00008000;
    public const uint SDC_VIRTUAL_REFRESH_RATE_AWARE = 0x00020000;
    public const uint SDC_USE_DATABASE_CURRENT =
        SDC_TOPOLOGY_INTERNAL | SDC_TOPOLOGY_CLONE | SDC_TOPOLOGY_EXTEND | SDC_TOPOLOGY_EXTERNAL;

    // ---------- DISPLAYCONFIG_DEVICE_INFO_TYPE ----------
    public const uint DCI_GET_SOURCE_NAME = 1;
    public const uint DCI_GET_TARGET_NAME = 2;
    public const uint DCI_GET_TARGET_PREFERRED_MODE = 3;
    public const uint DCI_GET_ADAPTER_NAME = 4;
    public const uint DCI_GET_ADVANCED_COLOR_INFO = 9;
    public const uint DCI_SET_ADVANCED_COLOR_STATE = 10;

    // ---------- 其他常量 ----------
    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;
    public const uint MODE_INFO_TYPE_SOURCE = 1;
    public const uint MODE_INFO_TYPE_TARGET = 2;
    public const uint MODE_INFO_TYPE_DESKTOP_IMAGE = 3;

    public const uint ROTATION_IDENTITY = 1;
    public const uint ROTATION_ROTATE90 = 2;
    public const uint ROTATION_ROTATE180 = 3;
    public const uint ROTATION_ROTATE270 = 4;

    public const uint SCALING_IDENTITY = 1;
    public const uint SCALING_PREFERRED = 80;

    public const uint PIXELFORMAT_32BPP = 4;
    public const uint SCANLINE_ORDERING_PROGRESSIVE = 1;

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    // ---------- 基础结构体（设备信息查询用） ----------

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID
    {
        public uint LowPart;
        public int HighPart;
        public override string ToString() => $"0x{HighPart:X8}{LowPart:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_2DREGION
    {
        public uint Cx;
        public uint Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint Type;
        public uint Size;
        public LUID AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_MODE
    {
        public ulong PixelRate; // 实测为 UINT64（如 4K144 ≈ 1.19GHz）
        public DISPLAYCONFIG_RATIONAL HSyncFreq;
        public DISPLAYCONFIG_RATIONAL VSyncFreq;
        public DISPLAYCONFIG_2DREGION ActiveSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_PREFERRED_MODE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Width;
        public uint Height;
        public DISPLAYCONFIG_TARGET_MODE TargetMode;
        public ulong Reserved0; // GET 类 API 允许 "至少" 大小，为未来扩展留裕量
        public ulong Reserved1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    /// <summary>
    /// 注意：文档中 DISPLAYCONFIG_ADAPTER_NAME 只有 adapterDevicePath 一个字符串字段
    /// （20 字节头 + 128 WCHAR = 276 字节）。此前误加 AdapterDescription 字段，
    /// 使结构体变成 532 字节，DisplayConfigGetDeviceInfo 直接返回 ERROR_INVALID_PARAMETER。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_ADAPTER_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string AdapterDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Value; // bit0 Enabled, bit1 Supported, bit2 Forceable
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Value; // bit0 AdvancedColorEnabled
    }

    // ---------- P/Invoke ----------

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        byte[]? pathArray,
        ref uint numModeArrayElements,
        byte[]? modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(
        uint numPathArrayElements,
        byte[]? pathArray,
        uint numModeArrayElements,
        byte[]? modeInfoArray,
        uint flags);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADAPTER_NAME requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_PREFERRED_MODE requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO requestPacket);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE requestPacket);

    // ---------- 字节访问辅助 ----------

    public static uint ReadUInt32(byte[] b, int off) => BitConverter.ToUInt32(b, off);
    public static int ReadInt32(byte[] b, int off) => BitConverter.ToInt32(b, off);
    public static ulong ReadUInt64(byte[] b, int off) => BitConverter.ToUInt64(b, off);
    public static void WriteUInt32(byte[] b, int off, uint v) => BitConverter.GetBytes(v).CopyTo(b, off);
    public static void WriteInt32(byte[] b, int off, int v) => BitConverter.GetBytes(v).CopyTo(b, off);
    public static void WriteUInt64(byte[] b, int off, ulong v) => BitConverter.GetBytes(v).CopyTo(b, off);

    public static LUID ReadLUID(byte[] b, int off) => new()
    { LowPart = ReadUInt32(b, off), HighPart = ReadInt32(b, off + 4) };

    public static void WriteLUID(byte[] b, int off, LUID v)
    {
        WriteUInt32(b, off, v.LowPart);
        WriteInt32(b, off + 4, v.HighPart);
    }

    // ---------- 路径元素（文档布局 72 字节；步长检测兼容扩展） ----------
    // 以下偏移已用原始内存转储逐一核对。

    private const int P_SRC_ADAPTER = 0;   // LUID (8)
    private const int P_SRC_ID = 8;
    private const int P_SRC_MODEIDX = 12;
    private const int P_SRC_STATUS = 16;
    private const int P_TGT_ADAPTER = 20;  // LUID (8)
    private const int P_TGT_ID = 28;
    private const int P_TGT_MODEIDX = 32;
    private const int P_OUTTECH = 36;
    private const int P_ROTATION = 40;
    private const int P_SCALING = 44;
    private const int P_REFNUM = 48;
    private const int P_REFDEN = 52;
    private const int P_SCANLINE = 56;
    private const int P_TGTAVAIL = 60;
    private const int P_TGT_STATUS = 64;
    private const int P_FLAGS = 68;

    // ---------- 模式元素偏移（前 16 字节固定，union 从 16 开始） ----------

    private const int M_INFOTYPE = 0;
    private const int M_ID = 4;
    private const int M_ADAPTER = 8;   // LUID (8)
    private const int M_UNION = 16;    // SOURCE: w@16 h@20 fmt@24 pos@28 / TARGET: pixelRate@16(8) hsync@24 vsync@32 active@40

    // ---------- 查询 ----------

    public sealed class QueryResult
    {
        public byte[] PathBuffer = Array.Empty<byte>();
        public byte[] ModeBuffer = Array.Empty<byte>();
        public int PathCount;
        public int ModeCount;
        public int PathStride = 72;
        public int ModeStride = 48;
    }

    private static readonly int[] PathStrideCandidates = { 72, 76, 80, 88 };
    private static readonly int[] ModeStrideCandidates = { 48, 56, 52, 64 };

    public static QueryResult Query(uint flags)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var hr = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (hr != ERROR_SUCCESS)
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes 失败 (0x{hr:X8})");

            var paths = new byte[pathCount * 96 + 128];
            var modes = new byte[modeCount * 80 + 160];
            hr = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (hr == ERROR_SUCCESS)
            {
                var result = new QueryResult
                {
                    PathBuffer = paths,
                    ModeBuffer = modes,
                    PathCount = (int)pathCount,
                    ModeCount = (int)modeCount,
                };
                result.PathStride = DetectPathStride(paths, result.PathCount, result.ModeCount);
                result.ModeStride = DetectModeStride(modes, result.ModeCount, paths, result.PathStride, result.PathCount);
                return result;
            }
            if (hr != ERROR_INSUFFICIENT_BUFFER)
                throw new InvalidOperationException($"QueryDisplayConfig 失败 (0x{hr:X8})");
        }
        throw new InvalidOperationException("QueryDisplayConfig 多次重试后仍然失败。");
    }

    private static int DetectPathStride(byte[] buf, int count, int modeCount)
    {
        foreach (var s in PathStrideCandidates)
        {
            if ((count + 1) * s > buf.Length) continue;
            if (ValidatePaths(buf, s, count, modeCount)) return s;
        }
        return 72;
    }

    private static bool ValidatePaths(byte[] buf, int stride, int count, int modeCount)
    {
        for (int i = 0; i < count; i++)
        {
            int o = i * stride;
            if (ReadUInt32(buf, o + P_ROTATION) > 4) return false;
            int tech = ReadInt32(buf, o + P_OUTTECH);
            if (tech != -1 && (tech < 0 || tech > 0x16)) return false;
            if (ReadUInt32(buf, o + P_FLAGS) > 0xFFFF) return false;
            uint srcIdx = ReadUInt32(buf, o + P_SRC_MODEIDX);
            if (srcIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && modeCount > 0 && srcIdx >= (uint)modeCount) return false;
            uint tgtIdx = ReadUInt32(buf, o + P_TGT_MODEIDX);
            if (tgtIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && modeCount > 0 && tgtIdx >= (uint)modeCount) return false;
        }
        return true;
    }

    private static int DetectModeStride(byte[] buf, int count, byte[] paths, int pathStride, int pathCount)
    {
        foreach (var s in ModeStrideCandidates)
        {
            if ((count + 1) * s > buf.Length) continue;
            if (ValidateModes(buf, s, count, paths, pathStride, pathCount)) return s;
        }
        return 48;
    }

    private static bool ValidateModes(byte[] buf, int stride, int count, byte[] paths, int pathStride, int pathCount)
    {
        for (int i = 0; i < count; i++)
        {
            uint t = ReadUInt32(buf, i * stride + M_INFOTYPE);
            if (t is not (MODE_INFO_TYPE_SOURCE or MODE_INFO_TYPE_TARGET or MODE_INFO_TYPE_DESKTOP_IMAGE))
                return false;
        }
        for (int i = 0; i < pathCount; i++)
        {
            int o = i * pathStride;
            uint srcIdx = ReadUInt32(paths, o + P_SRC_MODEIDX);
            if (srcIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && srcIdx < (uint)count)
            {
                if (ReadUInt32(buf, (int)srcIdx * stride + M_INFOTYPE) != MODE_INFO_TYPE_SOURCE) return false;
                if (ReadUInt32(buf, (int)srcIdx * stride + M_UNION) == 0) return false; // width > 0
            }
            uint tgtIdx = ReadUInt32(paths, o + P_TGT_MODEIDX);
            if (tgtIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && tgtIdx < (uint)count)
            {
                if (ReadUInt32(buf, (int)tgtIdx * stride + M_INFOTYPE) != MODE_INFO_TYPE_TARGET) return false;
                if (ReadUInt32(buf, (int)tgtIdx * stride + M_UNION + 24) == 0) return false; // activeSize.Cx
            }
        }
        return true;
    }

    // ---------- 路径字段访问 ----------

    public static LUID PathSourceAdapter(byte[] b, int stride, int i) => ReadLUID(b, i * stride + P_SRC_ADAPTER);
    public static uint PathSourceId(byte[] b, int stride, int i) => ReadUInt32(b, i * stride + P_SRC_ID);
    public static uint PathSourceModeIdx(byte[] b, int stride, int i) => ReadUInt32(b, i * stride + P_SRC_MODEIDX);
    public static LUID PathTargetAdapter(byte[] b, int stride, int i) => ReadLUID(b, i * stride + P_TGT_ADAPTER);
    public static uint PathTargetId(byte[] b, int stride, int i) => ReadUInt32(b, i * stride + P_TGT_ID);
    public static uint PathTargetModeIdx(byte[] b, int stride, int i) => ReadUInt32(b, i * stride + P_TGT_MODEIDX);
    public static uint PathOutputTechnology(byte[] b, int stride, int i) => ReadUInt32(b, i * stride + P_OUTTECH);
    public static uint PathRotation(byte[] b, int stride, int i) => ReadUInt32(b, i * stride + P_ROTATION);
    public static uint PathScaling(byte[] b, int stride, int i) => ReadUInt32(b, i * stride + P_SCALING);
    public static uint PathFlags(byte[] b, int stride, int i) => ReadUInt32(b, i * stride + P_FLAGS);
    public static int PathTargetAvailable(byte[] b, int stride, int i) => ReadInt32(b, i * stride + P_TGTAVAIL);
    public static (uint Num, uint Den) PathRefreshRate(byte[] b, int stride, int i) =>
        (ReadUInt32(b, i * stride + P_REFNUM), ReadUInt32(b, i * stride + P_REFDEN));

    /// <summary>复制原始元素并标记为活动、清零状态位。</summary>
    public static void PathInitActive(byte[] dst, int dstOff, byte[] src, int srcOff, int stride)
    {
        Array.Copy(src, srcOff, dst, dstOff, stride);
        WriteUInt32(dst, dstOff + P_SRC_STATUS, 0);
        WriteUInt32(dst, dstOff + P_TGT_STATUS, 0);
        WriteUInt32(dst, dstOff + P_FLAGS, DISPLAYCONFIG_PATH_ACTIVE);
    }

    public static void PathSetSourceModeIdx(byte[] b, int off, uint idx) => WriteUInt32(b, off + P_SRC_MODEIDX, idx);
    public static void PathSetTargetModeIdx(byte[] b, int off, uint idx) => WriteUInt32(b, off + P_TGT_MODEIDX, idx);

    public static void PathSetTargetInfo(byte[] b, int off, uint rotation, uint scaling, uint refNum, uint refDen)
    {
        WriteUInt32(b, off + P_ROTATION, rotation);
        WriteUInt32(b, off + P_SCALING, scaling);
        WriteUInt32(b, off + P_REFNUM, refNum);
        WriteUInt32(b, off + P_REFDEN, refDen);
        WriteUInt32(b, off + P_SCANLINE, SCANLINE_ORDERING_PROGRESSIVE);
    }

    // ---------- 模式字段访问 ----------

    public static uint ModeInfoType(byte[] b, int stride, int i) => ReadUInt32(b, i * stride + M_INFOTYPE);
    public static uint ModeId(byte[] b, int stride, int i) => ReadUInt32(b, i * stride + M_ID);
    public static LUID ModeAdapter(byte[] b, int stride, int i) => ReadLUID(b, i * stride + M_ADAPTER);

    public static (uint W, uint H, uint Fmt, int X, int Y) ModeReadSource(byte[] b, int stride, int i)
    {
        int o = i * stride + M_UNION;
        return (ReadUInt32(b, o), ReadUInt32(b, o + 4), ReadUInt32(b, o + 8), ReadInt32(b, o + 12), ReadInt32(b, o + 16));
    }

    public static void ModeWriteSource(byte[] b, int stride, int i, uint w, uint h, uint fmt, int x, int y)
    {
        int o = i * stride + M_UNION;
        WriteUInt32(b, o, w);
        WriteUInt32(b, o + 4, h);
        WriteUInt32(b, o + 8, fmt);
        WriteInt32(b, o + 12, x);
        WriteInt32(b, o + 16, y);
    }

    public static (ulong PixelRate, uint HN, uint HD, uint VN, uint VD, uint Cx, uint Cy) ModeReadTarget(byte[] b, int stride, int i)
    {
        int o = i * stride + M_UNION;
        return (ReadUInt64(b, o), ReadUInt32(b, o + 8), ReadUInt32(b, o + 12),
                ReadUInt32(b, o + 16), ReadUInt32(b, o + 20), ReadUInt32(b, o + 24), ReadUInt32(b, o + 28));
    }

    public static void ModeWriteTarget(byte[] b, int stride, int i, ulong pixelRate, uint hn, uint hd, uint vn, uint vd, uint cx, uint cy)
    {
        int o = i * stride + M_UNION;
        WriteUInt64(b, o, pixelRate);
        WriteUInt32(b, o + 8, hn);
        WriteUInt32(b, o + 12, hd);
        WriteUInt32(b, o + 16, vn);
        WriteUInt32(b, o + 20, vd);
        WriteUInt32(b, o + 24, cx);
        WriteUInt32(b, o + 28, cy);
    }

    // ---------- 设备信息查询 ----------

    public static string GetSourceGdiName(LUID adapterId, uint sourceId)
    {
        var req = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        req.Header.Type = DCI_GET_SOURCE_NAME;
        req.Header.Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
        req.Header.AdapterId = adapterId;
        req.Header.Id = sourceId;
        return DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS ? req.ViewGdiDeviceName : string.Empty;
    }

    public static (string FriendlyName, string DevicePath, uint OutputTechnology) GetTargetName(LUID adapterId, uint targetId)
    {
        var req = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        req.Header.Type = DCI_GET_TARGET_NAME;
        req.Header.Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        req.Header.AdapterId = adapterId;
        req.Header.Id = targetId;
        if (DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS)
            return (req.MonitorFriendlyDeviceName, req.MonitorDevicePath, req.OutputTechnology);
        return (string.Empty, string.Empty, 0);
    }

    public static string GetAdapterName(LUID adapterId)
    {
        var req = new DISPLAYCONFIG_ADAPTER_NAME();
        req.Header.Type = DCI_GET_ADAPTER_NAME;
        req.Header.Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>();
        req.Header.AdapterId = adapterId;
        req.Header.Id = 0;
        return DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS ? req.AdapterDevicePath : string.Empty;
    }

    public static (bool Supported, bool Enabled) GetAdvancedColor(LUID adapterId, uint targetId)
    {
        var req = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
        req.Header.Type = DCI_GET_ADVANCED_COLOR_INFO;
        req.Header.Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>();
        req.Header.AdapterId = adapterId;
        req.Header.Id = targetId;
        if (DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS)
            return ((req.Value & 0x2) != 0, (req.Value & 0x1) != 0);
        return (false, false);
    }

    public static bool SetAdvancedColor(LUID adapterId, uint targetId, bool enable)
    {
        var req = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE();
        req.Header.Type = DCI_SET_ADVANCED_COLOR_STATE;
        req.Header.Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>();
        req.Header.AdapterId = adapterId;
        req.Header.Id = targetId;
        req.Value = enable ? 1u : 0u;
        return DisplayConfigSetDeviceInfo(ref req) == ERROR_SUCCESS;
    }

    public static (uint Width, uint Height, DISPLAYCONFIG_TARGET_MODE Mode)? GetPreferredMode(LUID adapterId, uint targetId)
    {
        var req = new DISPLAYCONFIG_TARGET_PREFERRED_MODE();
        req.Header.Type = DCI_GET_TARGET_PREFERRED_MODE;
        req.Header.Size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_PREFERRED_MODE>();
        req.Header.AdapterId = adapterId;
        req.Header.Id = targetId;
        if (DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS)
            return (req.Width, req.Height, req.TargetMode);
        return null;
    }

    public static string DescribeError(int hr) => hr switch
    {
        ERROR_SUCCESS => "成功",
        87 => "参数无效 (0x57)",
        50 => "系统未运行 WDDM 显示驱动 (0x32)",
        5 => "无法访问当前桌面会话 (0x5)",
        31 => "系统发生了常规故障 (0x1F)",
        1610 => "找不到可用的源/目标模式组合 (0x64A)",
        ERROR_INSUFFICIENT_BUFFER => "缓冲区不足 (0x7A)",
        _ => $"错误代码 0x{hr:X8}"
    };
}
