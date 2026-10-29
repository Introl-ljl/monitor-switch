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
                AppPaths.Log($"快捷键注册失败（可能已被其它程序占用）：{p.Name} → {p.Hotkey}");
            }
        }
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        for (int i = 1; i <= MaxHotkeys; i++)
            ShellNative.UnregisterHotKey(_hwnd, i);
        _idToProfileId.Clear();
    }

    /// <summary>解析 "Ctrl+Alt+1" / "Win+Shift+F5" 形式的快捷键。</summary>
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

        var key = parts[^1];
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
        return false;
    }
}
