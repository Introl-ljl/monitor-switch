using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using MonitorSwitch.Controls;
using MonitorSwitch.Models;
using MonitorSwitch.Services;
using MonitorSwitch.ViewModels;

namespace MonitorSwitch.Views;

public partial class ProfilesPage : Page
{
    private bool _busy;
    private bool _subscribed;

    public ProfilesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            ProfileStore.Instance.Changed += OnStoreChanged;
            DisplayService.Instance.DisplaysChanged += OnStoreChanged;
            _subscribed = true;
        }
        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            ProfileStore.Instance.Changed -= OnStoreChanged;
            DisplayService.Instance.DisplaysChanged -= OnStoreChanged;
            _subscribed = false;
        }
    }

    private void OnStoreChanged()
    {
        if (_busy) return;
        // 系统应用配置后会连续触发多次刷新，合并处理
        Dispatcher.InvokeAsync(() =>
        {
            if (_busy) return;
            Rebuild();
        }, DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        try
        {
            // 保留正在编辑的卡片状态（重命名 / 快捷键捕获）
            var editing = Cards.ItemsSource is IEnumerable<ProfileCardVM> old
                ? old.Where(v => v.IsRenaming || v.IsEditingHotkey).ToDictionary(v => v.Profile.Id)
                : new Dictionary<string, ProfileCardVM>();

            List<LiveDisplay> cur;
            try { cur = DisplayService.Instance.GetDisplays(); }
            catch (Exception ex)
            {
                AppPaths.Log("读取显示器信息失败: " + ex);
                cur = new List<LiveDisplay>();
            }

            var vms = ProfileStore.Instance.Profiles
                .Select(p =>
                {
                    var vm = new ProfileCardVM(p, DisplayService.Instance.MatchesCurrent(p, cur));
                    if (editing.TryGetValue(p.Id, out var prev))
                    {
                        vm.EditName = prev.EditName;
                        vm.IsRenaming = prev.IsRenaming;
                        vm.IsEditingHotkey = prev.IsEditingHotkey;
                    }
                    return vm;
                })
                .ToList();

            Cards.ItemsSource = vms;
            var empty = vms.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            Cards.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            AppPaths.Log("模板列表刷新失败: " + ex);
        }
    }

    // ---------------------------------------------------------------- 应用 / 新建

    private async void OnApplyProfile(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not FrameworkElement { Tag: ProfileCardVM vm }) return;
        _busy = true;
        try
        {
            var result = await ApplyGuard.Instance.ApplyAsync(vm.Profile);
            ShowResult(result.Success, result.ErrorMessage, result.Warnings);
        }
        catch (Exception ex)
        {
            AppPaths.Log("应用模板失败: " + ex);
            ShowResult(false, ex.Message, null);
        }
        finally
        {
            _busy = false;
            Rebuild();
        }
    }

    private void OnCreateProfile(object sender, RoutedEventArgs e)
    {
        try
        {
            var profile = DisplayService.Instance.CaptureCurrent();
            if (profile.Monitors.Count == 0)
            {
                ShowResult(false, "未检测到显示器，无法创建模板。", null);
                return;
            }
            profile.Name = NextProfileName();
            profile.ModifiedAt = DateTime.Now;
            ProfileStore.Instance.Add(profile);
            ShowResult(true, null, null, "已保存为“" + profile.Name + "”。");
        }
        catch (Exception ex)
        {
            AppPaths.Log("创建模板失败: " + ex);
            ShowResult(false, ex.Message, null);
        }
    }

    private static string NextProfileName()
    {
        int i = ProfileStore.Instance.Profiles.Count + 1;
        while (ProfileStore.Instance.Profiles.Any(p => p.Name == $"配置 {i}")) i++;
        return $"配置 {i}";
    }

    // ---------------------------------------------------------------- 重命名

    private void OnRenameProfile(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProfileCardVM vm }) return;
        vm.IsEditingHotkey = false;
        vm.IsRenaming = true;
    }

    private void OnRenameBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (box.DataContext is not ProfileCardVM vm || !vm.IsRenaming) return;
        // 模板重新生成后需要再次聚焦，交给布局完成后再执行
        Dispatcher.InvokeAsync(() =>
        {
            if (!box.IsVisible) return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter)
        {
            CommitRename(box);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (box.DataContext is ProfileCardVM vm)
            {
                vm.EditName = vm.Profile.Name;
                vm.IsRenaming = false;
            }
            e.Handled = true;
        }
    }

    private void OnRenameCommit(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) CommitRename(box);
    }

    private void CommitRename(TextBox box)
    {
        if (box.DataContext is not ProfileCardVM vm || !vm.IsRenaming) return;
        vm.IsRenaming = false;

        var name = (box.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            // 空名字视为取消
            vm.EditName = vm.Profile.Name;
            Rebuild();
            return;
        }
        if (name == vm.Profile.Name)
        {
            Rebuild();
            return;
        }

        vm.Profile.Name = name;
        vm.Profile.ModifiedAt = DateTime.Now;
        ProfileStore.Instance.Touch();
    }

    // ---------------------------------------------------------------- 快捷键

    private void OnEditHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProfileCardVM vm }) return;
        vm.IsRenaming = false;
        vm.IsEditingHotkey = true;
    }

    private void OnHotkeyBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not HotkeyBox box) return;
        if (box.DataContext is not ProfileCardVM vm) return;
        box.SetHotkey(vm.Profile.Hotkey);
    }

    private void OnHotkeyCaptured(object sender, string? hotkey)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not ProfileCardVM vm) return;
        vm.IsEditingHotkey = false;

        var normalized = string.IsNullOrWhiteSpace(hotkey) ? null : hotkey.Trim();
        if (normalized != null && !HotkeyService.TryParse(normalized, out _, out _))
        {
            ShowResult(false, $"无法识别的快捷键：{normalized}（支持 Ctrl/Alt/Shift/Win + 数字、字母或 F1-F24）", null);
            Rebuild();
            return;
        }

        // 同一快捷键不能绑定到两份模板
        if (normalized != null)
        {
            var conflict = ProfileStore.Instance.Profiles
                .FirstOrDefault(p => p.Id != vm.Profile.Id &&
                                     string.Equals(p.Hotkey, normalized, StringComparison.OrdinalIgnoreCase));
            if (conflict != null)
            {
                ShowResult(false, $"快捷键 {normalized} 已被“{conflict.Name}”占用。", null);
                Rebuild();
                return;
            }
        }

        if (string.Equals(vm.Profile.Hotkey, normalized, StringComparison.Ordinal))
        {
            Rebuild();
            return;
        }

        vm.Profile.Hotkey = normalized;
        vm.Profile.ModifiedAt = DateTime.Now;
        ProfileStore.Instance.Touch();
        ShowResult(true, null, null, normalized == null
            ? $"已清除“{vm.Profile.Name}”的快捷键。"
            : $"已绑定 {normalized}（注册失败时会写入日志）。");
    }

    // ---------------------------------------------------------------- 删除

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProfileCardVM vm }) return;
        var answer = System.Windows.MessageBox.Show(
            $"确定删除模板“{vm.Profile.Name}”吗？此操作不可撤销。",
            "删除模板",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        ProfileStore.Instance.Remove(vm.Profile.Id);
        ShowResult(true, null, null, $"已删除“{vm.Profile.Name}”。");
        Rebuild();
    }

    // ---------------------------------------------------------------- 导入导出

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (ProfileStore.Instance.Profiles.Count == 0)
        {
            ShowResult(false, "还没有可导出的模板。", null);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "导出配置模板",
            Filter = "JSON 文件|*.json",
            FileName = "monitorswitch-templates.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json = JsonSerializer.Serialize(ProfileStore.Instance.Profiles, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            File.WriteAllText(dlg.FileName, json);
            ShowResult(true, null, null, $"已导出 {ProfileStore.Instance.Profiles.Count} 份模板。");
        }
        catch (Exception ex)
        {
            ShowResult(false, "导出失败：" + ex.Message, null);
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "导入配置模板",
            Filter = "JSON 文件|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<DisplayProfile>>(File.ReadAllText(dlg.FileName));
            if (list == null || list.Count == 0)
            {
                ShowResult(false, "文件中没有可导入的模板。", null);
                return;
            }

            var usedHotkeys = new HashSet<string>(
                ProfileStore.Instance.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.Hotkey)).Select(p => p.Hotkey!),
                StringComparer.OrdinalIgnoreCase);

            int added = 0, skipped = 0, hotkeysDropped = 0;
            foreach (var p in list)
            {
                if (p == null) continue;
                // 导入的模板必须至少有一台显示器，否则无法应用
                if (p.Monitors == null || p.Monitors.Count == 0) { skipped++; continue; }

                p.Id = Guid.NewGuid().ToString("N"); // 避免与现有 Id 冲突
                // 快捷键可能与本机已有绑定冲突，导入时不保留
                if (!string.IsNullOrWhiteSpace(p.Hotkey))
                {
                    if (usedHotkeys.Contains(p.Hotkey)) { p.Hotkey = null; hotkeysDropped++; }
                    else usedHotkeys.Add(p.Hotkey);
                }
                // 名字重复会让用户分不清，自动加后缀
                p.Name = UniqueName(string.IsNullOrWhiteSpace(p.Name) ? NextProfileName() : p.Name.Trim());
                p.ModifiedAt = DateTime.Now;
                ProfileStore.Instance.Profiles.Add(p);
                added++;
            }

            if (added == 0)
            {
                ShowResult(false, "文件中的模板均不可用（缺少显示器数据）。", null);
                return;
            }

            ProfileStore.Instance.Touch();

            var msg = $"已导入 {added} 份模板。";
            if (skipped > 0) msg += $"跳过 {skipped} 份无效数据。";
            if (hotkeysDropped > 0) msg += $"{hotkeysDropped} 份模板的快捷键与本机冲突，已清除，可手动重新绑定。";
            ShowResult(true, null, null, msg);
        }
        catch (Exception ex)
        {
            ShowResult(false, "导入失败：" + ex.Message, null);
        }
    }

    /// <summary>确保模板名不与现有模板重复：重名时追加 (2)、(3)…</summary>
    private static string UniqueName(string name)
    {
        var existing = ProfileStore.Instance.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(name)) return name;
        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{name} ({i})";
            if (!existing.Contains(candidate)) return candidate;
        }
        return $"{name} ({Guid.NewGuid().ToString("N")[..4]})";
    }

    private void ShowResult(bool success, string? error, List<string>? warnings, string? successMessage = null)
    {
        ResultBar.IsOpen = true;
        if (!success)
        {
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            ResultBar.Title = "操作失败";
            ResultBar.Message = error ?? "未知错误";
            return;
        }
        if (warnings is { Count: > 0 })
        {
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
            ResultBar.Title = "已应用（部分设置未生效）";
            ResultBar.Message = string.Join(" ", warnings);
        }
        else
        {
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Success;
            ResultBar.Title = "完成";
            ResultBar.Message = successMessage ?? string.Empty;
        }
    }
}
