using System.Runtime.InteropServices;
using MonitorSwitch.Interop;
using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

/// <summary>
/// 基于 CCD API 的显示器配置核心服务：读取、捕获模板、应用模板、切换投影模式。
/// </summary>
public sealed class DisplayService
{
    public static DisplayService Instance { get; } = new();

    /// <summary>显示器拓扑变化（WM_DISPLAYCHANGE 或本工具应用配置后触发，已调度到 UI 线程）。</summary>
    public event Action? DisplaysChanged;

    public void RaiseDisplaysChanged()
    {
        var app = System.Windows.Application.Current;
        if (app is null) DisplaysChanged?.Invoke();
        else app.Dispatcher.BeginInvoke(() => DisplaysChanged?.Invoke());
    }

    // ---------------------------------------------------------------- 读取

    public List<LiveDisplay> GetDisplays()
    {
        var q = CcdNative.Query(CcdNative.QDC_ONLY_ACTIVE_PATHS);

        var monitorMap = new Dictionary<string, (GdiNative.RECT Rect, uint Dpi)>(StringComparer.OrdinalIgnoreCase);
        GdiNative.MonitorEnumProc monitorCallback = (IntPtr hMonitor, IntPtr hdc, ref GdiNative.RECT rc, IntPtr data) =>
        {
            var mi = new GdiNative.MONITORINFOEXW();
            mi.CbSize = Marshal.SizeOf<GdiNative.MONITORINFOEXW>();
            if (GdiNative.GetMonitorInfoW(hMonitor, ref mi))
            {
                uint dpi = 96;
                try { GdiNative.GetDpiForMonitor(hMonitor, 0, out dpi, out _); } catch { }
                monitorMap[mi.SzDevice] = (mi.RcMonitor, dpi);
            }
            return true;
        };
        GdiNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, monitorCallback, IntPtr.Zero);

        var list = new List<LiveDisplay>();

        for (int i = 0; i < q.PathCount; i++)
        {
            var srcAdapter = CcdNative.PathSourceAdapter(q.PathBuffer, q.PathStride, i);
            uint srcId = CcdNative.PathSourceId(q.PathBuffer, q.PathStride, i);
            var tgtAdapter = CcdNative.PathTargetAdapter(q.PathBuffer, q.PathStride, i);
            uint tgtId = CcdNative.PathTargetId(q.PathBuffer, q.PathStride, i);

            var srcName = CcdNative.GetSourceGdiName(srcAdapter, srcId);
            var (monName, devPath, tech) = CcdNative.GetTargetName(tgtAdapter, tgtId);

            var d = new LiveDisplay
            {
                TargetDevicePath = devPath,
                MonitorName = FriendlyMonitorName(monName, srcName),
                AdapterName = GdiNative.GetAdapterFriendlyName(srcName),
                SourceGdiName = srcName,
                Connector = tech,
                Rotation = CcdNative.PathRotation(q.PathBuffer, q.PathStride, i),
                Scaling = CcdNative.PathScaling(q.PathBuffer, q.PathStride, i),
            };
            d.Number = ParseDisplayNumber(srcName);

            uint srcIdx = CcdNative.PathSourceModeIdx(q.PathBuffer, q.PathStride, i);
            if (srcIdx != CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID && srcIdx < (uint)q.ModeCount)
            {
                var sm = CcdNative.ModeReadSource(q.ModeBuffer, q.ModeStride, (int)srcIdx);
                // 源模式是面板朝向；桌面上的宽高需要按旋转交换
                d.SourceW = sm.W;
                d.SourceH = sm.H;
                bool portrait = d.Rotation is 2 or 4;
                d.W = portrait ? sm.H : sm.W;
                d.H = portrait ? sm.W : sm.H;
                d.X = sm.X;
                d.Y = sm.Y;
            }

            uint tgtIdx = CcdNative.PathTargetModeIdx(q.PathBuffer, q.PathStride, i);
            if (tgtIdx != CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID && tgtIdx < (uint)q.ModeCount)
            {
                var tm = CcdNative.ModeReadTarget(q.ModeBuffer, q.ModeStride, (int)tgtIdx);
                d.PanelW = tm.Cx;
                d.PanelH = tm.Cy;
                d.PixelRate = tm.PixelRate;
                d.HSyncNum = tm.HN;
                d.HSyncDen = Math.Max(1u, tm.HD);
                d.VSyncNum = tm.VN;
                d.VSyncDen = Math.Max(1u, tm.VD);
            }

            var rr = CcdNative.PathRefreshRate(q.PathBuffer, q.PathStride, i);
            if (rr.Den > 0 && rr.Num > 0)
            {
                d.RefreshNum = rr.Num;
                d.RefreshDen = rr.Den;
                d.RefreshHz = (double)rr.Num / rr.Den;
            }
            else if (d.VSyncDen > 0 && d.VSyncNum > 0)
            {
                d.RefreshNum = d.VSyncNum;
                d.RefreshDen = d.VSyncDen;
                d.RefreshHz = (double)d.VSyncNum / d.VSyncDen;
            }

            if (!string.IsNullOrEmpty(srcName) && monitorMap.TryGetValue(srcName, out var mm))
            {
                d.MonitorRect = mm.Rect;
            }
            // 每显示器缩放：Shcore.GetDpiForMonitor 在本机返回恒为 96，改用注册表 PerMonitorSettings
            d.ScalePercent = GdiNative.GetMonitorScalePercent("", srcName);
            d.IsPrimary = d.X == 0 && d.Y == 0;

            var (hdrSup, hdrEn) = CcdNative.GetAdvancedColor(tgtAdapter, tgtId);
            d.HdrSupported = hdrSup;
            d.HdrEnabled = hdrEn;

            if (!string.IsNullOrEmpty(srcName))
                FillModeLists(d);

            AppPaths.Log($"显示器 #{d.Number} \"{d.MonitorName}\" 桌面{d.W}×{d.H} 源{d.SourceW}×{d.SourceH} " +
                         $"@({d.X},{d.Y}) {DisplayProfile.FormatHz(d.RefreshHz)} 旋转{d.Rotation} 缩放{d.ScalePercent:0}% " +
                         $"src={srcName} [路径步长{q.PathStride} 模式步长{q.ModeStride}]");

            list.Add(d);
        }

