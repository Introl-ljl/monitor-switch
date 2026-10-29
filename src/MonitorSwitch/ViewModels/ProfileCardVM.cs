using System.ComponentModel;
using MonitorSwitch.I18n;
using System.Runtime.CompilerServices;
using MonitorSwitch.Controls;
using MonitorSwitch.Models;

namespace MonitorSwitch.ViewModels;

/// <summary>模板页与托盘面板共用的模板卡片视图模型。</summary>
public sealed class ProfileCardVM : INotifyPropertyChanged
{
    public DisplayProfile Profile { get; }
    public IList<PreviewRect> Preview { get; }

    public ProfileCardVM(DisplayProfile profile, bool isCurrent)
    {
        Profile = profile;
        _isCurrent = isCurrent;
        Preview = PreviewRect.FromMonitors(profile.Monitors);
        _editName = profile.Name;
    }

    public string Name => Profile.Name;
    public string Summary => Profile.Summary;
    public string HotkeyText => string.IsNullOrWhiteSpace(Profile.Hotkey) ? Strings.T("Profiles.NoHotkeyBadge") : Strings.F("Profiles.HotkeyBadge", Profile.Hotkey);

    /// <summary>托盘面板里只显示按键组合本身；空字符串表示未设置（用于隐藏标签）。</summary>
    public string ShortHotkey => Profile.Hotkey ?? "";

    /// <summary>重命名编辑框的临时文本（未提交前不写回 Profile）。</summary>
    private string _editName;
    public string EditName
    {
        get => _editName;
        set
        {
            if (_editName == value) return;
            _editName = value;
            Changed();
        }
    }

    private bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set { _isCurrent = value; Changed(); } }

    private bool _isRenaming;
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value) return;
            _isRenaming = value;
            if (value) EditName = Profile.Name;
            Changed();
        }
    }

    private bool _isEditingHotkey;
    public bool IsEditingHotkey { get => _isEditingHotkey; set { _isEditingHotkey = value; Changed(); } }

    /// <summary>模板内容变化后刷新只读展示属性。</summary>
    public void RefreshAll()
    {
        EditName = Profile.Name;
        Changed(nameof(Name));
        Changed(nameof(Summary));
        Changed(nameof(HotkeyText));
        Changed(nameof(ShortHotkey));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

