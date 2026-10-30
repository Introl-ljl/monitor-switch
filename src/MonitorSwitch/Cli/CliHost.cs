using System.Reflection;
using System.Text.Json;
using MonitorSwitch.I18n;
using MonitorSwitch.Models;
using MonitorSwitch.Services;

namespace MonitorSwitch.Cli;

/// <summary>
/// CLI 入口与命令分发。
///
/// 双运行形态：
/// 1. 中继（应用在运行）：命令经命名管道交给常驻实例执行，复用它的模板存储、
///    全局快捷键与 15 秒还原保护，托盘菜单/界面随 Changed 事件自动刷新；
/// 2. 直连（应用未运行）：在本进程内直接调用 CCD 与 JSON 存储，无窗口无托盘，
///    也没有还原保护（结果里会给出提示）。
///
/// 线程约定（中继模式由管道线程进入）：
///  - 模板/设置等“改内存 + 发事件”的操作必须经 <see cref="OnUi"/> 回到 UI 线程，
///    因为 Changed/SettingsChanged 订阅者会触碰 WPF 对象；
///  - 纯 CCD 调用（查询/应用/投影）放线程池，ApplyGuard 内部自行调度 UI。
/// </summary>
public static class CliHost
{
    private static readonly string[] Verbs =
    {
        "list", "apply", "save", "delete", "rename", "hotkey",
        "displays", "set", "topology", "config", "help", "version",
    };

    /// <summary>OnStartup 最早一步判断：本次启动是 CLI 调用还是托盘应用。</summary>
    public static bool IsCliInvocation(string[] args) =>
        args.Length > 0
        && !args[0].StartsWith("-", StringComparison.Ordinal)
        && Verbs.Contains(args[0].ToLowerInvariant());

    // ---------------------------------------------------------------- 进程入口

    /// <summary>CLI 进程入口（在单实例/托盘逻辑之前调用）。返回进程退出码。</summary>
    public static int Run(string[] args)
    {
        ConsoleAttach.TryAttach();

        CliRequest request;
        try
        {
            request = Parse(args);
        }
        catch (CliUsageException ex)
        {
            Print(UsageError(ex.Message), json: false);
            return 2;
        }

        // 语言：直连模式的文案由本进程产出；中继模式由常驻实例产出，这里只是兜底。
        // 模板存储同样必须加载：GUI 路径的 Load 在 OnStartup 里，而 CLI 早退分支跳过了它，
        // 不加载的话直连模式会看到空的模板列表。
        try
        {
            SettingsService.Instance.Load();
            ProfileStore.Instance.Load();
            Localization.Instance.Apply(SettingsService.Instance.Current.Language);
        }
        catch { /* 语言取不到就用英文回退，不影响命令执行 */ }

        // 优先中继给常驻实例；不可达（应用未运行）则直连执行
        var relayed = CliPipe.TryCallAsync(request).GetAwaiter().GetResult();
        if (relayed is not null)
        {
            Print(relayed, request.Json);
            return relayed.ExitCode;
        }

        CliResponse response;
        try
        {
            response = ExecuteAsync(request, relayed: false).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppPaths.Log("CLI 执行异常: " + ex);
            response = Fail(Strings.F("Cli.Err.Failed", ex.Message));
        }
        Print(response, request.Json);
        return response.ExitCode;
    }

