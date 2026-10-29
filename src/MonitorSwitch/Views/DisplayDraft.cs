using MonitorSwitch.Models;

namespace MonitorSwitch.Views;

/// <summary>
/// 显示器页的“待应用草稿”。
///
/// 设计目的：显示器页的所有改动（分辨率/刷新率/方向/HDR/启用/主屏/排列位置）
/// 都先写进这里，只有用户点“应用”才真正下发到系统，避免改一个下拉框
/// 就让屏幕闪一下。草稿以 SourceGdiName 为键，与 <see cref="LiveDisplay"/> 对应。
/// </summary>
internal sealed class DisplayDraft
{
    /// <summary>按源设备名索引的单屏草稿。</summary>
    internal sealed class Entry
    {
        public uint Width { get; set; }
        public uint Height { get; set; }
        public double RefreshHz { get; set; }
        public uint RefreshNum { get; set; }
        public uint RefreshDen { get; set; } = 1;
        public uint Rotation { get; set; } = 1;
        public bool Hdr { get; set; }
        public bool Active { get; set; } = true;
        public int X { get; set; }
        public int Y { get; set; }

        public Entry Clone() => (Entry)MemberwiseClone();
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>系统侧基线（用于判断草稿是否真的偏离了当前状态）。</summary>
    private readonly Dictionary<string, Entry> _baseline = new(StringComparer.Ordinal);

    /// <summary>
    /// 草稿是否与最近一次读取到的系统状态不同。
    /// 逐字段比较而不是靠“改过就算脏”的标志位 —— 用户把下拉框改回原值后，
    /// 提示条应当自动消失，而不是一直挂着。
    /// </summary>
    public bool IsDirty
    {
        get
        {
            if (_entries.Count != _baseline.Count) return true;
            foreach (var (key, cur) in _entries)
            {
                if (!_baseline.TryGetValue(key, out var baseEntry)) return true;
                if (!SameAs(cur, baseEntry)) return true;
            }
            return false;
        }
    }

    private static bool SameAs(Entry a, Entry b) =>
        a.Width == b.Width &&
        a.Height == b.Height &&
        Math.Abs(a.RefreshHz - b.RefreshHz) < 0.01 &&
        a.Rotation == b.Rotation &&
        a.Hdr == b.Hdr &&
        a.Active == b.Active &&
        a.X == b.X &&
        a.Y == b.Y;

    /// <summary>从当前系统状态重建草稿（丢弃未应用的修改）。</summary>
    public void Reset(IEnumerable<LiveDisplay> displays)
    {
        _entries.Clear();
        _baseline.Clear();
        foreach (var d in displays)
        {
            var entry = new Entry
            {
                Width = d.W,
                Height = d.H,
                RefreshHz = d.RefreshHz,
                RefreshNum = d.RefreshNum,
                RefreshDen = d.RefreshDen,
                Rotation = d.Rotation,
                Hdr = d.HdrEnabled,
                Active = true,
                X = d.X,
                Y = d.Y,
            };
            _entries[d.SourceGdiName] = entry;
            _baseline[d.SourceGdiName] = entry.Clone();
        }
    }

    public Entry? Get(string sourceGdiName) =>
        _entries.TryGetValue(sourceGdiName, out var e) ? e : null;

    /// <summary>就地修改某个显示器的草稿。</summary>
    public void Edit(string sourceGdiName, Action<Entry> mutate)
    {
        var e = Get(sourceGdiName);
        if (e is null) return;
        mutate(e);
    }

    /// <summary>整体平移桌面坐标（用于“设为主显示器”）。</summary>
    public void ShiftAll(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        foreach (var e in _entries.Values)
        {
            e.X += dx;
            e.Y += dy;
        }
    }

    /// <summary>把草稿套用到一份配置快照上。</summary>
    public void ApplyTo(DisplayProfile target)
    {
        foreach (var mon in target.Monitors)
        {
            var e = Get(mon.SourceGdiDeviceName);
            if (e is null) continue;

            bool oldPortrait = mon.Rotation is 2 or 4;
            mon.Rotation = e.Rotation;
            bool newPortrait = mon.Rotation is 2 or 4;

            // 源模式始终按面板朝向：旋转变化时交换宽高
            if (oldPortrait != newPortrait)
            {
                (mon.SourceWidth, mon.SourceHeight) = (mon.SourceHeight, mon.SourceWidth);
            }

            // 草稿里的 Width/Height 是桌面朝向，需要按当前旋转换算回面板朝向
            uint srcW = newPortrait ? e.Height : e.Width;
            uint srcH = newPortrait ? e.Width : e.Height;
            mon.SourceWidth = srcW;
            mon.SourceHeight = srcH;
            mon.PanelWidth = srcW;
            mon.PanelHeight = srcH;

            mon.RefreshHz = e.RefreshHz;
            mon.RefreshNumerator = e.RefreshNum;
            mon.RefreshDenominator = e.RefreshDen;
            mon.Hdr = e.Hdr;
            mon.Active = e.Active;
            mon.PositionX = e.X;
            mon.PositionY = e.Y;
        }
    }
}
