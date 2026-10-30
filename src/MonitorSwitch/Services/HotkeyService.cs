using System.Runtime.InteropServices;
using MonitorSwitch.Interop;
using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

/// <summary>配置模板全局快捷键（RegisterHotKey）。</summary>
public sealed class HotkeyService : IDisposable
{
    private const int MaxHotkeys = 64;

    private IntPtr _hwnd;
    private readonly Dictionary<int, string> _idToProfileId = new();

    /// <summary>参数为模板 Id。</summary>
    public event Action<string>? HotkeyPressed;

    /// <summary>
    /// 本轮 Refresh 中有组合注册失败时触发（参数为失败数）。
    /// 失败通常是被其它程序占用，用户在界面上看不到任何反馈，由宿主用托盘气泡提示。
    /// </summary>
    public event Action<int>? RegistrationFailures;

    /// <summary>上一轮 Refresh 中注册失败的数量（供显示层判断是否提示过）。</summary>
    public int LastFailureCount { get; private set; }

    public void Attach(IntPtr hwnd) => _hwnd = hwnd;

    public void HandleWmHotkey(int id)
    {
        if (_idToProfileId.TryGetValue(id, out var pid))
            HotkeyPressed?.Invoke(pid);
    }

    public void Refresh(IEnumerable<DisplayProfile> profiles)
    {
        if (_hwnd == IntPtr.Zero) return;

        for (int i = 1; i <= MaxHotkeys; i++)
            ShellNative.UnregisterHotKey(_hwnd, i);
        _idToProfileId.Clear();

        int id = 1;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int failures = 0;
        foreach (var p in profiles)
        {
            if (string.IsNullOrWhiteSpace(p.Hotkey)) continue;
            if (id > MaxHotkeys) break;
            // 同一组合只注册一次，避免后者静默失败
            if (!used.Add(p.Hotkey)) continue;

            if (TryParse(p.Hotkey, out var mods, out var vk) &&
                ShellNative.RegisterHotKey(_hwnd, id, mods | ShellNative.MOD_NOREPEAT, vk))
            {
                _idToProfileId[id] = p.Id;
                id++;
            }
            else
            {
                failures++;
                // 带上真实错误码：被占用是 ERROR_HOTKEY_ALREADY_REGISTERED(1409)，
                // 而跨线程调用会得到 ERROR_WINDOW_OF_OTHER_THREAD(1408)，归因完全不同
                var err = Marshal.GetLastWin32Error();
                AppPaths.Log($"快捷键注册失败 ({p.Name} → {p.Hotkey})，Win32 错误码 {err}");
            }
        }

        if (failures > 0)
            RegistrationFailures?.Invoke(failures);
        LastFailureCount = failures;
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        for (int i = 1; i <= MaxHotkeys; i++)
            ShellNative.UnregisterHotKey(_hwnd, i);
        _idToProfileId.Clear();
    }

    /// <summary>
    /// 解析 "Ctrl+Alt+1" / "Win+Shift+F5" 形式的快捷键。
    /// 键位支持：0-9、A-Z、F1-F24、NumPad0-9、Space，
    /// 以及 OEM 标点键（按命名 token 书写，避免 "+/," 等字符与分隔符冲突）：
    /// Semicolon、Equal/Plus、Comma、Minus、Period、Slash、Backquote、
    /// LeftBracket、Backslash、RightBracket、Quote。
    /// </summary>
    public static bool TryParse(string? text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false; // 必须至少带一个修饰键

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ShellNative.MOD_CONTROL; break;
                case "alt": mods |= ShellNative.MOD_ALT; break;
                case "shift": mods |= ShellNative.MOD_SHIFT; break;
                case "win" or "windows": mods |= ShellNative.MOD_WIN; break;
                default: return false;
            }
        }

        return TryKeyToVk(parts[^1], out vk);
    }

    /// <summary>末位键名 → 虚拟键码。</summary>
    private static bool TryKeyToVk(string key, out uint vk)
    {
        vk = 0;
        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z')
            {
                vk = c;
                return true;
            }
            return false;
        }
        if (key.Length >= 2 && key[0] is 'F' or 'f' &&
            int.TryParse(key[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        vk = key.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "numpad0" => 0x60, "numpad1" => 0x61, "numpad2" => 0x62, "numpad3" => 0x63,
            "numpad4" => 0x64, "numpad5" => 0x65, "numpad6" => 0x66, "numpad7" => 0x67,
            "numpad8" => 0x68, "numpad9" => 0x69,
            "semicolon" or "oem1" => 0xBA,
            "equal" or "equals" or "plus" or "oemplus" => 0xBB,
            "comma" or "oemcomma" => 0xBC,
            "minus" or "oemminus" => 0xBD,
            "period" or "oemperiod" => 0xBE,
            "slash" or "question" or "oem2" => 0xBF,
            "backquote" or "tilde" or "oem3" => 0xC0,
            "leftbracket" or "oem4" => 0xDB,
            "backslash" or "oem5" => 0xDC,
            "rightbracket" or "oem6" => 0xDD,
            "quote" or "oem7" => 0xDE,
            _ => 0,
        };
        return vk != 0;
    }
}
