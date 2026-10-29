using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using MonitorSwitch.Models;

namespace MonitorSwitch.Services;

/// <summary>配置模板存储（profiles.json）。</summary>
public sealed class ProfileStore
{
    public static ProfileStore Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 手动编辑过的文件可能带注释/尾逗号，读取时容忍
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public List<DisplayProfile> Profiles { get; private set; } = new();

    public event Action? Changed;

    public void Load()
    {
        Profiles = new List<DisplayProfile>();
        if (!File.Exists(AppPaths.ProfilesFile)) return;

        string text;
        try
        {
            text = File.ReadAllText(AppPaths.ProfilesFile);
        }
        catch (Exception ex)
        {
            AppPaths.Log("读取模板文件失败: " + ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            Profiles = Parse(text);
            // 读入后顺手清理明显无效的条目（没有显示器的模板无法应用）
            int removed = Profiles.RemoveAll(p => p is null || p.Monitors is null || p.Monitors.Count == 0);
            if (removed > 0)
                AppPaths.Log($"已忽略 {removed} 份不含显示器数据的模板");
            foreach (var p in Profiles)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "未命名配置";
            }
            return;
        }
        catch (Exception ex)
        {
            AppPaths.Log("解析模板失败: " + ex.Message);
        }

        // 解析失败时不要直接丢弃用户数据：先备份，再从备份里尽力抢救可用的条目
        var backup = AppPaths.ProfilesFile + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        try
        {
            File.Copy(AppPaths.ProfilesFile, backup, true);
            AppPaths.Log("已备份损坏的模板文件到 " + backup);
        }
        catch (Exception ex)
        {
            AppPaths.Log("备份损坏文件失败: " + ex.Message);
        }

        Profiles = Salvage(text);
        if (Profiles.Count > 0)
            AppPaths.Log($"已从损坏文件中抢救出 {Profiles.Count} 份模板");
        else
            AppPaths.Log("模板文件无法解析，已从空列表开始（原文件已备份）");
    }

    /// <summary>同时接受数组与单个对象两种写法（手写/旧版本导出可能只有一份）。</summary>
    private static List<DisplayProfile> Parse(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('['))
            return JsonSerializer.Deserialize<List<DisplayProfile>>(text, JsonOptions) ?? new List<DisplayProfile>();

        var single = JsonSerializer.Deserialize<DisplayProfile>(text, JsonOptions);
        return single is null ? new List<DisplayProfile>() : new List<DisplayProfile> { single };
    }

    /// <summary>把文件里能单独解析出来的对象逐个抢救出来。</summary>
    private static List<DisplayProfile> Salvage(string text)
    {
        var result = new List<DisplayProfile>();
        int depth = 0, start = -1;
        bool inString = false, escape = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; continue; }
            if (c == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    var chunk = text[start..(i + 1)];
                    start = -1;
                    try
                    {
                        var p = JsonSerializer.Deserialize<DisplayProfile>(chunk, JsonOptions);
                        if (p is not null && p.Monitors is { Count: > 0 }) result.Add(p);
                    }
                    catch { }
                }
            }
        }
        return result;
    }

    public void Add(DisplayProfile profile)
    {
        Profiles.Add(profile);
        Save();
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        Profiles.RemoveAll(p => p.Id == id);
        Save();
        Changed?.Invoke();
    }

    /// <summary>模板内容在原地被修改（重命名 / 快捷键等）后调用。</summary>
    public void Touch()
    {
        Save();
        Changed?.Invoke();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var tmp = AppPaths.ProfilesFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Profiles, JsonOptions));
            File.Move(tmp, AppPaths.ProfilesFile, true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("保存模板失败: " + ex.Message);
        }
    }

    public void RaiseChanged() => Changed?.Invoke();
}
