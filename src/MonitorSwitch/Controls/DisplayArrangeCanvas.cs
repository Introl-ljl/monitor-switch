using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MonitorSwitch.Services;

namespace MonitorSwitch.Controls;

/// <summary>排列画布中的显示器矩形（桌面像素坐标）。</summary>
public sealed class ArrangeItem : System.ComponentModel.INotifyPropertyChanged
{
    public required string Key { get; init; }
    public int Number { get; set; }
    public string Name { get; set; } = "";

    private double _x;
    public double X { get => _x; set { if (Math.Abs(_x - value) < 0.001) return; _x = value; Changed(nameof(X)); } }

    private double _y;
    public double Y { get => _y; set { if (Math.Abs(_y - value) < 0.001) return; _y = value; Changed(nameof(Y)); } }

    public double W { get; set; }
    public double H { get; set; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; Changed(nameof(IsSelected)); } }

    public bool IsPrimary { get; set; }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>
/// 仿 Windows 设置“重新排列显示器”的交互画布：拖拽移动、边缘吸附、防重叠。
///
/// 配色说明：这里所有画刷都从当前主题实时计算，不再硬编码灰阶。
/// 之前为了绕开系统强调色（青灰 #69797E）会泛绿的问题，把颜色写死成中性灰，
/// 代价是切到浅色主题时画布不会跟着变。现在强调色已由 ThemeService 统一
/// 覆盖为中性蓝，这里可以放心地重新用强调色标记选中态。
/// </summary>
public sealed class DisplayArrangeCanvas : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IList<ArrangeItem>), typeof(DisplayArrangeCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    public IList<ArrangeItem>? Items
    {
        get => (IList<ArrangeItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>拖拽结束后触发（仅当位置发生了变化）。</summary>
    public event Action<ArrangeItem>? PositionCommitted;
    public event Action<ArrangeItem>? SelectionChanged;

    private const double CanvasPad = 16;
    private const double SnapTolDip = 10;

    private double _scale = 0.1;
    private double _ox, _oy;

    private ArrangeItem? _dragItem;
    private Point _dragStartPoint;
    private double _dragItemStartX, _dragItemStartY;
    private readonly List<(double Pos, bool Vertical)> _guides = new();

    public DisplayArrangeCanvas()
    {
        Cursor = Cursors.SizeAll;
        ClipToBounds = true;
        Focusable = false;
        Loaded += (_, _) => ThemeService.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    private void OnThemeChanged() => InvalidateVisual();

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (DisplayArrangeCanvas)d;
        if (e.OldValue is IList<ArrangeItem> old)
            foreach (var item in old) item.PropertyChanged -= c.ItemPropertyChanged;
        if (e.NewValue is IList<ArrangeItem> cur)
            foreach (var item in cur) item.PropertyChanged += c.ItemPropertyChanged;
        c.InvalidateVisual();
    }

    private void ItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ArrangeItem.X) or nameof(ArrangeItem.Y) or nameof(ArrangeItem.IsSelected))
            InvalidateVisual();
    }

    // ---------------------------------------------------------------- 布局

    private void ComputeTransform()
    {
        var items = Items;
        if (items is null || items.Count == 0) return;

        double minX = items.Min(i => i.X), minY = items.Min(i => i.Y);
        double maxX = items.Max(i => i.X + i.W), maxY = items.Max(i => i.Y + i.H);
        double bw = Math.Max(1, maxX - minX), bh = Math.Max(1, maxY - minY);
        double availW = Math.Max(1, ActualWidth - CanvasPad * 2), availH = Math.Max(1, ActualHeight - CanvasPad * 2);
        _scale = Math.Clamp(Math.Min(availW / bw, availH / bh), 0.005, 0.6);
        _ox = (ActualWidth - bw * _scale) / 2 - minX * _scale;
        _oy = (ActualHeight - bh * _scale) / 2 - minY * _scale;
    }

    private Rect RectOf(ArrangeItem i) => new(i.X * _scale + _ox, i.Y * _scale + _oy,
        Math.Max(2, i.W * _scale), Math.Max(2, i.H * _scale));

    // ---------------------------------------------------------------- 主题画刷

    /// <summary>一次取出本次绘制需要的全部画刷，避免在循环里反复分配。</summary>
    private sealed class Palette
    {
        public required Brush Fill;
        public required Brush FillSelected;
        public required Pen Stroke;
        public required Pen StrokeSelected;
        public required Brush PrimaryBar;
        public required Brush BarIdle;
        public required Brush Text;
        public required Brush SubText;
        public required Pen Guide;
        public required Brush EmptyHint;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color ResourceColor(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b.Color;
        return fallback;
    }

    private static Palette BuildPalette(bool dark)
    {
        // 前景色取自主题，保证明暗两套都能自适应
        var fg = ResourceColor("TextFillColorPrimaryBrush", dark ? Colors.White : Colors.Black);
        var accent = ResourceColor("SystemAccentColor", Color.FromRgb(0x4C, 0x8B, 0xF5));

        // 未选中：低对比的填充 + 细描边，让画布安静下来
        var fill = new SolidColorBrush(dark ? WithAlpha(fg, 0x1A) : Color.FromArgb(0x0A, 0, 0, 0));
        var stroke = new Pen(new SolidColorBrush(dark ? WithAlpha(fg, 0x45) : Color.FromArgb(0x30, 0, 0, 0)), 1);

        // 选中：强调色描边 + 强调色淡填充，语义明确
        var fillSelected = new SolidColorBrush(WithAlpha(accent, dark ? (byte)0x2E : (byte)0x1F));
        var strokeSelected = new Pen(new SolidColorBrush(accent), 2);

        // 主显示器：底部实心条用强调色，其它用弱化条
        var primaryBar = new SolidColorBrush(accent);
        var barIdle = new SolidColorBrush(dark ? WithAlpha(fg, 0x2E) : Color.FromArgb(0x24, 0, 0, 0));

        var text = new SolidColorBrush(dark ? WithAlpha(fg, 0xE5) : Color.FromArgb(0xD9, 0, 0, 0));
        var subText = new SolidColorBrush(dark ? WithAlpha(fg, 0x8C) : Color.FromArgb(0x73, 0, 0, 0));
        var guide = new Pen(new SolidColorBrush(WithAlpha(accent, 0xAA)), 1);
        var empty = new SolidColorBrush(dark ? WithAlpha(fg, 0x59) : Color.FromArgb(0x59, 0, 0, 0));

        foreach (var b in new[] { fill, fillSelected, primaryBar, barIdle, text, subText, empty })
            b.Freeze();
        stroke.Freeze();
        strokeSelected.Freeze();
        guide.Freeze();

        return new Palette
        {
            Fill = fill,
            FillSelected = fillSelected,
            Stroke = stroke,
            StrokeSelected = strokeSelected,
            PrimaryBar = primaryBar,
            BarIdle = barIdle,
            Text = text,
            SubText = subText,
            Guide = guide,
            EmptyHint = empty,
        };
    }

    // ---------------------------------------------------------------- 绘制

    protected override void OnRender(DrawingContext dc)
    {
        ComputeTransform();
        var items = Items;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool dark = ThemeService.IsDark;
        var p = BuildPalette(dark);

        var face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        if (items is null || items.Count == 0)
        {
            var hint = new FormattedText("未检测到显示器", System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, face, 13, p.EmptyHint, dpi);
            dc.DrawText(hint, new Point(Math.Max(0, (ActualWidth - hint.Width) / 2), Math.Max(0, (ActualHeight - hint.Height) / 2)));
            return;
        }

        // 吸附辅助线
        if (_guides.Count > 0)
        {
            foreach (var (pos, vertical) in _guides)
            {
                if (vertical)
                {
                    double x = pos * _scale + _ox;
                    dc.DrawLine(p.Guide, new Point(x, 0), new Point(x, ActualHeight));
                }
                else
                {
                    double y = pos * _scale + _oy;
                    dc.DrawLine(p.Guide, new Point(0, y), new Point(ActualWidth, y));
                }
            }
        }

        foreach (var item in items)
        {
            var rect = RectOf(item);
            dc.DrawRoundedRectangle(item.IsSelected ? p.FillSelected : p.Fill,
                item.IsSelected ? p.StrokeSelected : p.Stroke, rect, 4, 4);

            // 主显示器底栏（模拟任务栏）
            var barRect = new Rect(rect.X + 3, rect.Bottom - 6, Math.Max(0, rect.Width - 6), 4);
            dc.DrawRoundedRectangle(item.IsPrimary ? p.PrimaryBar : p.BarIdle, null, barRect, 1.5, 1.5);

            if (rect.Width > 24 && rect.Height > 22)
            {
                var num = new FormattedText(item.Number > 0 ? item.Number.ToString() : "?",
                    System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 13, p.Text, dpi);
                dc.DrawText(num, new Point(rect.X + 8, rect.Y + 6));

                if (rect.Width > 70 && rect.Height > 46 && !string.IsNullOrWhiteSpace(item.Name))
                {
                    var name = new FormattedText(Truncate(item.Name, rect),
                        System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 10, p.SubText, dpi);
                    dc.DrawText(name, new Point(rect.X + 8, rect.Y + 26));
                }
            }
        }
    }

    private static string Truncate(string s, Rect rect)
    {
        int max = (int)Math.Floor((rect.Width - 14) / 5.4);
        if (max >= s.Length) return s;
        if (max <= 1) return "…";
        return s[..Math.Min(s.Length, max - 1)] + "…";
    }

    // ---------------------------------------------------------------- 交互

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var items = Items;
        if (items is null || items.Count == 0) return;
        ComputeTransform();
        var pos = e.GetPosition(this);

        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (!RectOf(items[i]).Contains(pos)) continue;
            _dragItem = items[i];
            _dragStartPoint = pos;
            _dragItemStartX = _dragItem.X;
            _dragItemStartY = _dragItem.Y;
            _guides.Clear();
            foreach (var it in items) it.IsSelected = ReferenceEquals(it, _dragItem);
            SelectionChanged?.Invoke(_dragItem);
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragItem is null) return;
        var items = Items!;
        var pos = e.GetPosition(this);
        double dx = (pos.X - _dragStartPoint.X) / _scale;
        double dy = (pos.Y - _dragStartPoint.Y) / _scale;
        double nx = _dragItemStartX + dx;
        double ny = _dragItemStartY + dy;

        double tol = SnapTolDip / _scale;
        _guides.Clear();

        // 与其它显示器边缘/中心吸附：逐候选对求位移，取最小
        double? shiftX = null, shiftY = null;
        foreach (var other in items)
        {
            if (ReferenceEquals(other, _dragItem)) continue;
            double ol = other.X, or = other.X + other.W, oc = other.X + other.W / 2;
            double ot = other.Y, ob = other.Y + other.H, om = other.Y + other.H / 2;

            double[] myXs = { nx, nx + _dragItem.W, nx + _dragItem.W / 2 };
            double[] tgtXs = { ol, or, oc };
            foreach (var mx in myXs)
                foreach (var tx in tgtXs)
                {
                    double gap = Math.Abs(mx - tx);
                    if (gap <= tol && (shiftX is null || gap < Math.Abs(shiftX.Value)))
                        shiftX = tx - mx;
                }

            double[] myYs = { ny, ny + _dragItem.H, ny + _dragItem.H / 2 };
            double[] tgtYs = { ot, ob, om };
            foreach (var my in myYs)
                foreach (var ty in tgtYs)
                {
                    double gap = Math.Abs(my - ty);
                    if (gap <= tol && (shiftY is null || gap < Math.Abs(shiftY.Value)))
                        shiftY = ty - my;
                }
        }

        if (shiftX.HasValue) nx += shiftX.Value;
        if (shiftY.HasValue) ny += shiftY.Value;

        // 记录辅助线（对齐边而非中心）
        if (shiftX.HasValue) _guides.Add((nx, true));
        if (shiftY.HasValue) _guides.Add((ny, false));

        // 防重叠：整块矩形不允许与其它显示器相交
        bool Overlaps(double x, double y)
        {
            var r = new Rect(x, y, _dragItem.W, _dragItem.H);
            foreach (var o in items)
            {
                if (ReferenceEquals(o, _dragItem)) continue;
                // 用 Inflate(-1) 容忍刚好贴边的情况
                var or = new Rect(o.X, o.Y, o.W, o.H);
                or.Inflate(-1, -1);
                if (r.IntersectsWith(or)) return true;
            }
            return false;
        }

        if (!Overlaps(nx, ny))
        {
            _dragItem.X = nx;
            _dragItem.Y = ny;
        }
        else if (!Overlaps(nx, _dragItem.Y))
        {
            _dragItem.X = nx;
        }
        else if (!Overlaps(_dragItem.X, ny))
        {
            _dragItem.Y = ny;
        }
        // 两个方向都会重叠时保持原位，避免拖进别的屏幕里

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragItem is null) return;
        FinishDrag();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_dragItem is not null && Mouse.LeftButton != MouseButtonState.Pressed)
            FinishDrag();
        base.OnMouseLeave(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_dragItem is not null) FinishDrag();
        base.OnLostMouseCapture(e);
    }

    private void FinishDrag()
    {
        var item = _dragItem;
        _dragItem = null;
        _guides.Clear();
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        if (item is null) return;

        // 吸附到整数像素，避免把 0.0001 的抖动写回系统
        item.X = Math.Round(item.X);
        item.Y = Math.Round(item.Y);

        if (Math.Abs(item.X - _dragItemStartX) > 0.5 || Math.Abs(item.Y - _dragItemStartY) > 0.5)
            PositionCommitted?.Invoke(item);
    }
}
