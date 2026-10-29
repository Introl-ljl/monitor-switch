using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

public sealed class SettingsService
{
    public static SettingsService Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // 容忍手动编辑产生的注释与尾逗号，避免整份设置被重置
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public AppSettings Current { get; private set; } = new();

    public event Action? SettingsChanged;

    public void Load()
    {
        if (!File.Exists(AppPaths.SettingsFile)) return;
        try
        {
            var text = File.ReadAllText(AppPaths.SettingsFile);
            if (string.IsNullOrWhiteSpace(text)) return;
            Current = JsonSerializer.Deserialize<AppSettings>(text, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            // 设置文件损坏时保留副本，便于排查，然后回退到默认值
            AppPaths.Log("读取设置失败: " + ex.Message);
            try
            {
                var backup = AppPaths.SettingsFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(AppPaths.SettingsFile, backup, true);
                AppPaths.Log("已备份损坏的设置文件到 " + backup);
            }
            catch { }
            Current = new AppSettings();
        }
    }

    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Save();
        SettingsChanged?.Invoke();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(tmp, AppPaths.SettingsFile, true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("保存设置失败: " + ex.Message);
        }
    }
}
