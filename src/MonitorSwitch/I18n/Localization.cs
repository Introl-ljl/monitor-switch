using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace MonitorSwitch.I18n;

/// <summary>
/// 界面文案的运行时查表。
///
/// 设计要点：
/// 1) 文案放在两个嵌入的 JSON 资源里（strings.en.json / strings.zh.json），
///    方便译者直接编辑，不必碰 C# 代码。
/// 2) 通过索引器 <c>Localization.Instance["Key"]</c> 取词，XAML 里用
///    <c>{Binding [Key], Source={x:Static i18n:Localization.Instance}}</c> 绑定。
/// 3) 切换语言时对 <c>Item[]</c> 发一次 PropertyChanged，
///    WPF 会把所有索引器绑定全部刷新 —— 无需重启窗口。
/// 4) 缺词时回退到英文，再回退到 key 本身，保证界面不会出现空白或异常。
/// </summary>
public sealed class Localization : System.ComponentModel.INotifyPropertyChanged
{
    public static Localization Instance { get; } = new();

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public const string Auto = "auto";
    public const string English = "en";
    public const string Chinese = "zh-CN";

    private Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

    /// <summary>当前生效的语言代码（"en" 或 "zh-CN"）。</summary>
    public string Language { get; private set; } = English;

    private Localization() { }

    /// <summary>XAML 绑定入口。</summary>
    public string this[string key] => Get(key);

    public string Get(string key)
    {
        if (_current.TryGetValue(key, out var v)) return v;
        if (_fallback.TryGetValue(key, out var f)) return f;
        return key;
    }

    /// <summary>
    /// 应用语言设置。传 <see cref="Auto"/> 时跟随系统 UI 语言。
    /// 返回是否真的发生了语言变化（供调用方决定要不要重建界面）。
    /// </summary>
    public bool Apply(string? setting)
    {
        var resolved = Resolve(setting);
        bool changed = !string.Equals(Language, resolved, StringComparison.OrdinalIgnoreCase);

        Language = resolved;
        _current = Load(resolved);
        _fallback = Load(English);

        if (changed)
        {
            // 通知所有索引器绑定刷新
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("Language"));
        }
        return changed;
    }

    /// <summary>把设置值解析成实际语言代码。</summary>
    public static string Resolve(string? setting)
    {
        if (string.Equals(setting, English, StringComparison.OrdinalIgnoreCase)) return English;
        if (string.Equals(setting, Chinese, StringComparison.OrdinalIgnoreCase)) return Chinese;

        // Auto：按系统 UI 语言判断是否中文
        try
        {
            var ui = System.Globalization.CultureInfo.CurrentUICulture;
            if (ui.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
                return Chinese;
        }
        catch { /* 取不到就退回英文 */ }
        return English;
    }

    private static Dictionary<string, string> Load(string lang)
    {
        var name = lang == Chinese ? "strings.zh-CN.json" : "strings.en.json";

        // 文案以 WPF 资源（Resource）形式打包在程序集内，按 pack URI 读取。
        // 不走 EmbeddedResource 是因为 UseWPF 项目的资源管线会把它并入 .g.resources，
        // 单独的 JSON 会被静默丢弃。
        try
        {
            var uri = new Uri($"pack://application:,,,/MonitorSwitch;component/I18n/{name}", UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info is not null)
            {
                using var reader = new StreamReader(info.Stream);
                var json = reader.ReadToEnd();
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict is not null)
                    return new Dictionary<string, string>(dict, StringComparer.Ordinal);
            }
        }
        catch
        {
            // 资源不可用时退回空表，Get() 会回退到 key，界面不至于崩溃
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }
}
