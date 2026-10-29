using System.Windows;
using System.Windows.Media;
using MonitorSwitch.Models;
using MonitorSwitch.Services;

namespace MonitorSwitch.Controls;

/// <summary>预览图中的单个显示器矩形（桌面像素坐标）。</summary>
public sealed class PreviewRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public int Number { get; set; }
    public bool IsPrimary { get; set; }

    public static List<PreviewRect> FromMonitors(IEnumerable<MonitorSnapshot> monitors) =>
        monitors.Where(m => m.Active).Select(m => new PreviewRect
        {
            X = m.PositionX,
            Y = m.PositionY,
            // 使用桌面朝向的宽高（竖屏时与面板朝向不同）
            W = (int)m.DesktopWidth,
            H = (int)m.DesktopHeight,
            Number = ParseNumber(m.SourceGdiDeviceName),
            IsPrimary = m.PositionX == 0 && m.PositionY == 0,
        }).ToList();

    private static int ParseNumber(string gdiName)
    {
        if (string.IsNullOrEmpty(gdiName)) return 0;
        var m = System.Text.RegularExpressions.Regex.Match(gdiName, @"DISPLAY(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }
}

/// <summary>
/// 静态迷你显示器排列预览（模板卡片 / 托盘面板用）。
/// 配色随主题实时计算，主显示器条使用强调色（强调色已由 ThemeService 统一为中性蓝）。
/// </summary>
public sealed class MonitorLayoutPreview : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IList<PreviewRect>), typeof(MonitorLayoutPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IList<PreviewRect>? Items
    {
        get => (IList<PreviewRect>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    private const double Pad = 4;

    public MonitorLayoutPreview()
    {
        Loaded += (_, _) => ThemeService.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    private void OnThemeChanged() => InvalidateVisual();

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static Color ResourceColor(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush b) return b.Color;
        return fallback;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var items = Items;
        if (items is null || items.Count == 0) return;

        double availW = Math.Max(1, ActualWidth - Pad * 2), availH = Math.Max(1, ActualHeight - Pad * 2);
        if (availW < 4 || availH < 4) return;

        int minX = items.Min(r => r.X), minY = items.Min(r => r.Y);
        int maxX = items.Max(r => r.X + Math.Max(1, r.W)), maxY = items.Max(r => r.Y + Math.Max(1, r.H));
        double bw = Math.Max(1, maxX - minX), bh = Math.Max(1, maxY - minY);
        double scale = Math.Min(availW / bw, availH / bh);
        double ox = (ActualWidth - bw * scale) / 2 - minX * scale;
        double oy = (ActualHeight - bh * scale) / 2 - minY * scale;

        bool dark = ThemeService.IsDark;
        var fg = ResourceColor("TextFillColorPrimaryBrush", dark ? Colors.White : Colors.Black);
        var accent = ResourceColor("SystemAccentColor", Color.FromRgb(0x4C, 0x8B, 0xF5));

        var fill = new SolidColorBrush(dark ? WithAlpha(fg, 0x24) : Color.FromArgb(0x0F, 0, 0, 0));
        var stroke = new Pen(new SolidColorBrush(dark ? WithAlpha(fg, 0x4D) : Color.FromArgb(0x33, 0, 0, 0)), 1);
        var primaryFill = new SolidColorBrush(accent);
        var numBrush = new SolidColorBrush(dark ? WithAlpha(fg, 0xD9) : Color.FromArgb(0xC0, 0, 0, 0));
        fill.Freeze();
        stroke.Freeze();
        primaryFill.Freeze();
        numBrush.Freeze();

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var r in items)
        {
            var rect = new Rect(r.X * scale + ox, r.Y * scale + oy,
                Math.Max(2, Math.Max(1, r.W) * scale), Math.Max(2, Math.Max(1, r.H) * scale));
            dc.DrawRoundedRectangle(fill, stroke, rect, 2, 2);

            if (r.IsPrimary)
                dc.DrawRectangle(primaryFill, null, new Rect(rect.X + 2, rect.Bottom - 3.5, Math.Max(0, rect.Width - 4), 2.5));

            if (rect.Width > 12 && rect.Height > 11 && r.Number > 0)
            {
                var ft = new FormattedText(
                    r.Number.ToString(),
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
                        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    9,
                    numBrush,
                    dpi);
                if (ft.Width < rect.Width - 6 && ft.Height < rect.Height - 4)
                    dc.DrawText(ft, new Point(rect.X + 3, rect.Y + 2));
            }
        }
        dc.Pop();
    }
}
