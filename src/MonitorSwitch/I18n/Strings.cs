using System.Globalization;
namespace MonitorSwitch.I18n;

/// <summary>
/// 界面文案的便捷取用入口。
///
/// 用法：
///   Strings.T("Common.Apply")                  // 取词
///   Strings.F("Msg.ProfileSaved", name)        // 取词并格式化 {0}
///
/// 所有面向用户的文案都必须经过这里，日志（AppPaths.Log）保持中文即可 ——
/// 日志是给开发者看的，不参与本地化。
/// </summary>
public static class Strings
{
    public static string T(string key) => Localization.Instance.Get(key);

    public static string F(string key, params object?[] args)
    {
        var template = Localization.Instance.Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // 占位符与参数不匹配时退回原文，避免因翻译错误让程序崩溃
            return template;
        }
    }
}
