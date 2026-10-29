using System.Windows;
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
            ShowResult(false, "读取显示器信息失败：" + ex.Message, null);
            return;
        }

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
                Text = t.PreferredW > 0 ? $"首选 {t.PreferredW} × {t.PreferredH}" : "未在桌面中启用",
                Style = (Style)FindResource("SettingDesc"),
                Margin = new Thickness(0, 2, 0, 0),
            });

            var btn = new Wpf.Ui.Controls.Button
            {
                Content = "启用",
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
            MonitorSubText.Text = $"{d.AdapterName} · {d.ConnectorName} · {d.ResolutionText} @ {DisplayProfile.FormatHz(d.RefreshHz)} · 缩放 {d.ScalePercent:0}%";
            PrimaryBadge.Visibility = d.IsPrimary ? Visibility.Visible : Visibility.Collapsed;

            var resItems = d.Resolutions.Select(r => $"{r.W} × {r.H}").ToList();
            ResCombo.ItemsSource = resItems;
            var cur = d.Resolutions.FindIndex(r => r.W == d.W && r.H == d.H);
            ResCombo.SelectedIndex = cur;
            ResCombo.IsEnabled = d.Resolutions.Count > 1;

            var hzItems = d.RefreshRates.Select(hz => DisplayProfile.FormatHz(hz)).ToList();
            RefreshCombo.ItemsSource = hzItems;
            var curHz = d.RefreshRates.FindIndex(hz => Math.Abs(hz - d.RefreshHz) < 0.5);
            RefreshCombo.SelectedIndex = curHz;
            RefreshCombo.IsEnabled = d.RefreshRates.Count > 1;

            OrientCombo.SelectedIndex = d.Rotation switch
            {
                2 => 1,
                3 => 2,
                4 => 3,
                _ => 0,
            };

            ScaleCombo.ItemsSource = new[] { $"{d.ScalePercent:0}%（推荐）" };
            ScaleCombo.SelectedIndex = 0;

            HdrToggle.Visibility = d.HdrSupported ? Visibility.Visible : Visibility.Collapsed;
            HdrDesc.Text = d.HdrSupported ? "高动态范围（需要显示器支持）" : "此显示器不支持 HDR";
            HdrToggle.IsChecked = d.HdrEnabled;
            HdrToggle.IsEnabled = d.HdrSupported;

            EnableToggle.IsChecked = true;
            EnableToggle.IsEnabled = _displays.Count > 1;

            SetPrimaryBtn.IsEnabled = !d.IsPrimary;
        }
        finally
        {
            _loading = false;
        }
    }

    // ---------------------------------------------------------------- 修改并应用

    /// <summary>捕获当前配置、修改指定显示器、整体应用。</summary>
    private async Task ApplyModifiedAsync(string gdiName, Action<MonitorSnapshot> modify)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var current = DisplayService.Instance.CaptureCurrent();
            var snap = current.Monitors.FirstOrDefault(m => m.SourceGdiDeviceName == gdiName);
            if (snap == null)
            {
                ShowResult(false, "找不到所选显示器的当前配置，请点击“重新检测”。", null);
                return;
            }

            uint oldRot = snap.Rotation;
            modify(snap);

            // 旋转变化时交换桌面源分辨率（面板原生时序不随旋转变化）
            bool oldPortrait = oldRot is 2 or 4;
            bool newPortrait = snap.Rotation is 2 or 4;
            if (oldPortrait != newPortrait)
            {
                (snap.SourceWidth, snap.SourceHeight) = (snap.SourceHeight, snap.SourceWidth);
            }

            var result = await ApplyGuard.Instance.ApplyAsync(current);
            ShowResult(result.Success, result.ErrorMessage, result.Warnings);
        }
        catch (Exception ex)
        {
            AppPaths.Log("ApplyModified 异常: " + ex);
            ShowResult(false, ex.Message, null);
        }
        finally
        {
            _busy = false;
            RefreshAll();
        }
    }

    private async void OnResChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selected == null || ResCombo.SelectedIndex < 0) return;
        if (ResCombo.SelectedIndex >= _selected.Resolutions.Count) return;
        var dim = _selected.Resolutions[ResCombo.SelectedIndex];
        if (dim.W == _selected.W && dim.H == _selected.H) return;

        var gdi = _selected.SourceGdiName;
        var refreshNum = _selected.RefreshNum;
        var refreshDen = _selected.RefreshDen;
        var refreshHz = _selected.RefreshHz;
        await ApplyModifiedAsync(gdi, snap =>
        {
            snap.SourceWidth = dim.W;
            snap.SourceHeight = dim.H;
            bool portrait = snap.Rotation is 2 or 4;
            snap.PanelWidth = portrait ? dim.H : dim.W;
            snap.PanelHeight = portrait ? dim.W : dim.H;
            // 分辨率变化后目标时序不再适用，交由系统按新分辨率重新选择
            snap.RefreshHz = refreshHz;
            snap.RefreshNumerator = refreshNum;
            snap.RefreshDenominator = refreshDen;
        });
    }

    private async void OnRefreshChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selected == null || RefreshCombo.SelectedIndex < 0) return;
        if (RefreshCombo.SelectedIndex >= _selected.RefreshRates.Count) return;
        var hz = _selected.RefreshRates[RefreshCombo.SelectedIndex];
        if (Math.Abs(hz - _selected.RefreshHz) < 0.5) return;

        var gdi = _selected.SourceGdiName;
        await ApplyModifiedAsync(gdi, snap =>
        {
            snap.RefreshHz = hz;
            bool integral = Math.Abs(hz - Math.Round(hz)) < 0.01;
            snap.RefreshNumerator = integral ? (uint)Math.Round(hz) : (uint)Math.Round(hz * 1000);
            snap.RefreshDenominator = integral ? 1u : 1000u;
        });
    }

    private async void OnOrientChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _selected == null || OrientCombo.SelectedIndex < 0) return;
        uint rot = OrientCombo.SelectedIndex switch { 1 => 2, 2 => 3, 3 => 4, _ => 1 };
        if (rot == _selected.Rotation) return;
        var gdi = _selected.SourceGdiName;
        await ApplyModifiedAsync(gdi, snap => snap.Rotation = rot);
    }

    private async void OnHdrToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected == null) return;
        bool on = HdrToggle.IsChecked == true;
        if (on == _selected.HdrEnabled) return;
        var gdi = _selected.SourceGdiName;
        await ApplyModifiedAsync(gdi, snap => snap.Hdr = on);
    }

    private async void OnEnableToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _selected == null) return;
        if (EnableToggle.IsChecked != false) return; // 只处理“停用”
        if (_displays.Count <= 1)
        {
            ShowResult(false, "至少需要保留一台启用的显示器。", null);
            _loading = true;
            EnableToggle.IsChecked = true;
            _loading = false;
            return;
        }
        var gdi = _selected.SourceGdiName;
        await ApplyModifiedAsync(gdi, snap => snap.Active = false);
    }

    private async void OnSetPrimary(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected == null) return;
        _busy = true;
        try
        {
            var current = DisplayService.Instance.CaptureCurrent();
            var snap = current.Monitors.FirstOrDefault(m => m.SourceGdiDeviceName == _selected.SourceGdiName);
            if (snap == null) return;

            // 整体平移桌面坐标，使所选显示器位于 (0,0) —— 该位置即主显示器
            int dx = -snap.PositionX;
            int dy = -snap.PositionY;
            foreach (var m in current.Monitors)
            {
                m.PositionX += dx;
                m.PositionY += dy;
            }

            var result = await ApplyGuard.Instance.ApplyAsync(current);
            ShowResult(result.Success, result.ErrorMessage, result.Warnings);
        }
        catch (Exception ex)
        {
            AppPaths.Log("设为主显示器失败: " + ex);
            ShowResult(false, ex.Message, null);
        }
        finally
        {
            _busy = false;
            RefreshAll();
        }
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
                ShowResult(false, "无法读取该显示器的首选模式，未能启用。", null);
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

    private async void OnPositionCommitted(ArrangeItem item)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var current = DisplayService.Instance.CaptureCurrent();
            foreach (var snap in current.Monitors)
            {
                var it = ArrangeCanvas.Items?.FirstOrDefault(i => i.Key == snap.SourceGdiDeviceName);
                if (it != null)
                {
                    snap.PositionX = (int)Math.Round(it.X);
                    snap.PositionY = (int)Math.Round(it.Y);
                }
            }
            var result = await ApplyGuard.Instance.ApplyAsync(current);
            ShowResult(result.Success, result.ErrorMessage, result.Warnings);
        }
        catch (Exception ex)
        {
            AppPaths.Log("调整排列失败: " + ex);
            ShowResult(false, ex.Message, null);
        }
        finally
        {
            _busy = false;
            RefreshAll();
        }
    }

    private void OnIdentify(object sender, RoutedEventArgs e) =>
        IdentifyService.Identify(_displays);

    private void OnDetect(object sender, RoutedEventArgs e)
    {
        ShowResult(true, null, null, "已重新检测显示器。");
        RefreshAll();
    }

    // ---------------------------------------------------------------- 反馈

    private void ShowResult(bool success, string? error, List<string>? warnings, string? successMessage = null)
    {
        ResultBar.IsOpen = true;
        if (!success)
        {
            ResultBar.Severity = Wpf.Ui.Controls.InfoBarSeverity.Error;
            ResultBar.Title = "应用失败";
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
            ResultBar.Title = "已应用";
            ResultBar.Message = successMessage ?? "显示器配置已更新。";
        }
    }
}