    /// <summary>管道服务端与直连模式共用的命令分发入口。</summary>
    public static async Task<CliResponse> ExecuteAsync(CliRequest req, bool relayed)
    {
        try
        {
            switch (req.Command)
            {
                case "list": return await CmdListAsync(req);
                case "displays": return await Task.Run(CmdDisplays);
                case "apply": return await CmdApplyAsync(req, relayed);
                case "set": return await CmdSetAsync(req, relayed);
                case "topology": return await CmdTopologyAsync(req, relayed);
                case "save": return await CmdSaveAsync(req);
                case "delete": return CmdDelete(req);
                case "rename": return CmdRename(req);
                case "hotkey": return CmdHotkey(req);
                case "config": return await CmdConfigAsync(req);
                case "version": return CmdVersion();
                case "help": return CmdHelp();
                default:
                    return UsageError(Strings.F("Cli.Err.UnknownCommand", req.Command));
            }
        }
        catch (CliUsageException ex)
        {
            return UsageError(ex.Message);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"CLI {req.Command} 执行异常: " + ex);
            return Fail(Strings.F("Cli.Err.Failed", ex.Message));
        }
    }

    internal static CliRequest Parse(string[] args)
    {
        var req = new CliRequest();
        foreach (var a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var body = a[2..];
                int eq = body.IndexOf('=');
                var key = (eq < 0 ? body : body[..eq]).ToLowerInvariant();
                req.Flags[key] = eq < 0 ? "true" : body[(eq + 1)..];
            }
            else
            {
                req.Args.Add(a);
            }
        }
        if (req.Args.Count == 0)
            throw new CliUsageException(Strings.T("Cli.Help.Text"));
        req.Command = req.Args[0].ToLowerInvariant();
        req.Args.RemoveAt(0);
        return req;
    }

    private static void Print(CliResponse r, bool json)
    {
        if (json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(r, CliJson.Options));
            return;
        }
        if (!string.IsNullOrEmpty(r.Message)) Console.Out.WriteLine(r.Message);
        foreach (var w in r.Warnings) Console.Error.WriteLine("warning: " + w);
        if (!r.Ok && !string.IsNullOrEmpty(r.Error)) Console.Error.WriteLine("error: " + r.Error);
    }

    // ---------------------------------------------------------------- 结果与工具

    private static CliResponse Ok(string? message = null, object? data = null, List<string>? warnings = null) =>
        new() { Ok = true, ExitCode = 0, Message = message, Data = data, Warnings = warnings ?? new List<string>() };

    private static CliResponse Fail(string error) =>
        new() { Ok = false, ExitCode = 1, Error = error };

    private static CliResponse UsageError(string error) =>
        new() { Ok = false, ExitCode = 2, Error = error };

    private static T OnUi<T>(Func<T> func)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) return func();
        return app.Dispatcher.Invoke(func);
    }

    private static void OnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.Invoke(action);
    }

    private static string RequireArg(CliRequest req, int index, string what)
    {
        if (index >= req.Args.Count || string.IsNullOrWhiteSpace(req.Args[index]))
            throw new CliUsageException(Strings.F("Cli.Err.MissingArg", what));
        return req.Args[index].Trim();
    }

    private static void AddModeNote(List<string> warnings, bool relayed)
    {
        if (relayed)
        {
            if (SettingsService.Instance.Current.RevertGuardEnabled)
                warnings.Add(Strings.T("Cli.Apply.GuardNote"));
        }
        else
        {
            warnings.Add(Strings.T("Cli.Apply.DirectNote"));
        }
    }

    /// <summary>按 Id → 完整名称 → 唯一前缀 的顺序解析模板；歧义与缺失都给出候选。</summary>
    private static DisplayProfile ResolveProfileOrThrow(string token)
    {
        return OnUi(() =>
        {
            var profiles = ProfileStore.Instance.Profiles;
            if (profiles.Count == 0)
                throw new CliUsageException(Strings.T("Cli.Err.NoProfiles"));

            var names = string.Join(", ", profiles.Select(p => p.Name));

            var byId = profiles.FirstOrDefault(p => p.Id.Equals(token, StringComparison.OrdinalIgnoreCase));
            if (byId is not null) return byId;

            var byName = profiles.Where(p => p.Name.Equals(token, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            if (byName.Count > 1)
                throw new CliUsageException(Strings.F("Cli.Err.AmbiguousProfile", token, names));

            var byPrefix = profiles.Where(p => p.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byPrefix.Count == 1) return byPrefix[0];
            if (byPrefix.Count > 1)
                throw new CliUsageException(Strings.F("Cli.Err.AmbiguousProfile", token, names));

            throw new CliUsageException(Strings.F("Cli.Err.ProfileNotFound", token, names));
        });
    }

    // ---------------------------------------------------------------- 模板命令

    private static async Task<CliResponse> CmdListAsync(CliRequest req)
    {
        var profiles = OnUi(() => ProfileStore.Instance.Profiles.ToArray());
        if (profiles.Length == 0)
            return Ok(Strings.T("Cli.Err.NoProfiles"));

        var currentIds = await Task.Run(() =>
        {
            try
            {
                var cur = DisplayService.Instance.GetDisplays();
                return profiles
                    .Where(p => DisplayService.Instance.MatchesCurrent(p, cur))
                    .Select(p => p.Id)
                    .ToHashSet(StringComparer.Ordinal);
            }
            catch
            {
                return new HashSet<string>(StringComparer.Ordinal);
            }
        });

        var data = profiles.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            hotkey = p.Hotkey,
            current = currentIds.Contains(p.Id),
            createdAt = p.CreatedAt,
            modifiedAt = p.ModifiedAt,
            monitors = p.Monitors.Where(m => m.Active).Select(m => new
            {
                name = m.MonitorName,
                width = m.DesktopWidth,
                height = m.DesktopHeight,
                refreshHz = m.RefreshHz,
                rotation = m.Rotation,
                hdr = m.Hdr,
            }).ToList(),
        }).ToList();

        if (req.Json) return Ok(data: data);

        int width = Math.Min(28, profiles.Max(p => DisplayWidth(p.Name)));
        var lines = new List<string>();
        foreach (var p in profiles)
        {
            var marker = currentIds.Contains(p.Id) ? "*" : " ";
            var hotkey = string.IsNullOrWhiteSpace(p.Hotkey) ? "-" : p.Hotkey;
            lines.Add($"{marker} {PadDisplay(p.Name, width)}  {p.Summary}  [{hotkey}]");
        }
        return Ok(string.Join(Environment.NewLine, lines), data);
    }

    /// <summary>终端列对齐按显示宽度：CJK 全角字符按 2 列计，避免中文模板名错位。</summary>
    internal static int DisplayWidth(string s) =>
        s.Sum(ch => ch >= 0x1100 && (
            ch <= 0x115F ||                          // Hangul Jamo
            (ch >= 0x2E80 && ch <= 0xA4CF) ||        // CJK 部首/符号/音节
            (ch >= 0xAC00 && ch <= 0xD7A3) ||        // Hangul 音节
            (ch >= 0xF900 && ch <= 0xFAFF) ||        // CJK 兼容表意
            (ch >= 0xFE30 && ch <= 0xFE4F) ||        // CJK 兼容形式
            (ch >= 0xFF00 && ch <= 0xFF60) ||        // 全角形式
            (ch >= 0xFFE0 && ch <= 0xFFE6))          // 全角符号
            ? 2 : 1);

    private static string PadDisplay(string s, int width)
    {
        int pad = width - DisplayWidth(s);
        return pad <= 0 ? s : s + new string(' ', pad);
    }

    private static async Task<CliResponse> CmdApplyAsync(CliRequest req, bool relayed)
    {
        var token = RequireArg(req, 0, "name|id");
        var profile = ResolveProfileOrThrow(token);

        var already = await Task.Run(() =>
        {
            try { return DisplayService.Instance.MatchesCurrent(profile); }
            catch { return false; }
        });
        if (already)
            return Ok(Strings.F("Cli.Apply.AlreadyActive", profile.Name),
                new { id = profile.Id, name = profile.Name, applied = false });

        var result = relayed
            ? await ApplyGuard.Instance.ApplyAsync(profile)
            : await Task.Run(() => DisplayService.Instance.ApplyProfile(profile));

        if (!result.Success)
            return Fail(result.ErrorMessage ?? Strings.F("Apply.InternalError", "-"));

        var warnings = new List<string>(result.Warnings);
        AddModeNote(warnings, relayed);
        return Ok(Strings.F("Cli.Apply.Done", profile.Name),
            new { id = profile.Id, name = profile.Name, applied = true }, warnings);
    }

    private static async Task<CliResponse> CmdSaveAsync(CliRequest req)
    {
        var name = RequireArg(req, 0, "name");
        var store = ProfileStore.Instance;

        var existing = OnUi(() =>
            store.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));

        var captured = await Task.Run(() => DisplayService.Instance.CaptureCurrent());
        if (captured.Monitors.Count == 0)
            return Fail(Strings.T("Cli.Err.DisplaysEmpty"));

        if (existing is null)
        {
            captured.Name = name;
            OnUi(() => store.Add(captured));
            return Ok(Strings.F("Cli.Save.Done", name, captured.Monitors.Count),
                new { id = captured.Id, name = captured.Name, created = true });
        }

        if (!req.Flags.ContainsKey("replace"))
            throw new CliUsageException(Strings.F("Cli.Save.Exists", existing.Name));

        OnUi(() =>
        {
            existing.Monitors = captured.Monitors;
            existing.ModifiedAt = DateTime.Now;
            store.Touch();
        });
        return Ok(Strings.F("Cli.Save.Replaced", existing.Name),
            new { id = existing.Id, name = existing.Name, created = false });
    }

    private static CliResponse CmdDelete(CliRequest req)
    {
        var token = RequireArg(req, 0, "name|id");
        var profile = ResolveProfileOrThrow(token);
        OnUi(() => ProfileStore.Instance.Remove(profile.Id));
        return Ok(Strings.F("Cli.Delete.Done", profile.Name),
            new { id = profile.Id, name = profile.Name });
    }

    private static CliResponse CmdRename(CliRequest req)
    {
        var oldToken = RequireArg(req, 0, "old");
        var newName = RequireArg(req, 1, "new");
        var profile = ResolveProfileOrThrow(oldToken);
        var store = ProfileStore.Instance;

        var renamed = OnUi(() =>
        {
            var clash = store.Profiles.FirstOrDefault(p =>
                p.Id != profile.Id && p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase));
            if (clash is not null) return false;
            profile.Name = newName;
            profile.ModifiedAt = DateTime.Now;
            store.Touch();
            return true;
        });
        if (!renamed)
            throw new CliUsageException(Strings.F("Cli.Rename.Exists", newName));

        return Ok(Strings.F("Cli.Rename.Done", oldToken, newName),
            new { id = profile.Id, name = newName });
    }

    private static CliResponse CmdHotkey(CliRequest req)
    {
        var token = RequireArg(req, 0, "name|id");
        var combo = RequireArg(req, 1, "hotkey");
        var profile = ResolveProfileOrThrow(token);
        var store = ProfileStore.Instance;

        string? normalized;
        if (combo.Equals("none", StringComparison.OrdinalIgnoreCase)
            || combo.Equals("off", StringComparison.OrdinalIgnoreCase)
            || combo.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            normalized = null;
        }
        else
        {
            if (!HotkeyService.TryParse(combo, out _, out _))
                throw new CliUsageException(Strings.F("Cli.Hotkey.Invalid", combo));
            normalized = combo;
        }

        var takenBy = OnUi<string?>(() =>
        {
            if (normalized is null)
            {
                profile.Hotkey = null;
                profile.ModifiedAt = DateTime.Now;
                store.Touch();
                return null;
            }
            var other = store.Profiles.FirstOrDefault(p =>
                p.Id != profile.Id &&
                p.Hotkey is not null &&
                p.Hotkey.Equals(normalized, StringComparison.OrdinalIgnoreCase));
            if (other is not null) return other.Name;

            profile.Hotkey = normalized;
            profile.ModifiedAt = DateTime.Now;
            store.Touch();
            return null;
        });

        if (takenBy is not null)
            throw new CliUsageException(Strings.F("Cli.Hotkey.Taken", combo, takenBy));

        return Ok(normalized is null
            ? Strings.F("Cli.Hotkey.Cleared", profile.Name)
            : Strings.F("Cli.Hotkey.Set", profile.Name, normalized),
            new { id = profile.Id, name = profile.Name, hotkey = normalized });
    }

    // ---------------------------------------------------------------- 显示器命令

    private static CliResponse CmdDisplays()
    {
        var displays = DisplayService.Instance.GetDisplays();
        if (displays.Count == 0)
            return Fail(Strings.T("Cli.Err.DisplaysEmpty"));

        var data = displays.Select(d => new
        {
            number = d.Number,
            name = d.DisplayName,
            monitorName = d.MonitorName,
            adapter = d.AdapterName,
            connector = d.Connector,
            connectorName = d.ConnectorName,
            gdiDevice = d.SourceGdiName,
            devicePath = d.TargetDevicePath,
            x = d.X,
            y = d.Y,
            width = d.W,
            height = d.H,
            sourceWidth = d.SourceW,
            sourceHeight = d.SourceH,
            refreshHz = d.RefreshHz,
            rotation = d.Rotation,
            scaling = d.Scaling,
            scalePercent = d.ScalePercent,
            hdr = d.HdrEnabled,
            hdrSupported = d.HdrSupported,
            primary = d.IsPrimary,
            resolutions = d.Resolutions.Select(r => new { width = r.W, height = r.H }).ToList(),
            refreshRates = d.RefreshRates.ToList(),
        }).ToList();

        var lines = new List<string>();
        foreach (var d in displays)
        {
            var orient = d.Rotation switch
            {
                2 => Strings.T("Display.Orient.Portrait"),
                3 => Strings.T("Display.Orient.LandscapeFlipped"),
                4 => Strings.T("Display.Orient.PortraitFlipped"),
                _ => Strings.T("Display.Orient.Landscape"),
            };
            var hdr = (d.HdrSupported ? (d.HdrEnabled ? Strings.T("Cli.On") : Strings.T("Cli.Off"))
                                       : Strings.T("Cli.HdrUnsupportedShort"));
            var primary = d.IsPrimary ? "  " + Strings.T("Cli.Primary") : "";
            lines.Add(
                $"#{d.Number}  {d.DisplayName.PadRight(24)}  {d.W} × {d.H} @ {DisplayProfile.FormatHz(d.RefreshHz)}" +
                $"  {orient}  HDR {hdr}  {d.ScalePercent:0}%{primary}  ({d.X},{d.Y})  {d.ConnectorName}");
        }
        return Ok(string.Join(Environment.NewLine, lines), data);
    }

    private static async Task<CliResponse> CmdSetAsync(CliRequest req, bool relayed)
    {
        var sel = RequireArg(req, 0, "display");
        if (req.Args.Count < 3)
            throw new CliUsageException(Strings.F("Cli.Err.MissingArg", "key value"));
        var pairs = ParsePairs(req.Args);

        var all = sel.Equals("all", StringComparison.OrdinalIgnoreCase);
        // 只查一次显示器：目标定位与快照构建必须来自同一份状态，
        // 两次查询之间拓扑变化（拔线/睡眠唤醒）会让两者对不上。
        var displays = await Task.Run(() => DisplayService.Instance.GetDisplays());
        if (displays.Count == 0)
            throw new CliUsageException(Strings.T("Cli.Err.DisplaysEmpty"));

        LiveDisplay? target = null;
        if (!all)
        {
            target = displays.FirstOrDefault(d => d.Number.ToString() == sel) ?? SelectByName(displays, sel);
            if (target is null)
                throw new CliUsageException(Strings.F("Cli.Set.BadDisplay", sel));
        }

        var profile = new DisplayProfile { Name = "(cli)" };
        foreach (var d in displays)
            profile.Monitors.Add(d.ToSnapshot());
        var affected = all
            ? profile.Monitors
            : new List<MonitorSnapshot> { profile.Monitors.First(m =>
                  m.TargetDevicePath.Equals(target!.TargetDevicePath, StringComparison.OrdinalIgnoreCase)) };

        // rotation 先于 resolution 应用：分辨率始终按“应用完本条命令后的最终朝向”解释
        foreach (var (key, value) in pairs)
        {
            switch (key)
            {
                case "rotation":
                {
                    var rot = ParseRotation(value);
                    foreach (var m in affected) m.Rotation = rot;
                    break;
                }
                case "resolution":
                {
                    if (all) throw new CliUsageException(Strings.F("Cli.Set.AllNotAllowed", "resolution"));
                    var (w, h) = ParseSize(value);
                    foreach (var m in affected)
                    {
                        if (m.Rotation is 2 or 4) { m.SourceWidth = h; m.SourceHeight = w; }
                        else { m.SourceWidth = w; m.SourceHeight = h; }
                        ClearTiming(m);
                    }
                    break;
                }
                case "refresh":
                {
                    if (all) throw new CliUsageException(Strings.F("Cli.Set.AllNotAllowed", "refresh"));
                    var (num, den, hz) = ParseRefresh(value);
                    foreach (var m in affected)
                    {
                        m.RefreshNumerator = num;
                        m.RefreshDenominator = den;
                        m.RefreshHz = hz;
                        ClearTiming(m);
                    }
                    break;
                }
                case "hdr":
                {
                    var on = ParseBool("hdr", value);
                    foreach (var m in affected) m.Hdr = on;
                    break;
                }
                case "position":
                {
                    if (all) throw new CliUsageException(Strings.F("Cli.Set.AllNotAllowed", "position"));
                    var (x, y) = ParsePosition(value);
                    foreach (var m in affected) { m.PositionX = x; m.PositionY = y; }
                    break;
                }
                default:
                    throw new CliUsageException(Strings.F("Cli.Set.UnknownKey", key));
            }
        }

        // 预检（朝向不变时）：GDI 已枚举出支持的分辨率/刷新率，直接给出明确错误，
        // 避免无谓的闪屏后才发现 CCD 拒绝了请求。改变朝向时列表不可靠，跳过预检。
        if (target is not null && !pairs.Any(p => p.Key == "rotation"))
        {
            var res = pairs.FirstOrDefault(p => p.Key == "resolution");
            if (res.Key is not null)
            {
                var (w, h) = ParseSize(res.Value);
                if (!target.Resolutions.Any(r => r.W == w && r.H == h))
                    throw new CliUsageException(Strings.F("Cli.Set.BadResolution", target.Number, w, h));
            }
            var refresh = pairs.FirstOrDefault(p => p.Key == "refresh");
            if (refresh.Key is not null && res.Key is null && target.RefreshRates.Count > 0)
            {
                var hz = ParseRefresh(refresh.Value).Hz;
                if (!target.RefreshRates.Any(r => Math.Abs(r - hz) < 0.51))
                    throw new CliUsageException(Strings.F(
                        "Cli.Set.BadRefresh", target.Number, hz, string.Join(", ", target.RefreshRates)));
            }
        }

        var result = relayed
            ? await ApplyGuard.Instance.ApplyAsync(profile)
            : await Task.Run(() => DisplayService.Instance.ApplyProfile(profile));

        if (!result.Success)
            return Fail(result.ErrorMessage ?? Strings.F("Apply.InternalError", "-"));

        var warnings = new List<string>(result.Warnings);
        AddModeNote(warnings, relayed);
        var targetName = all ? Strings.T("Cli.AllDisplays") : target!.DisplayName;
        return Ok(Strings.F("Cli.Set.Done", targetName), new { applied = true }, warnings);
    }

    private static async Task<CliResponse> CmdTopologyAsync(CliRequest req, bool relayed)
    {
        var value = RequireArg(req, 0, "topology").ToLowerInvariant();
        var topology = value switch
        {
            "internal" or "internal-only" or "pc" => QuickTopology.InternalOnly,
            "clone" or "duplicate" => QuickTopology.Clone,
            "extend" => QuickTopology.Extend,
            "external" or "external-only" or "second" => QuickTopology.ExternalOnly,
            _ => throw new CliUsageException(Strings.F("Cli.Topology.Unknown", value)),
        };

        var result = relayed
            ? await ApplyGuard.Instance.ApplyTopologyAsync(topology)
            : await Task.Run(() => DisplayService.Instance.ApplyTopology(topology));

        if (!result.Success)
            return Fail(result.ErrorMessage ?? Strings.F("Apply.InternalError", "-"));

        var warnings = new List<string>(result.Warnings);
        AddModeNote(warnings, relayed);
        return Ok(Strings.F("Cli.Topology.Done", value), new { topology = value }, warnings);
    }

    // ---------------------------------------------------------------- 设置命令

    private static async Task<CliResponse> CmdConfigAsync(CliRequest req)
    {
        if (req.Args.Count == 0 || req.Args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
            return CmdConfigList(req);

        if (!req.Args[0].Equals("set", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException(Strings.F("Cli.Err.UnknownCommand", req.Args[0]));

        if (req.Args.Count < 3)
            throw new CliUsageException(Strings.F("Cli.Err.MissingArg", "key value"));
        var key = req.Args[1].ToLowerInvariant();
        var value = req.Args[2].Trim();

        var settings = SettingsService.Instance;
        // Update 会同步引发 SettingsChanged → App.OnSettingsChanged 触碰窗口标题与
        // WPF 资源字典（线程亲和），必须回 UI 线程执行；校验失败经 Dispatcher 原样传播。
        switch (key)
        {
            case "theme":
                OnUi(() => settings.Update(s => s.Theme = NormalizeTheme(key, value)));
                break;
            case "language":
                OnUi(() => settings.Update(s => s.Language = NormalizeLanguage(key, value)));
                // 直连模式下本进程没有 SettingsChanged 订阅者，主动应用一次，
                // 让这条命令自己的回显与后续输出使用新语言（中继模式由宿主应用，幂等）
                Localization.Instance.Apply(settings.Current.Language);
                break;
            case "start-minimized":
                OnUi(() => settings.Update(s => s.StartMinimized = ParseBool(key, value)));
                break;
            case "close-to-tray":
                OnUi(() => settings.Update(s => s.CloseToTray = ParseBool(key, value)));
                break;
            case "revert-guard":
                OnUi(() => settings.Update(s => s.RevertGuardEnabled = ParseBool(key, value)));
                break;
            case "autostart":
            {
                var want = ParseBool(key, value);
                var actual = await Task.Run(() => AutoStartService.SetEnabled(want));
                if (!actual)
                    return Fail(Strings.F("Cli.Err.Failed", Strings.F("Cli.Config.BadValue", "autostart", value)));
                break;
            }
            default:
                throw new CliUsageException(Strings.F("Cli.Config.UnknownKey", key));
        }

        return Ok(Strings.F("Cli.Config.Set", key, value), CmdConfigData());
    }

    private static CliResponse CmdConfigList(CliRequest req)
    {
        var data = CmdConfigData();
        if (req.Json) return Ok(data: data);
        var lines = new[]
        {
            $"theme           = {data.Theme}",
            $"language        = {data.Language}",
            $"start-minimized = {data.StartMinimized}",
            $"close-to-tray   = {data.CloseToTray}",
            $"revert-guard    = {data.RevertGuard}",
            $"autostart       = {data.Autostart}",
        };
        return Ok(string.Join(Environment.NewLine, lines), data);
    }

    private sealed record ConfigData(
        string Theme, string Language, bool StartMinimized, bool CloseToTray, bool RevertGuard, bool Autostart);

    private static ConfigData CmdConfigData()
    {
        var s = SettingsService.Instance.Current;
        return new ConfigData(
            s.Theme, s.Language, s.StartMinimized, s.CloseToTray, s.RevertGuardEnabled, AutoStartService.IsEnabled());
    }

    private static string NormalizeTheme(string key, string value) =>
        value.ToLowerInvariant() switch
        {
            "auto" or "system" => "Auto",
            "light" => "Light",
            "dark" => "Dark",
            _ => throw new CliUsageException(Strings.F("Cli.Config.BadValue", key, value)),
        };

    private static string NormalizeLanguage(string key, string value) =>
        value.ToLowerInvariant() switch
        {
            "auto" or "system" => "auto",
            "en" or "en-us" or "english" => "en",
            "zh" or "zh-cn" or "chinese" => Localization.Chinese,
            _ => throw new CliUsageException(Strings.F("Cli.Config.BadValue", key, value)),
        };

    // ---------------------------------------------------------------- 元命令

    private static CliResponse CmdVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var text = version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";
        return Ok($"MonitorSwitch v{text}", new { version = text });
    }

    private static CliResponse CmdHelp() => Ok(Strings.T("Cli.Help.Text"));

    // ---------------------------------------------------------------- 值解析

    /// <summary>把 [key, value, key, value…] 展开成键值对列表。</summary>
    internal static List<(string Key, string Value)> ParsePairs(List<string> args)
    {
        if ((args.Count - 1) % 2 != 0)
            throw new CliUsageException(Strings.F("Cli.Err.MissingArg", "value"));
        var pairs = new List<(string, string)>();
        for (int i = 1; i < args.Count; i += 2)
        {
            var key = args[i].Trim().ToLowerInvariant();
            var value = args[i + 1].Trim();
            if (key.Length == 0 || value.Length == 0)
                throw new CliUsageException(Strings.F("Cli.Err.MissingArg", "key value"));
            pairs.Add((key, value));
        }
        return pairs;
    }

    private static LiveDisplay? SelectByName(List<LiveDisplay> displays, string token)
    {
        var matches = displays
            .Where(d => d.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase)
                        || d.MonitorName.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    internal static uint ParseRotation(string value) =>
        value.ToLowerInvariant() switch
        {
            "0" or "landscape" => 1,
            "90" or "portrait" => 2,
            "180" or "landscape-flipped" or "flipped" => 3,
            "270" or "portrait-flipped" => 4,
            _ => throw new CliUsageException(Strings.F("Cli.Set.BadValue", "rotation", value)),
        };

    internal static (uint W, uint H) ParseSize(string value)
    {
        var parts = value.Split('x', 'X', '*', '×', ',');
        if (parts.Length == 2
            && uint.TryParse(parts[0].Trim(), out var w) && w > 0
            && uint.TryParse(parts[1].Trim(), out var h) && h > 0)
            return (w, h);
        throw new CliUsageException(Strings.F("Cli.Set.BadValue", "resolution", value));
    }

    internal static (uint Num, uint Den, double Hz) ParseRefresh(string value)
    {
        if (!double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var hz)
            || hz <= 0 || hz >= 500)
            throw new CliUsageException(Strings.F("Cli.Set.BadValue", "refresh", value));
        var rounded = Math.Round(hz);
        if (Math.Abs(hz - rounded) < 0.01)
            return ((uint)rounded, 1, hz);
        return ((uint)Math.Round(hz * 1000), 1000, hz);
    }

    internal static (int X, int Y) ParsePosition(string value)
    {
        var parts = value.Split(',', 'x', 'X', '*');
        if (parts.Length == 2
            && int.TryParse(parts[0].Trim(), out var x)
            && int.TryParse(parts[1].Trim(), out var y))
            return (x, y);
        throw new CliUsageException(Strings.F("Cli.Set.BadValue", "position", value));
    }

    internal static bool ParseBool(string key, string value) =>
        value.ToLowerInvariant() switch
        {
            "on" or "true" or "1" or "yes" or "enable" or "enabled" => true,
            "off" or "false" or "0" or "no" or "disable" or "disabled" => false,
            _ => throw new CliUsageException(Strings.F("Cli.Set.BadValue", key, value)),
        };

    /// <summary>
    /// 清空模板里保存的目标时序。分辨率/刷新率被修改后旧时序必然与请求不符，
    /// 交还给系统重新选择时序，否则驱动会按旧目标模式应用、改动静默失效
    /// （详见 DisplayService.TimingMatchesLiveMode 的说明）。
    /// </summary>
    private static void ClearTiming(MonitorSnapshot m)
    {
        m.PixelRate = 0;
        m.HSyncNumerator = 0;
        m.HSyncDenominator = 0;
        m.VSyncNumerator = 0;
        m.VSyncDenominator = 0;
    }
}
