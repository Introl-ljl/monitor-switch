using System.IO;
using MonitorSwitch.I18n;
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
                        // 注意顺序：IsRenaming 的 setter 会用 Profile.Name 重置 EditName，
                        // 必须先置标志再回填文本，否则重建列表时输入到一半的改名会丢失
                        if (prev.IsRenaming) vm.IsRenaming = true;
                        vm.EditName = prev.EditName;
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
                ShowResult(false, Strings.T("Msg.NoDisplayForProfile"), null);
                return;
            }
            profile.Name = NextProfileName();
            profile.ModifiedAt = DateTime.Now;
            ProfileStore.Instance.Add(profile);
            ShowResult(true, null, null, Strings.F("Msg.ProfileSaved", profile.Name));
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
        while (ProfileStore.Instance.Profiles.Any(p => p.Name == Strings.F("Profiles.DefaultName", i))) i++;
        return Strings.F("Profiles.DefaultName", i);
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
            ShowResult(false, Strings.F("Msg.HotkeyInvalid", normalized), null);
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
                ShowResult(false, Strings.F("Msg.HotkeyConflict", normalized, conflict.Name), null);
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
            ? Strings.F("Msg.HotkeyCleared", vm.Profile.Name)
            : Strings.F("Msg.HotkeyBound", normalized));
    }

    // ---------------------------------------------------------------- 删除

    private void OnDeleteProfile(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProfileCardVM vm }) return;
        var answer = System.Windows.MessageBox.Show(
            Strings.F("Msg.DeleteConfirm", vm.Profile.Name),
            Strings.T("Msg.DeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        ProfileStore.Instance.Remove(vm.Profile.Id);
        ShowResult(true, null, null, Strings.F("Msg.ProfileDeleted", vm.Profile.Name));
        Rebuild();
    }

    // ---------------------------------------------------------------- 导入导出

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (ProfileStore.Instance.Profiles.Count == 0)
        {
            ShowResult(false, Strings.T("Msg.NothingToExport"), null);
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = Strings.T("Msg.ExportTitle"),
            Filter = Strings.T("Msg.JsonFilter"),
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
            ShowResult(true, null, null, Strings.F("Msg.ExportDone", ProfileStore.Instance.Profiles.Count));
        }
        catch (Exception ex)
        {
            ShowResult(false, Strings.F("Msg.ExportFailed", ex.Message), null);
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Strings.T("Msg.ImportTitle"),
            Filter = Strings.T("Msg.JsonFilter"),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // 与 profiles.json 的加载同一套选项：容忍手编文件的注释/尾逗号与大小写差异
            var list = JsonSerializer.Deserialize<List<DisplayProfile>>(File.ReadAllText(dlg.FileName), ProfileStore.JsonOptions);
            if (list == null || list.Count == 0)
            {
                ShowResult(false, Strings.T("Msg.NothingToImport"), null);
                return;
            }

            var usedHotkeys = new HashSet<string>(
                ProfileStore.Instance.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.Hotkey)).Select(p => p.Hotkey!),
                StringComparer.OrdinalIgnoreCase);

            int added = 0, skipped = 0, hotkeysDropped = 0;
            foreach (var p in list)
            {
                if (p == null) continue;
                // 导入的模板必须至少有一台显示器，否则无法应用；null 元素一并清掉
                if (p.Monitors is null) { skipped++; continue; }
                p.Monitors.RemoveAll(m => m is null);
                if (p.Monitors.Count == 0) { skipped++; continue; }

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
                ShowResult(false, Strings.T("Msg.ImportAllInvalid"), null);
                return;
            }

            ProfileStore.Instance.Touch();

            var msg = Strings.F("Msg.ImportDone", added);
            if (skipped > 0) msg += Strings.F("Msg.ImportSkipped", skipped);
            if (hotkeysDropped > 0) msg += Strings.F("Msg.ImportHotkeysDropped", hotkeysDropped);
            ShowResult(true, null, null, msg);
        }
        catch (Exception ex)
        {
            ShowResult(false, Strings.F("Msg.ImportFailed", ex.Message), null);
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
            ResultBar.Title = Strings.T("Result.Failed");
            ResultBar.Message = error ?? Strings.T("Common.UnknownError");
            return;
        }
        if (warnings is { Count: > 0 })
        {
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
            ResultBar.Title = Strings.T("Result.AppliedPartial");
            ResultBar.Message = string.Join(" ", warnings);
        }
        else
        {
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Success;
            ResultBar.Title = Strings.T("Result.Done");
            ResultBar.Message = successMessage ?? string.Empty;
        }
    }
}
