using System.Windows;
using MonitorSwitch.I18n;
using System.Windows.Controls;
using System.Windows.Threading;
using MonitorSwitch.Controls;
using MonitorSwitch.Models;
using MonitorSwitch.Services;

namespace MonitorSwitch.Views;

public partial class DisplayPage : Page
{
    private List<LiveDisplay> _displays = new();
    private List<DisplayService.InactiveTarget> _inactive = new();
    private LiveDisplay? _selected;
    private bool _loading;
    private bool _busy;
    private bool _subscribed;
    private DispatcherTimer? _debounce;

    /// <summary>待应用的修改草稿。所有控件改动只写这里，点“应用”才下发。</summary>
    private readonly DisplayDraft _draft = new();

    /// <summary>当前刷新率下拉框里的候选值（随分辨率变化）。</summary>
    private List<double> _refreshOptions = new();

    public DisplayPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ArrangeCanvas.SelectionChanged += OnCanvasSelectionChanged;
        ArrangeCanvas.PositionCommitted += OnPositionCommitted;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            DisplayService.Instance.DisplaysChanged += OnDisplaysChanged;
            _subscribed = true;
        }
        RefreshAll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            DisplayService.Instance.DisplaysChanged -= OnDisplaysChanged;
            _subscribed = false;
        }
        _debounce?.Stop();
    }

    private void OnDisplaysChanged()
    {
        if (_busy) return;
        // 系统在应用配置后会连续触发多次 WM_DISPLAYCHANGE，合并为一次刷新
        Dispatcher.InvokeAsync(() =>
        {
            if (_busy) return;
            _debounce ??= CreateDebounce();
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private DispatcherTimer CreateDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_busy) RefreshAll();
        };
        return timer;
    }

    // ---------------------------------------------------------------- 数据加载

    private void RefreshAll()
    {
        try
        {
            _displays = DisplayService.Instance.GetDisplays();
            _inactive = DisplayService.Instance.GetInactiveTargets();
        }
        catch (Exception ex)
        {
            AppPaths.Log("读取显示器信息失败: " + ex);
            ShowResult(false, Strings.F("Result.ReadFailed", ex.Message), null);
            return;
        }

        // 重新读取系统状态后，草稿以系统为准重建（未应用的改动会被丢弃）。
        // 这是刻意的：系统状态变了（例如热插拔、其它程序改了配置），
        // 继续沿用旧草稿会让“应用”下发一份基于过期数据的配置。
        _draft.Reset(_displays);
        UpdatePendingState();

        BuildArrangeItems();
        BuildSelector();
        BuildInactiveList();

        // 保持选中同一块屏幕（按源名匹配，源名在同一会话内稳定）
        LiveDisplay? keep = null;
        if (_selected != null)
            keep = _displays.FirstOrDefault(d => d.SourceGdiName == _selected.SourceGdiName);
        keep ??= _displays.FirstOrDefault(d => d.SourceGdiName == _selected?.SourceGdiName);
        SelectMonitor(keep ?? _displays.FirstOrDefault());
    }

    private void BuildArrangeItems()
    {
        var selectedKey = _selected?.SourceGdiName
                          ?? ArrangeCanvas.Items?.FirstOrDefault(i => i.IsSelected)?.Key;
        var items = _displays.Select(d => new ArrangeItem
        {
            Key = d.SourceGdiName,
            Number = d.Number,
            Name = d.DisplayName,
            X = d.X,
            Y = d.Y,
            W = d.W,
            H = d.H,
            IsPrimary = d.IsPrimary,
            IsSelected = d.SourceGdiName == selectedKey,
        }).ToList();
        ArrangeCanvas.Items = items;
    }

    private void BuildSelector()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            MonitorSelector.ItemsSource = _displays
                .Select(d => $"{d.Number}. {d.DisplayName}（{d.ConnectorName}）")
                .ToList();
            MonitorSelector.SelectedIndex = _selected is null
                ? (_displays.Count > 0 ? 0 : -1)
                : _displays.FindIndex(d => d.SourceGdiName == _selected.SourceGdiName);
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private void BuildInactiveList()
    {
        InactiveList.Children.Clear();
        var show = _inactive.Count > 0;
        InactiveTitle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        InactiveCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        for (int i = 0; i < _inactive.Count; i++)
        {
            var t = _inactive[i];
            var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var stack = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = t.MonitorName, Style = (Style)FindResource("SettingTitle") });
            stack.Children.Add(new TextBlock
            {
                Text = t.PreferredW > 0 ? Strings.F("Display.InactivePreferred", t.PreferredW, t.PreferredH) : Strings.T("Display.InactiveNotEnabled"),
                Style = (Style)FindResource("SettingDesc"),
                Margin = new Thickness(0, 2, 0, 0),
            });

            var btn = new Wpf.Ui.Controls.Button
            {
                Content = Strings.T("Display.InactiveEnable"),
                Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                MinWidth = 80,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = t.TargetDevicePath,
            };
            btn.Click += OnEnableInactive;
            Grid.SetColumn(stack, 0);
            Grid.SetColumn(btn, 1);
            row.Children.Add(stack);
            row.Children.Add(btn);
            InactiveList.Children.Add(row);

            if (i < _inactive.Count - 1)
            {
                InactiveList.Children.Add(new Border
                {
                    Height = 1,
                    Background = (System.Windows.Media.Brush)FindResource("DividerStrokeColorDefaultBrush"),
                });
            }
        }
    }

    // ---------------------------------------------------------------- 选中显示器

    private void OnMonitorSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var idx = MonitorSelector.SelectedIndex;
        SelectMonitor(idx >= 0 && idx < _displays.Count ? _displays[idx] : null);
    }

    private void OnCanvasSelectionChanged(ArrangeItem item)
    {
        var d = _displays.FirstOrDefault(x => x.SourceGdiName == item.Key);
        if (d == null || ReferenceEquals(d, _selected)) return;
        SelectMonitor(d);
    }

    private void SelectMonitor(LiveDisplay? d)
    {
        _selected = d;
        _loading = true;
        try
        {
            // 画布选中态与下拉框保持一致
            if (ArrangeCanvas.Items != null)
            {
                foreach (var item in ArrangeCanvas.Items)
                    item.IsSelected = d != null && item.Key == d.SourceGdiName;
            }

            var idx = d == null ? -1 : _displays.FindIndex(x => x.SourceGdiName == d.SourceGdiName);
            if (MonitorSelector.SelectedIndex != idx) MonitorSelector.SelectedIndex = idx;

            if (d == null)
            {
                SettingsCard.Visibility = Visibility.Collapsed;
                return;
            }

            SettingsCard.Visibility = Visibility.Visible;
            MonitorNameText.Text = d.DisplayName;
            PrimaryBadge.Visibility = d.IsPrimary ? Visibility.Visible : Visibility.Collapsed;

            var e = _draft.Get(d.SourceGdiName);
            if (e == null)
            {
                // 草稿里没有这块屏（理论上不会发生），退回系统值
                e = new DisplayDraft.Entry
                {
                    Width = d.W, Height = d.H, RefreshHz = d.RefreshHz,
                    RefreshNum = d.RefreshNum, RefreshDen = d.RefreshDen,
                    Rotation = d.Rotation, Hdr = d.HdrEnabled, Active = true, X = d.X, Y = d.Y,
                };
            }

            var resItems = d.Resolutions.Select(r => $"{r.W} × {r.H}").ToList();
            ResCombo.ItemsSource = resItems;
            var cur = d.Resolutions.FindIndex(r => r.W == e.Width && r.H == e.Height);
            ResCombo.SelectedIndex = cur;
            ResCombo.IsEnabled = d.Resolutions.Count > 1;

            _refreshOptions = RefreshOptionsFor(d, e.Width, e.Height);
            RefreshCombo.ItemsSource = _refreshOptions.Select(hz => DisplayProfile.FormatHz(hz)).ToList();
            var curHz = _refreshOptions.FindIndex(hz => Math.Abs(hz - e.RefreshHz) < 0.5);
            RefreshCombo.SelectedIndex = curHz;
            RefreshCombo.IsEnabled = _refreshOptions.Count > 1;

            OrientCombo.SelectedIndex = e.Rotation switch
            {
                2 => 1,
                3 => 2,
                4 => 3,
                _ => 0,
            };

            ScaleCombo.ItemsSource = new[] { Strings.F("Display.ScaleValue", $"{d.ScalePercent:0}") };
            ScaleCombo.SelectedIndex = 0;

            HdrToggle.Visibility = d.HdrSupported ? Visibility.Visible : Visibility.Collapsed;
            HdrDesc.Text = d.HdrSupported ? Strings.T("Display.HdrDesc") : Strings.T("Display.HdrUnsupported");
            HdrToggle.IsChecked = e.Hdr;
            HdrToggle.IsEnabled = d.HdrSupported;

            EnableToggle.IsChecked = e.Active;
            EnableToggle.IsEnabled = _displays.Count > 1;

            SetPrimaryBtn.IsEnabled = !(e.X == 0 && e.Y == 0);

            MonitorSubText.Text = BuildSubText(d, e);
        }
        finally
        {
            _loading = false;
        }
    }

    // ---------------------------------------------------------------- 编辑（只改草稿，不生效）

    /// <summary>
    /// 编辑某个显示器的草稿并刷新界面状态。
    ///
    /// 关键点：这里**不再**调用 ApplyGuard 下发到系统。所有改动只写进 <see cref="_draft"/>，
    /// 由用户点“应用”才真正生效 —— 这是避免“动一下下拉框屏幕就闪一次”的核心。
    /// </summary>
    private void EditDraft(string gdiName, Action<DisplayDraft.Entry> mutate)
    {
        _draft.Edit(gdiName, mutate);
        // 先回填控件（例如换分辨率后刷新率列表要跟着变），再刷新提示条，
        // 保证“待应用”状态与界面显示一致。
        SyncControlsFromDraft();
        UpdatePendingState();
    }

    /// <summary>根据草稿与系统状态的差异，显示/隐藏“待应用”提示条。</summary>
    private void UpdatePendingState()
    {
        bool dirty = _draft.IsDirty;
        PendingBar.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;

        if (dirty)
        {
            PendingDesc.Text = _displays.Count > 1
                ? "点击“应用”后生效。应用会短暂切换显示器，15 秒内未确认将自动还原。"
                : "点击“应用”后生效。应用会短暂切换显示器，15 秒内未确认将自动还原。";
        }
    }

    /// <summary>把草稿值回填到各个控件（不触发事件）。</summary>
    private void SyncControlsFromDraft()
    {
        if (_selected == null) return;
        var e = _draft.Get(_selected.SourceGdiName);
        if (e == null) return;

        bool wasLoading = _loading;
        _loading = true;
        try
        {
            // 分辨率：草稿值是桌面朝向，需要和 Resolutions 列表（桌面朝向）比对
            int resIdx = _selected.Resolutions.FindIndex(r => r.W == e.Width && r.H == e.Height);
            if (resIdx >= 0 && ResCombo.SelectedIndex != resIdx) ResCombo.SelectedIndex = resIdx;

            // 刷新率：分辨率变了要换一套候选列表
            var hzList = RefreshOptionsFor(_selected, e.Width, e.Height);
            if (!hzList.SequenceEqual(_refreshOptions))
            {
                _refreshOptions = hzList;
                RefreshCombo.ItemsSource = hzList.Select(hz => DisplayProfile.FormatHz(hz)).ToList();
            }
            int hzIdx = hzList.FindIndex(hz => Math.Abs(hz - e.RefreshHz) < 0.5);
            if (hzIdx >= 0 && RefreshCombo.SelectedIndex != hzIdx) RefreshCombo.SelectedIndex = hzIdx;

            int orientIdx = e.Rotation switch { 2 => 1, 3 => 2, 4 => 3, _ => 0 };
            if (OrientCombo.SelectedIndex != orientIdx) OrientCombo.SelectedIndex = orientIdx;

            if (HdrToggle.IsChecked != e.Hdr) HdrToggle.IsChecked = e.Hdr;
            if (EnableToggle.IsChecked != e.Active) EnableToggle.IsChecked = e.Active;

            SetPrimaryBtn.IsEnabled = !(e.X == 0 && e.Y == 0);

            // 副标题实时反映草稿，让用户看到“将要变成什么”
            MonitorSubText.Text = BuildSubText(_selected, e);
        }
        finally
        {
            _loading = wasLoading;
        }

        // 画布也要跟着草稿的位置走
        SyncCanvasFromDraft();
    }

    private static string BuildSubText(LiveDisplay d, DisplayDraft.Entry e)
    {
        var hz = DisplayProfile.FormatHz(e.RefreshHz);
        return $"{d.AdapterName} · {d.ConnectorName} · {e.Width} × {e.Height} @ {hz} · {d.ScalePercent:0}%";
    }

    /// <summary>把草稿里的位置与启用状态同步到排列画布。</summary>
    private void SyncCanvasFromDraft()
    {
        var items = ArrangeCanvas.Items;
        if (items == null) return;

        _loading = true;
        try
        {
            foreach (var item in items)
            {
                var e = _draft.Get(item.Key);
                if (e == null) continue;
                item.X = e.X;
                item.Y = e.Y;
                item.IsPrimary = e.X == 0 && e.Y == 0;
            }
        }
        finally
        {
            _loading = false;
        }
        ArrangeCanvas.InvalidateVisual();
    }

    /// <summary>某块屏在指定分辨率下可选的刷新率。</summary>
    private static List<double> RefreshOptionsFor(LiveDisplay d, uint w, uint h)
    {
        // 分辨率等于当前分辨率时，直接用系统枚举出的完整列表
        if (w == d.W && h == d.H) return new List<double>(d.RefreshRates);

        // 换了分辨率：系统是按“当前时序”枚举刷新率的，没有可靠的按分辨率查表 API，
        // 这里保留原列表让用户仍能选择（下发时由 SetDisplayConfig 校验）。
        return new List<double>(d.RefreshRates);
    }

    // ---------------------------------------------------------------- 应用 / 撤销

    private async void OnApplyPending(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_draft.IsDirty)
        {
            UpdatePendingState();
            return;
        }

        _busy = true;
        try
        {
            var current = DisplayService.Instance.CaptureCurrent();
            // 先按草稿改快照，再一次下发
            _draft.ApplyTo(current);

            var result = await ApplyGuard.Instance.ApplyAsync(current);
            ShowResult(result.Success, result.ErrorMessage, result.Warnings);

            if (result.Success)
            {
                // 应用成功后草稿与系统一致，清掉脏标记
                _draft.Reset(DisplayService.Instance.GetDisplays());
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("应用草稿异常: " + ex);
            ShowResult(false, ex.Message, null);
        }
        finally
        {
            _busy = false;
            RefreshAll();
        }
    }

    private void OnDiscardPending(object sender, RoutedEventArgs e)
    {
        // 用系统真实状态重建草稿，然后把控件回填到系统值
        _draft.Reset(_displays);
        SyncControlsFromDraft();
        UpdatePendingState();
        ShowResult(true, null, null, Strings.T("Result.DiscardedMsg"), title: Strings.T("Result.Discarded"));
    }

    // ---------------------------------------------------------------- 控件事件（只改草稿）

    private void OnResChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selected == null || ResCombo.SelectedIndex < 0) return;
        if (ResCombo.SelectedIndex >= _selected.Resolutions.Count) return;
        var dim = _selected.Resolutions[ResCombo.SelectedIndex];

        EditDraft(_selected.SourceGdiName, entry =>
        {
            entry.Width = dim.W;
            entry.Height = dim.H;
        });
    }

    private void OnRefreshChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selected == null || RefreshCombo.SelectedIndex < 0) return;
        if (RefreshCombo.SelectedIndex >= _refreshOptions.Count) return;
        var hz = _refreshOptions[RefreshCombo.SelectedIndex];

        EditDraft(_selected.SourceGdiName, entry =>
        {
            entry.RefreshHz = hz;
            bool integral = Math.Abs(hz - Math.Round(hz)) < 0.01;
            entry.RefreshNum = integral ? (uint)Math.Round(hz) : (uint)Math.Round(hz * 1000);
            entry.RefreshDen = integral ? 1u : 1000u;
        });
    }

    private void OnOrientChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selected == null || OrientCombo.SelectedIndex < 0) return;
        uint rot = OrientCombo.SelectedIndex switch { 1 => 2, 2 => 3, 3 => 4, _ => 1 };

        EditDraft(_selected.SourceGdiName, entry =>
        {
            // 旋转改变桌面朝向：草稿里的宽高要跟着交换
            bool wasPortrait = entry.Rotation is 2 or 4;
            bool willPortrait = rot is 2 or 4;
            entry.Rotation = rot;
            if (wasPortrait != willPortrait)
                (entry.Width, entry.Height) = (entry.Height, entry.Width);
        });
    }

    private void OnHdrToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected == null) return;
        bool on = HdrToggle.IsChecked == true;
        EditDraft(_selected.SourceGdiName, entry => entry.Hdr = on);
    }

    private void OnEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected == null) return;
        bool on = EnableToggle.IsChecked == true;

        // 至少保留一台启用的显示器
        if (!on && _displays.Count <= 1)
        {
            ShowResult(false, Strings.T("Result.AtLeastOneDisplay"), null);
            _loading = true;
            EnableToggle.IsChecked = true;
            _loading = false;
            return;
        }

        EditDraft(_selected.SourceGdiName, entry => entry.Active = on);
    }

    private void OnSetPrimary(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected == null) return;
        var entry = _draft.Get(_selected.SourceGdiName);
        if (entry == null) return;

        // 整体平移草稿坐标，使所选显示器位于 (0,0) —— 该位置即主显示器
        _draft.ShiftAll(-entry.X, -entry.Y);
        UpdatePendingState();
        SyncCanvasFromDraft();
    }

    private async void OnEnableInactive(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Wpf.Ui.Controls.Button { Tag: string path }) return;
        _busy = true;
        try
        {
            var current = DisplayService.Instance.CaptureCurrent();
            var act = current.Monitors.Where(m => m.Active).ToList();
            int rightX = act.Count > 0 ? act.Max(m => m.PositionX + (int)m.SourceWidth) : 0;
            int topY = act.Count > 0 ? act.Min(m => m.PositionY) : 0;

            var snap = DisplayService.Instance.BuildSnapshotForTarget(path, rightX, topY);
            if (snap == null)
            {
                ShowResult(false, Strings.T("Msg.CannotReadPreferred"), null);
                return;
            }
            current.Monitors.Add(snap);

            var result = await ApplyGuard.Instance.ApplyAsync(current);
            ShowResult(result.Success, result.ErrorMessage, result.Warnings);
        }
        catch (Exception ex)
        {
            AppPaths.Log("启用显示器失败: " + ex);
            ShowResult(false, ex.Message, null);
        }
        finally
        {
            _busy = false;
            RefreshAll();
        }
    }

    /// <summary>
    /// 画布拖拽结束。只更新草稿坐标，不立即下发 —— 与其它控件一致，
    /// 由用户点“应用”统一生效，避免拖一下就重排整个桌面。
    /// </summary>
    private void OnPositionCommitted(ArrangeItem item)
    {
        if (_busy) return;

        // 拖拽改变的是被拖动那块屏的位置；其余屏位置保持不变
        _draft.Edit(item.Key, entry =>
        {
            entry.X = (int)Math.Round(item.X);
            entry.Y = (int)Math.Round(item.Y);
        });

        UpdatePendingState();

        // 主显示器标记随坐标变化
        if (ArrangeCanvas.Items != null)
        {
            _loading = true;
            try
            {
                foreach (var it in ArrangeCanvas.Items)
                {
                    var e = _draft.Get(it.Key);
                    if (e == null) continue;
                    it.IsPrimary = e.X == 0 && e.Y == 0;
                }
            }
            finally { _loading = false; }
            ArrangeCanvas.InvalidateVisual();
        }

        if (_selected != null && _selected.SourceGdiName == item.Key)
            SetPrimaryBtn.IsEnabled = !(item.X == 0 && item.Y == 0);
    }

    private void OnIdentify(object sender, RoutedEventArgs e) =>
        IdentifyService.Identify(_displays);

    private void OnDetect(object sender, RoutedEventArgs e)
    {
        ShowResult(true, null, null, Strings.T("Result.RefreshedMsg"), title: Strings.T("Result.Refreshed"));
        RefreshAll();
    }

    // ---------------------------------------------------------------- 反馈

    /// <summary>显示反馈条。<paramref name="title"/> 为空时按成功/失败取默认标题。</summary>
    private void ShowResult(bool success, string? error, List<string>? warnings,
        string? successMessage = null, string? title = null)
    {
        ResultBar.IsOpen = true;
        if (!success)
        {
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            ResultBar.Title = title ?? Strings.T("Result.Failed");
            ResultBar.Message = error ?? Strings.T("Common.UnknownError");
            return;
        }
        if (warnings is { Count: > 0 })
        {
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Warning;
            ResultBar.Title = title ?? Strings.T("Result.AppliedPartial");
            ResultBar.Message = string.Join(" ", warnings);
        }
        else
        {
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Success;
            ResultBar.Title = title ?? Strings.T("Result.Applied");
            ResultBar.Message = successMessage ?? Strings.T("Result.AppliedMsg");
        }
    }
}