        list.Sort((a, b) => a.Number.CompareTo(b.Number));
        return list;
    }

    /// <summary>
    /// 枚举某显示器可用的分辨率与刷新率。
    /// GDI 的 EnumDisplaySettingsEx 在竖屏时同样返回“桌面朝向”的尺寸
    /// （如 2560×1600 面板旋转 90° 时报告 1600×2560），因此这里直接使用原始值，
    /// 再按当前旋转是否已生效做一次兜底匹配。
    /// </summary>
    /// <summary>
    /// CCD 的 MonitorFriendlyDeviceName 在内置屏/部分 EDID 缺失的设备上为空，
    /// 依次回退到 PnP 监视器名与目标设备路径中的型号片段，避免一律显示“未知显示器”。
    /// </summary>
    private static string FriendlyMonitorName(string ccdName, string srcGdiName)
    {
        var name = (ccdName ?? string.Empty).TrimEnd('\0').Trim();
        if (!string.IsNullOrWhiteSpace(name)) return name;

        var pnp = (GdiNative.GetMonitorPnpName(srcGdiName) ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(pnp)) return "未知显示器";
        // 内置屏在 GDI 中通常只报 Generic PnP Monitor
        if (pnp.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase))
            return "内置显示屏";
        return pnp;
    }

    private static void FillModeLists(LiveDisplay d)
    {
        var gdiModes = GdiNative.EnumerateModes(d.SourceGdiName);
        var by32 = gdiModes.Where(m => m.Bpp == 32).ToList();
        if (by32.Count == 0) by32 = gdiModes;

        // GDI 报告的是桌面朝向的尺寸；当前桌面尺寸是 d.W × d.H
        var candidates = by32.Select(m => (W: m.W, H: m.H, Freq: m.Freq)).ToList();

        // 兜底：某些驱动/系统版本仍按面板朝向报告，若当前尺寸一个都匹配不上就交换
        bool matches = candidates.Any(c => c.W == d.W && c.H == d.H);
        if (!matches && d.Rotation is 2 or 4)
        {
            var swapped = candidates.Select(c => (W: c.H, H: c.W, Freq: c.Freq)).ToList();
            if (swapped.Any(c => c.W == d.W && c.H == d.H)) candidates = swapped;
        }

        var dims = new SortedSet<(uint W, uint H)>(Comparer<(uint W, uint H)>.Create((a, b) =>
        {
            int c = ((long)b.W * b.H).CompareTo((long)a.W * a.H);
            if (c != 0) return c;
            c = b.W.CompareTo(a.W);
            return c != 0 ? c : b.H.CompareTo(a.H);
        }));

        foreach (var (w, h, _) in candidates)
            if (w > 0 && h > 0)
                dims.Add((w, h));

        foreach (var dim in dims) d.Resolutions.Add(dim);

        var rates = new SortedSet<uint>();
        foreach (var (w, h, freq) in candidates)
            if (w == d.W && h == d.H && freq > 0)
                rates.Add(freq);

        // 当前刷新率可能不在 GDI 列表中（如通过 CCD 设置的时序），补进去以免下拉框空白
        if (rates.Count > 0 && d.RefreshHz > 0)
        {
            uint rounded = (uint)Math.Round(d.RefreshHz);
            if (Math.Abs(d.RefreshHz - rounded) < 0.5 && !rates.Contains(rounded))
                rates.Add(rounded);
        }

        foreach (var r in rates.OrderByDescending(x => x))
            d.RefreshRates.Add(r);

        if (d.Resolutions.Count == 0 && d.W > 0)
            d.Resolutions.Add((d.W, d.H));
    }

    public sealed record InactiveTarget(
        string TargetDevicePath, string MonitorName, string AdapterName,
        uint PreferredW, uint PreferredH);

    /// <summary>当前已连接但未在桌面中启用的显示器。</summary>
    public List<InactiveTarget> GetInactiveTargets()
    {
        var activeQ = CcdNative.Query(CcdNative.QDC_ONLY_ACTIVE_PATHS);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < activeQ.PathCount; i++)
        {
            var (_, devPath, _) = CcdNative.GetTargetName(
                CcdNative.PathTargetAdapter(activeQ.PathBuffer, activeQ.PathStride, i),
                CcdNative.PathTargetId(activeQ.PathBuffer, activeQ.PathStride, i));
            if (!string.IsNullOrEmpty(devPath)) active.Add(devPath);
        }

        var allQ = CcdNative.Query(CcdNative.QDC_ALL_PATHS);
        var result = new List<InactiveTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < allQ.PathCount; i++)
        {
            var tgtAdapter = CcdNative.PathTargetAdapter(allQ.PathBuffer, allQ.PathStride, i);
            var tgtId = CcdNative.PathTargetId(allQ.PathBuffer, allQ.PathStride, i);
            var t = CcdNative.GetTargetName(tgtAdapter, tgtId);
            if (string.IsNullOrEmpty(t.DevicePath) || active.Contains(t.DevicePath) || !seen.Add(t.DevicePath))
                continue;
            // 只有“可用但未启用”的目标才值得列出
            if (CcdNative.PathTargetAvailable(allQ.PathBuffer, allQ.PathStride, i) == 0)
                continue;
            var pref = CcdNative.GetPreferredMode(tgtAdapter, tgtId);
            result.Add(new InactiveTarget(
                t.DevicePath,
                FriendlyMonitorName(t.FriendlyName, ""),
                CcdNative.GetAdapterName(tgtAdapter),
                pref?.Width ?? 0, pref?.Height ?? 0));
        }
        return result;
    }

    /// <summary>为指定（未启用的）目标合成一份快照，用于“启用显示器”。</summary>
    public MonitorSnapshot? BuildSnapshotForTarget(string targetDevicePath, int posX, int posY)
    {
        var allQ = CcdNative.Query(CcdNative.QDC_ALL_PATHS);
        for (int i = 0; i < allQ.PathCount; i++)
        {
            var tgtAdapter = CcdNative.PathTargetAdapter(allQ.PathBuffer, allQ.PathStride, i);
            var tgtId = CcdNative.PathTargetId(allQ.PathBuffer, allQ.PathStride, i);
            var t = CcdNative.GetTargetName(tgtAdapter, tgtId);
            if (!string.Equals(t.DevicePath, targetDevicePath, StringComparison.OrdinalIgnoreCase)) continue;
            var pref = CcdNative.GetPreferredMode(tgtAdapter, tgtId);
            if (pref is null) return null;
            var (w, h, mode) = pref.Value;
            if (w == 0 || h == 0) return null;
            return new MonitorSnapshot
            {
                TargetDevicePath = t.DevicePath,
                MonitorName = FriendlyMonitorName(t.FriendlyName, ""),
                AdapterName = CcdNative.GetAdapterName(tgtAdapter),
                SourceGdiDeviceName = "",
                Active = true,
                PositionX = posX,
                PositionY = posY,
                SourceWidth = w,
                SourceHeight = h,
                PanelWidth = w,
                PanelHeight = h,
                RefreshNumerator = mode.VSyncFreq.Numerator,
                RefreshDenominator = Math.Max(1u, mode.VSyncFreq.Denominator),
                RefreshHz = (double)mode.VSyncFreq.Numerator / Math.Max(1u, mode.VSyncFreq.Denominator),
                Rotation = CcdNative.ROTATION_IDENTITY,
                Scaling = CcdNative.SCALING_IDENTITY,
                Hdr = false,
                PixelRate = mode.PixelRate,
                HSyncNumerator = mode.HSyncFreq.Numerator,
                HSyncDenominator = Math.Max(1u, mode.HSyncFreq.Denominator),
                VSyncNumerator = mode.VSyncFreq.Numerator,
                VSyncDenominator = Math.Max(1u, mode.VSyncFreq.Denominator),
            };
        }
        return null;
    }

    // ---------------------------------------------------------------- 捕获

    public DisplayProfile CaptureCurrent()
    {
        var profile = new DisplayProfile { Name = "" };
        foreach (var d in GetDisplays())
            profile.Monitors.Add(d.ToSnapshot());
        return profile;
    }

    /// <summary>当前配置是否与模板一致（用于“当前使用中”标记）。</summary>
    public bool MatchesCurrent(DisplayProfile profile) => MatchesCurrent(profile, GetDisplays());

    public bool MatchesCurrent(DisplayProfile profile, List<LiveDisplay> cur)
    {
        var active = profile.Monitors.Where(m => m.Active).ToList();
        if (active.Count != cur.Count) return false;

        var map = new Dictionary<string, LiveDisplay>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in cur)
            if (!string.IsNullOrEmpty(d.TargetDevicePath))
                map[d.TargetDevicePath] = d;

        foreach (var mon in active)
        {
            if (!map.TryGetValue(mon.TargetDevicePath, out var d)) return false;
            if (d.X != mon.PositionX || d.Y != mon.PositionY) return false;
            // 比较桌面尺寸：模板中的 Source* 是面板朝向，需要按旋转换算
            if (d.W != mon.DesktopWidth || d.H != mon.DesktopHeight) return false;
            if (Math.Abs(d.RefreshHz - mon.RefreshHz) > 0.5) return false;
            if (d.Rotation != mon.Rotation) return false;
            if (d.HdrEnabled != mon.Hdr) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- 应用

    private const uint DegradeScaling = 1;
    private const uint DegradeTargetMode = 2;
    private const uint DegradeKeepRotScale = 4;

    private readonly struct AttemptSpec
    {
        public bool TopologyOnly { get; init; }
        public uint Degrade { get; init; }
        public bool AllowChanges { get; init; }
    }

    private static readonly AttemptSpec[] AttemptLadder =
    {
        new() { Degrade = 0, AllowChanges = true },
        new() { Degrade = 0, AllowChanges = false },
        new() { Degrade = DegradeScaling, AllowChanges = true },
        new() { Degrade = DegradeTargetMode | DegradeKeepRotScale, AllowChanges = true },
        new() { Degrade = DegradeTargetMode, AllowChanges = true },
        new() { TopologyOnly = true },
    };

    /// <summary>应用标志组合变体：不同驱动/系统版本对标志的要求不同，逐一尝试。</summary>
    private static readonly uint[] ApplyFlagVariants =
    {
        CcdNative.SDC_USE_SUPPLIED_DISPLAY_CONFIG | CcdNative.SDC_SAVE_TO_DATABASE | CcdNative.SDC_ALLOW_CHANGES,
        CcdNative.SDC_USE_SUPPLIED_DISPLAY_CONFIG | CcdNative.SDC_SAVE_TO_DATABASE | CcdNative.SDC_ALLOW_CHANGES
            | CcdNative.SDC_FORCE_MODE_ENUMERATION,
        CcdNative.SDC_USE_SUPPLIED_DISPLAY_CONFIG | CcdNative.SDC_SAVE_TO_DATABASE | CcdNative.SDC_ALLOW_CHANGES
            | CcdNative.SDC_VIRTUAL_MODE_AWARE,
        CcdNative.SDC_USE_SUPPLIED_DISPLAY_CONFIG | CcdNative.SDC_ALLOW_CHANGES,
        CcdNative.SDC_USE_SUPPLIED_DISPLAY_CONFIG | CcdNative.SDC_ALLOW_CHANGES
            | CcdNative.SDC_VIRTUAL_MODE_AWARE | CcdNative.SDC_VIRTUAL_REFRESH_RATE_AWARE,
    };

    private sealed class PathCandidate
    {
        public int Index;
        public string SrcGdi = "";
        public string SourceKey = "";
        public bool Active;
    }

    public ApplyResult ApplyProfile(DisplayProfile profile)
    {
        try
        {
            return ApplyProfileCore(profile);
        }
        catch (Exception ex)
        {
            AppPaths.Log("ApplyProfile 异常: " + ex);
            return ApplyResult.Fail("应用配置时发生内部错误：" + ex.Message);
        }
    }

    private ApplyResult ApplyProfileCore(DisplayProfile profile)
    {
        var active = profile.Monitors.Where(m => m.Active).ToList();
        if (active.Count == 0)
            return ApplyResult.Fail("该配置没有启用任何显示器。");

        var q = CcdNative.Query(CcdNative.QDC_ALL_PATHS);

        var candidates = new Dictionary<string, List<PathCandidate>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < q.PathCount; i++)
        {
            var t = CcdNative.GetTargetName(
                CcdNative.PathTargetAdapter(q.PathBuffer, q.PathStride, i),
                CcdNative.PathTargetId(q.PathBuffer, q.PathStride, i));
            if (string.IsNullOrEmpty(t.DevicePath)) continue;
            if (!candidates.TryGetValue(t.DevicePath, out var list))
                candidates[t.DevicePath] = list = new List<PathCandidate>();
            list.Add(new PathCandidate
            {
                Index = i,
                SrcGdi = CcdNative.GetSourceGdiName(
                    CcdNative.PathSourceAdapter(q.PathBuffer, q.PathStride, i),
                    CcdNative.PathSourceId(q.PathBuffer, q.PathStride, i)),
                SourceKey = SourceKeyOf(q, i),
                Active = (CcdNative.PathFlags(q.PathBuffer, q.PathStride, i) & CcdNative.DISPLAYCONFIG_PATH_ACTIVE) != 0,
            });
        }

        // 逐台匹配路径
        var chosen = new List<(MonitorSnapshot Mon, int PathIndex)>();
        var missing = new List<string>();
        var usedSources = new HashSet<string>();
        var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mon in active)
        {
            if (!candidates.TryGetValue(mon.TargetDevicePath, out var cands) || cands.Count == 0)
            {
                missing.Add(string.IsNullOrEmpty(mon.MonitorName) ? mon.TargetDevicePath : mon.MonitorName);
                continue;
            }

            PathCandidate? pick = null;

            // 1) 克隆组共享同一源
            if (mon.SourceGdiDeviceName.Length > 0 && assigned.TryGetValue(mon.SourceGdiDeviceName, out var wantSrc))
                pick = cands.FirstOrDefault(c => c.SourceKey == wantSrc);
            // 2) 与模板相同的源（优先当前已激活的那条）
            if (pick is null && mon.SourceGdiDeviceName.Length > 0)
                pick = cands.FirstOrDefault(c => c.SrcGdi == mon.SourceGdiDeviceName && c.Active && !usedSources.Contains(c.SourceKey))
                       ?? cands.FirstOrDefault(c => c.SrcGdi == mon.SourceGdiDeviceName && !usedSources.Contains(c.SourceKey))
                       ?? cands.FirstOrDefault(c => c.SrcGdi == mon.SourceGdiDeviceName);
            // 3) 任一空闲源
            if (pick is null)
                pick = cands.FirstOrDefault(c => !usedSources.Contains(c.SourceKey));
            if (pick is null)
                pick = cands.FirstOrDefault(c => c.Active) ?? cands[0];

            usedSources.Add(pick.SourceKey);
            if (mon.SourceGdiDeviceName.Length > 0)
                assigned[mon.SourceGdiDeviceName] = pick.SourceKey;
            chosen.Add((mon, pick.Index));
        }

        if (missing.Count > 0)
            return ApplyResult.Fail("以下显示器当前未连接，无法应用：" + string.Join("、", missing));

        var errors = new List<string>();
        foreach (var attempt in AttemptLadder)
        {
            int hr;
            if (attempt.TopologyOnly)
            {
                var pathBuf = new byte[chosen.Count * q.PathStride + 64];
                for (int k = 0; k < chosen.Count; k++)
                {
                    int outPe = k * q.PathStride;
                    CcdNative.PathInitActive(pathBuf, outPe, q.PathBuffer, chosen[k].PathIndex * q.PathStride, q.PathStride);
                    CcdNative.PathSetSourceModeIdx(pathBuf, outPe, CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID);
                    CcdNative.PathSetTargetModeIdx(pathBuf, outPe, CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID);
                }
                hr = CcdNative.SetDisplayConfig((uint)chosen.Count, pathBuf, 0, null,
                    CcdNative.SDC_TOPOLOGY_SUPPLIED | CcdNative.SDC_VALIDATE);
                if (hr == CcdNative.ERROR_SUCCESS)
                {
                    hr = CcdNative.SetDisplayConfig((uint)chosen.Count, pathBuf, 0, null,
                        CcdNative.SDC_TOPOLOGY_SUPPLIED | CcdNative.SDC_APPLY | CcdNative.SDC_ALLOW_PATH_ORDER_CHANGES);
                    if (hr == CcdNative.ERROR_SUCCESS)
                        return FinishApply(active);
                }
                errors.Add("仅拓扑：" + CcdNative.DescribeError(hr));
                continue;
            }

            var (pathBuf2, modeBuf, modeCount) = BuildBuffers(q, chosen, attempt.Degrade);

            bool applied = false;
            foreach (var baseVariant in ApplyFlagVariants)
            {
                var variant = attempt.AllowChanges ? baseVariant : baseVariant & ~CcdNative.SDC_ALLOW_CHANGES;
                if (variant == 0) continue;

                // 验证与应用使用同一组标志（APPLY 换成 VALIDATE），确保验证结果对应用有效
                var validateFlags = variant | CcdNative.SDC_VALIDATE;
                hr = CcdNative.SetDisplayConfig((uint)chosen.Count, pathBuf2, (uint)modeCount, modeBuf, validateFlags);
                if (hr != CcdNative.ERROR_SUCCESS)
                {
                    errors.Add(CcdNative.DescribeError(hr));
                    continue;
                }

                hr = CcdNative.SetDisplayConfig((uint)chosen.Count, pathBuf2, (uint)modeCount, modeBuf, variant | CcdNative.SDC_APPLY);
                if (hr == CcdNative.ERROR_SUCCESS)
                {
                    AppPaths.Log($"ApplyProfile 成功 (flags=0x{variant:X}, degrade={attempt.Degrade})");
                    applied = true;
                    break;
                }
                errors.Add("应用阶段[" + variant.ToString("X") + "]：" + CcdNative.DescribeError(hr));
            }
            if (applied)
                return FinishApply(active);
        }

        AppPaths.Log("ApplyProfile 失败: " + string.Join(" | ", errors));
        return ApplyResult.Fail("无法应用该显示配置（" + string.Join("；", errors) + "）。");
    }

    private static string SourceKeyOf(CcdNative.QueryResult qq, int pathIndex) =>
        $"{CcdNative.PathSourceAdapter(qq.PathBuffer, qq.PathStride, pathIndex)}:{CcdNative.PathSourceId(qq.PathBuffer, qq.PathStride, pathIndex)}";

    private static (byte[] PathBuf, byte[] ModeBuf, int ModeCount) BuildBuffers(
        CcdNative.QueryResult q,
        List<(MonitorSnapshot Mon, int PathIndex)> chosen,
        uint degrade)
    {
        int pStride = q.PathStride;
        int mStride = q.ModeStride;
        var pathBuf = new byte[chosen.Count * pStride + 64];
        var modeBuf = new byte[(chosen.Count * 2 + 2) * mStride + 128];
        int modeUsed = 0;
        var srcMap = new Dictionary<string, int>();

        for (int k = 0; k < chosen.Count; k++)
        {
            var (mon, pIdx) = chosen[k];
            int outPe = k * pStride;
            CcdNative.PathInitActive(pathBuf, outPe, q.PathBuffer, pIdx * pStride, pStride);

            uint srcId = CcdNative.PathSourceId(q.PathBuffer, pStride, pIdx);
            var srcAdapter = CcdNative.PathSourceAdapter(q.PathBuffer, pStride, pIdx);
            string skey = $"{srcAdapter}:{srcId}";

            // 源模式：分辨率 + 位置（克隆共享同一源模式）
            // 注意：SourceWidth/Height 是面板朝向，与 CCD 源模式一致，无需按旋转交换
            if (mon.SourceWidth > 0 && mon.SourceHeight > 0)
            {
                if (!srcMap.TryGetValue(skey, out int smIdx))
                {
                    smIdx = modeUsed++;
                    int me = smIdx * mStride;
                    uint origSrcIdx = CcdNative.PathSourceModeIdx(q.PathBuffer, pStride, pIdx);
                    if (origSrcIdx != CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID && origSrcIdx < (uint)q.ModeCount)
                        Array.Copy(q.ModeBuffer, origSrcIdx * mStride, modeBuf, me, mStride);
                    else
                        Array.Clear(modeBuf, me, mStride);
                    CcdNative.WriteUInt32(modeBuf, me, CcdNative.MODE_INFO_TYPE_SOURCE);
                    CcdNative.WriteUInt32(modeBuf, me + 4, srcId);
                    CcdNative.WriteLUID(modeBuf, me + 8, srcAdapter);
                    CcdNative.ModeWriteSource(modeBuf, mStride, smIdx,
                        mon.SourceWidth, mon.SourceHeight, CcdNative.PIXELFORMAT_32BPP, mon.PositionX, mon.PositionY);
                    srcMap[skey] = smIdx;
                }
                CcdNative.PathSetSourceModeIdx(pathBuf, outPe, (uint)srcMap[skey]);
            }
            else
            {
                CcdNative.PathSetSourceModeIdx(pathBuf, outPe, CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID);
            }

            // 目标模式
            uint tgtId = CcdNative.PathTargetId(q.PathBuffer, pStride, pIdx);
            var tgtAdapter = CcdNative.PathTargetAdapter(q.PathBuffer, pStride, pIdx);
            uint origTgtIdx = CcdNative.PathTargetModeIdx(q.PathBuffer, pStride, pIdx);
            var (wantNum, wantDen) = NormalizeRefresh(mon);
            bool hasTiming = mon.PanelWidth > 0 && mon.PanelHeight > 0
                             && mon.PixelRate > 0
                             && mon.HSyncNumerator > 0 && mon.HSyncDenominator > 0
                             && mon.VSyncNumerator > 0 && mon.VSyncDenominator > 0;

            // 捕获的时序只有在“刷新率与面板尺寸都没被改动”时才依然有效。
            // 用户改了分辨率或刷新率后，旧时序与请求不符，必须交给系统重新选择，
            // 否则驱动会按旧的目标模式应用，改动静默失效。
            bool timingMatchesRequest =
                hasTiming
                && Math.Abs((double)mon.VSyncNumerator / mon.VSyncDenominator
                            - (double)wantNum / wantDen) < 0.5
                && TimingMatchesLiveMode(q, origTgtIdx, mon);

            if ((degrade & DegradeTargetMode) == 0 && timingMatchesRequest)
            {
                int tmIdx = modeUsed++;
                int te = tmIdx * mStride;
                if (origTgtIdx != CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID && origTgtIdx < (uint)q.ModeCount)
                    Array.Copy(q.ModeBuffer, origTgtIdx * mStride, modeBuf, te, mStride);
                else
                    Array.Clear(modeBuf, te, mStride);
                CcdNative.WriteUInt32(modeBuf, te, CcdNative.MODE_INFO_TYPE_TARGET);
                CcdNative.WriteUInt32(modeBuf, te + 4, tgtId);
                CcdNative.WriteLUID(modeBuf, te + 8, tgtAdapter);

                CcdNative.ModeWriteTarget(modeBuf, mStride, tmIdx,
                    mon.PixelRate,
                    mon.HSyncNumerator, mon.HSyncDenominator,
                    mon.VSyncNumerator, mon.VSyncDenominator,
                    mon.PanelWidth, mon.PanelHeight);
                CcdNative.PathSetTargetModeIdx(pathBuf, outPe, (uint)tmIdx);
            }
            else
            {
                // 不提供目标模式：由系统按当前路径参数（刷新率 + 分辨率）自行选择最佳时序
                CcdNative.PathSetTargetModeIdx(pathBuf, outPe, CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID);
            }

            // 方向 / 缩放 / 刷新率
            if ((degrade & DegradeKeepRotScale) != 0)
            {
                var curRot = CcdNative.PathRotation(q.PathBuffer, pStride, pIdx);
                var curScale = CcdNative.PathScaling(q.PathBuffer, pStride, pIdx);
                var (rn, rd) = CcdNative.PathRefreshRate(q.PathBuffer, pStride, pIdx);
                CcdNative.PathSetTargetInfo(pathBuf, outPe, curRot, curScale, rn, Math.Max(1u, rd));
            }
            else
            {
                var (rn, rd) = NormalizeRefresh(mon);
                CcdNative.PathSetTargetInfo(pathBuf, outPe, mon.Rotation,
                    (degrade & DegradeScaling) != 0 ? CcdNative.SCALING_PREFERRED : Math.Max(1u, mon.Scaling),
                    rn, rd);
            }
        }

        return (pathBuf, modeBuf, modeUsed);
    }

    /// <summary>
    /// 模板里保存的目标时序是否仍与系统当前为该路径选中的目标模式一致。
    /// 只要用户改了分辨率或刷新率，系统当前目标模式就与模板里的时序不同，
    /// 此时继续提交旧时序会被驱动忽略，导致改动静默失效。
    /// </summary>
    private static bool TimingMatchesLiveMode(CcdNative.QueryResult q, uint liveTgtIdx, MonitorSnapshot mon)
    {
        if (mon.PanelWidth == 0 || mon.PanelHeight == 0) return false;
        if (mon.SourceWidth == 0 || mon.SourceHeight == 0) return false;
        // 目标模式 activeSize 与源模式同朝向、同尺寸（面板原生朝向）
        if (mon.PanelWidth != mon.SourceWidth || mon.PanelHeight != mon.SourceHeight) return false;
        // 当前没有目标模式（例如刚通过拓扑切换）时无从比较，保守地重新选择
        if (liveTgtIdx == CcdNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID || liveTgtIdx >= (uint)q.ModeCount)
            return false;

        var live = CcdNative.ModeReadTarget(q.ModeBuffer, q.ModeStride, (int)liveTgtIdx);
        if (live.Cx != mon.PanelWidth || live.Cy != mon.PanelHeight) return false;
        if (live.PixelRate != mon.PixelRate) return false;
        if (live.VN != mon.VSyncNumerator || live.VD != mon.VSyncDenominator) return false;
        return true;
    }

    /// <summary>把模板里的刷新率规范化成 CCD 期望的有理数（避免 0/0）。</summary>
    private static (uint Num, uint Den) NormalizeRefresh(MonitorSnapshot mon)
    {
        if (mon.RefreshNumerator > 0 && mon.RefreshDenominator > 0)
            return (mon.RefreshNumerator, mon.RefreshDenominator);
        if (mon.RefreshHz > 0)
        {
            double rounded = Math.Round(mon.RefreshHz);
            if (Math.Abs(mon.RefreshHz - rounded) < 0.01)
                return ((uint)rounded, 1u);
            return ((uint)Math.Round(mon.RefreshHz * 1000), 1000u);
        }
        return (60, 1);
    }

    private ApplyResult FinishApply(List<MonitorSnapshot> active)
    {
        var warnings = ApplyHdrStates(active);
        RaiseDisplaysChanged();
        return ApplyResult.Ok(warnings);
    }

    private List<string> ApplyHdrStates(List<MonitorSnapshot> active)
    {
        var warnings = new List<string>();
        try
        {
            var q = CcdNative.Query(CcdNative.QDC_ONLY_ACTIVE_PATHS);
            var byPath = new Dictionary<string, (CcdNative.LUID Adapter, uint Id)>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < q.PathCount; i++)
            {
                var adapter = CcdNative.PathTargetAdapter(q.PathBuffer, q.PathStride, i);
                var id = CcdNative.PathTargetId(q.PathBuffer, q.PathStride, i);
                var t = CcdNative.GetTargetName(adapter, id);
                if (!string.IsNullOrEmpty(t.DevicePath))
                    byPath[t.DevicePath] = (adapter, id);
            }

            foreach (var mon in active)
            {
                if (!byPath.TryGetValue(mon.TargetDevicePath, out var ti)) continue;
                var (sup, en) = CcdNative.GetAdvancedColor(ti.Adapter, ti.Id);
                if (!sup)
                {
                    if (mon.Hdr) warnings.Add($"“{mon.MonitorName}”不支持 HDR，已跳过。");
                    continue;
                }
                if (en != mon.Hdr && !CcdNative.SetAdvancedColor(ti.Adapter, ti.Id, mon.Hdr))
                    warnings.Add($"“{mon.MonitorName}”的 HDR 状态未能切换。");
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("ApplyHdrStates 异常: " + ex);
        }
        return warnings;
    }

    // ---------------------------------------------------------------- 投影快捷模式

    public ApplyResult ApplyTopology(QuickTopology topology)
    {
        var sdc = topology switch
        {
            QuickTopology.InternalOnly => CcdNative.SDC_TOPOLOGY_INTERNAL,
            QuickTopology.Clone => CcdNative.SDC_TOPOLOGY_CLONE,
            QuickTopology.Extend => CcdNative.SDC_TOPOLOGY_EXTEND,
            QuickTopology.ExternalOnly => CcdNative.SDC_TOPOLOGY_EXTERNAL,
            _ => CcdNative.SDC_TOPOLOGY_EXTEND,
        };
        var hr = CcdNative.SetDisplayConfig(0, null, 0, null, sdc | CcdNative.SDC_APPLY);
        if (hr != CcdNative.ERROR_SUCCESS)
            return ApplyResult.Fail("切换失败：" + CcdNative.DescribeError(hr));
        RaiseDisplaysChanged();
        return ApplyResult.Ok();
    }

    // ---------------------------------------------------------------- 工具

    private static int ParseDisplayNumber(string gdiName)
    {
        if (string.IsNullOrEmpty(gdiName)) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(gdiName, @"DISPLAY(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    /// <summary>虚拟桌面总边界（用于排列画布）。</summary>
    public (int MinX, int MinY, int MaxX, int MaxY) GetVirtualBounds(List<LiveDisplay> displays)
    {
        if (displays.Count == 0) return (0, 0, 1920, 1080);
        int minX = displays.Min(d => d.X);
        int minY = displays.Min(d => d.Y);
        int maxX = displays.Max(d => d.X + (int)d.W);
        int maxY = displays.Max(d => d.Y + (int)d.H);
        return (minX, minY, maxX, maxY);
    }
}
