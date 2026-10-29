namespace MonitorSwitch.Models;

public sealed class AppSettings
{
    /// <summary>Auto / Light / Dark</summary>
    public string Theme { get; set; } = "Auto";

    /// <summary>界面语言：auto（跟随系统）/ en / zh-CN。</summary>
    public string Language { get; set; } = "auto";

    public bool StartMinimized { get; set; }
    public bool CloseToTray { get; set; } = true;
    /// <summary>应用配置后显示“保留 / 还原”确认，15 秒后自动还原。</summary>
    public bool RevertGuardEnabled { get; set; } = true;

    /// <summary>
    /// 托盘面板上次被拖动到的位置（物理像素）。
    /// 为 null 表示用户还没拖动过，此时按光标位置自动摆放。
    /// 显示器布局变化后该位置可能已无效，使用前会做一次可见性校验。
    /// </summary>
    public double? FlyoutLeft { get; set; }
    public double? FlyoutTop { get; set; }
}
