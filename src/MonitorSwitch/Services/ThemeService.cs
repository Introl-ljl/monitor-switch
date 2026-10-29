using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace MonitorSwitch.Services;

/// <summary>主题应用（跟随系统 / 浅色 / 深色）。</summary>
public static class ThemeService
{
    private static string _currentSetting = "";
    private static bool _uiResourcesLoaded;

    public static event Action? ThemeChanged;

    /// <summary>当前应用是否处于深色主题（自定义绘制控件使用）。</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>
    /// 按需加载 WPF-UI 的资源字典。
    ///
    /// 这两个字典会实例化整套控件模板（Button/ComboBox/NavigationView…），
    /// 实测把进程常驻内存从约 37MB 抬到约 62MB。托盘常驻时一个控件都不用，
    /// 所以延迟到真正要显示窗口（主窗口 / 托盘面板 / 还原确认条）时才加载。
    /// 重复调用是安全的。
    /// </summary>
    public static void EnsureUiResources()
    {
        if (_uiResourcesLoaded) return;
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var dicts = app.Resources.MergedDictionaries;
        dicts.Add(new Wpf.Ui.Markup.ThemesDictionary
        {
            Theme = IsDark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
        });
        dicts.Add(new Wpf.Ui.Markup.ControlsDictionary());
        _uiResourcesLoaded = true;

        // WPF-UI 的 FontIcon 把 FontFamily 的默认值写死成 "Microsoft YaHei UI"，
        // 而该字体不含任何图标字形，所有图标都会渲染成方框。
        //
        // 这里必须用隐式 Style 覆盖，且要放在 ControlsDictionary 之后注册：
        //  - OverrideMetadata 不可用（WPF-UI 内部已注册过该属性的元数据，会抛
        //    "PropertyMetadata 已注册类型 FontIcon"）；
        //  - 隐式 Style 通过 BasedOn 继承 WPF-UI 自己的 FontIcon 样式，
        //    只覆盖 FontFamily，不会丢掉模板与其它视觉状态。
        ApplyFontIconStyle();

        // 资源到位后才真正把主题/强调色/表面色写进字典
        ApplyCore();
    }

    /// <summary>让 WPF-UI 的 FontIcon 使用真正含图标字形的字体。</summary>
    private static void ApplyFontIconStyle()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var iconType = typeof(Wpf.Ui.Controls.FontIcon);
        if (app.Resources.Contains(iconType)) return;

        // 继承 WPF-UI 已有的 FontIcon 隐式样式（若有），只改字体
        Style? baseStyle = null;
        try { baseStyle = app.TryFindResource(iconType) as Style; } catch { }

        var style = baseStyle is not null ? new Style(iconType, baseStyle) : new Style(iconType);
        style.Setters.Add(new Setter(
            Wpf.Ui.Controls.FontIcon.FontFamilyProperty,
            new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets")));
        app.Resources[iconType] = style;
    }

    /// <summary>
    /// 记录主题设置。
    ///
    /// 注意：这个方法在启动阶段就会被调用（托盘常驻也走这里），因此**不能**触碰
    /// 任何 Wpf.Ui 类型 —— 一旦 JIT 到 <c>ApplicationTheme</c>，Wpf.Ui.dll（5.7MB）
    /// 就会被加载进常驻内存，之前为降低占用做的延迟加载就白费了。
    /// 所以这里只做纯托管计算，真正的应用推迟到 <see cref="EnsureUiResources"/>。
    /// </summary>
    public static void Apply(string setting)
    {
        setting = string.IsNullOrWhiteSpace(setting) ? "Auto" : setting;

        bool wasDark = IsDark;
        IsDark = setting switch
        {
            "Light" => false,
            "Dark" => true,
            _ => !IsSystemLight(),
        };

        bool sameSetting = string.Equals(_currentSetting, setting, StringComparison.Ordinal);
        _currentSetting = setting;

        // 相同设置且深浅没有变化时不重复应用，避免无谓的重绘与闪烁
        if (sameSetting && wasDark == IsDark) return;

        // 托盘常驻阶段 UI 资源还没加载：只记下主题，等窗口真正出现时再一次性应用。
        // 否则会在这里提前把整套控件模板拉进内存，常驻占用就降不下来。
        if (!_uiResourcesLoaded)
        {
            ThemeChanged?.Invoke();
            return;
        }

        ApplyCore();
        ThemeChanged?.Invoke();
    }

    /// <summary>系统主题变化时调用：仅在“跟随系统”时重新应用。</summary>
    public static void OnSystemThemeChanged()
    {
        if (!string.Equals(_currentSetting, "Auto", StringComparison.Ordinal)) return;
        bool wasDark = IsDark;
        IsDark = !IsSystemLight();
        if (wasDark == IsDark) return;
        if (!_uiResourcesLoaded)
        {
            ThemeChanged?.Invoke();
            return;
        }
        ApplyCore();
        ThemeChanged?.Invoke();
    }

    /// <summary>把当前主题、强调色、表面色写入资源字典。仅在 UI 资源已加载时调用。</summary>
    private static void ApplyCore()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        // updateAccent: false —— 不让 WPF-UI 把系统强调色灌进来，稍后由我们覆盖。
        // 背板保持 Mica（与原先 ApplicationThemeManager.Apply(theme) 的默认值一致）。
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(
            IsDark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light,
            Wpf.Ui.Controls.WindowBackdropType.Mica,
            false);

        ApplyAccent();
        ApplySurface();
    }

    /// <summary>
    /// 按主题设置卡片表面色。
    ///
    /// WPF-UI 的 <c>CardBackgroundFillColorDefaultBrush</c> 是 #0DFFFFFF（5% 白），
    /// 叠在深色页面底 #272727 上几乎看不出差别 —— 实测卡片与页面背景都是 RGB(39,39,39)，
    /// 对比度 1.00:1，卡片完全没有“浮起来”，只剩 1px 描边勉强分隔。
    /// 这里按主题给卡片一个明确的表面色，让层级关系一眼可见。
    /// </summary>
    private static void ApplySurface()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        // 深色：比页面底 #272727 亮一档；浅色：纯白 + 更明显的描边
        var card = IsDark ? Color.FromRgb(0x30, 0x30, 0x30) : Colors.White;
        var stroke = IsDark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00);

        app.Resources["AppCardBackgroundBrush"] = new SolidColorBrush(card);
        app.Resources["AppCardStrokeBrush"] = new SolidColorBrush(stroke);

        // 强调色描边：用于“有未应用的更改”这类需要吸引注意但不刺眼的提示条
        var accent = IsDark ? AccentPrimaryDark : AccentPrimaryLight;
        app.Resources["AppAccentBorderBrush"] = new SolidColorBrush(
            IsDark ? Color.FromArgb(0x66, accent.R, accent.G, accent.B)
                   : Color.FromArgb(0x55, accent.R, accent.G, accent.B));
    }

    /// <summary>
    /// 覆盖 WPF-UI 的强调色资源。
    ///
    /// 注意 1：<c>ApplicationAccentColorManager.Apply(Color, ApplicationTheme, bool)</c>
    /// 实测并不会更新 <c>AccentButtonBackground</c>（它始终停留在系统强调色派生出的
    /// #9DA2A4 青灰），所以主按钮一直是脏灰绿色。这里在调用 API 之后，
    /// 再逐个直接写入资源字典，确保所有强调色画刷都落到中性蓝。
    ///
    /// 注意 2：主题字典里 <c>SystemAccentColor</c> 一类键是 <see cref="Color"/> 类型，
    /// 而 <c>AccentFillColorDefaultBrush</c> 一类是 <see cref="SolidColorBrush"/>。
    /// 写错类型会让模板里 <c>SolidColorBrush.Color="{DynamicResource ...}"</c>
    /// 在解析页面时抛 XamlParseException，因此这里按资源原有类型写入。
    /// </summary>
    private static void ApplyAccent()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;

        var primary = IsDark ? AccentPrimaryDark : AccentPrimaryLight;
        var secondary = IsDark ? AccentSecondaryDark : AccentSecondaryLight;
        var tertiary = IsDark ? AccentTertiaryDark : AccentTertiaryLight;

        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(AccentBase, primary, secondary, tertiary);

        // 强调色背景上的前景文字统一用白色（强调色本身足够深）
        var onAccent = Colors.White;
        var onAccentSecondary = Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);

        var dict = app.Resources;

        // 按资源原有类型写入：Color 键写 Color，Brush 键写 Brush。
        // 注意必须用 TryFindResource 探测 —— 主题资源在 merged dictionaries 里，
        // Application.Resources.Contains 只查顶层字典，会误判为不存在。
        void Set(string key, Color c)
        {
            var existing = app.TryFindResource(key);
            dict[key] = existing is Color ? c : new SolidColorBrush(c);
        }

        // 强调色本体
        Set("SystemAccentColor", AccentBase);
        Set("SystemAccentColorPrimary", primary);
        Set("SystemAccentColorSecondary", secondary);
        Set("SystemAccentColorTertiary", tertiary);
        Set("SystemAccentColorBrush", AccentBase);
        Set("SystemAccentColorPrimaryBrush", primary);
        Set("SystemAccentColorSecondaryBrush", secondary);
        Set("SystemAccentColorTertiaryBrush", tertiary);
        Set("SystemAccentBrush", primary);

        // 填充
        Set("AccentFillColorDefaultBrush", AccentBase);
        Set("AccentFillColorSecondaryBrush", primary);
        Set("AccentFillColorTertiaryBrush", secondary);
        Set("AccentFillColorSelectedTextBackgroundBrush", AccentBase);

        // 主按钮（AccentButtonBackground 不会随 API 更新，必须显式写）
        Set("AccentButtonBackground", AccentBase);
        Set("AccentButtonBackgroundPointerOver", primary);
        Set("AccentButtonBackgroundPressed", secondary);
        Set("AccentButtonForeground", onAccent);
        Set("AccentButtonForegroundPointerOver", onAccent);
        Set("AccentButtonForegroundPressed", onAccent);

        // 强调色上的文字
        Set("AccentTextFillColorPrimaryBrush", tertiary);
        Set("AccentTextFillColorSecondaryBrush", secondary);
        Set("AccentTextFillColorTertiaryBrush", primary);
        Set("TextOnAccentFillColorPrimaryBrush", onAccent);
        Set("TextOnAccentFillColorSecondaryBrush", onAccentSecondary);
    }

    /// <summary>
    /// 应用强调色。
    ///
    /// 为什么不直接用系统强调色：WPF-UI 由 <c>SystemAccentColor</c> 派生
    /// <c>AccentFillColorSelectedTextBackgroundBrush</c> 等一整套画刷，选中态、
    /// 高亮、主按钮全部跟着它走。本机系统强调色是青灰色 #69797E
    /// （R105 G121 B126，绿通道最高），铺到界面上会让整片 UI 泛青绿。
    /// 这里固定用中性蓝，既没有色偏，也符合 Fluent 的默认观感。
    /// </summary>
    private static readonly Color AccentBase = Color.FromRgb(0x4C, 0x8B, 0xF5);

    /// <summary>深色主题下的强调色阶（基准色偏亮，保证在深底上有足够对比）。</summary>
    private static readonly Color AccentPrimaryDark = Color.FromRgb(0x6E, 0xA4, 0xFF);
    private static readonly Color AccentSecondaryDark = Color.FromRgb(0x8C, 0xB7, 0xFF);
    private static readonly Color AccentTertiaryDark = Color.FromRgb(0xAB, 0xCA, 0xFF);

    /// <summary>浅色主题下的强调色阶（基准色偏深，保证在浅底上可读）。</summary>
    private static readonly Color AccentPrimaryLight = Color.FromRgb(0x2F, 0x6F, 0xD6);
    private static readonly Color AccentSecondaryLight = Color.FromRgb(0x1E, 0x5A, 0xBA);
    private static readonly Color AccentTertiaryLight = Color.FromRgb(0x14, 0x47, 0x96);

    public static bool IsSystemLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }
}
