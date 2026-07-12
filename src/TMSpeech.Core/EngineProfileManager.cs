using System.Text.Json;

namespace TMSpeech.Core;

/// <summary>
/// 命名引擎实例的读写与激活。
///
/// 设计要点（保持对现有架构零侵入）：
/// 档案列表持久化在配置键 <see cref="RecognizerConfigTypes.Profiles"/> 下（JSON 字符串）。
/// 激活一个档案时，只是把它的插件 ID 与配置写回现有的标准键
/// （<see cref="RecognizerConfigTypes.Recognizer"/> 和 plugin.{id}.config），
/// 因此 JobManager / 识别器加载流程完全不用改动，照常工作。
/// </summary>
public static class EngineProfileManager
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private static ConfigManager Config => ConfigManagerFactory.Instance;

    /// <summary>读取全部命名引擎实例。</summary>
    public static List<EngineProfile> GetProfiles()
    {
        var raw = Config.Get<string>(RecognizerConfigTypes.Profiles);
        if (string.IsNullOrWhiteSpace(raw)) return new List<EngineProfile>();
        try
        {
            return JsonSerializer.Deserialize<List<EngineProfile>>(raw) ?? new List<EngineProfile>();
        }
        catch
        {
            return new List<EngineProfile>();
        }
    }

    /// <summary>持久化全部命名引擎实例。</summary>
    public static void SaveProfiles(IEnumerable<EngineProfile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles.ToList(), _jsonOptions);
        Config.Apply(RecognizerConfigTypes.Profiles, json);
    }

    /// <summary>按名称查找。</summary>
    public static EngineProfile? Find(string name)
    {
        return GetProfiles().FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.Ordinal));
    }

    /// <summary>新增或按名称覆盖一个引擎实例。</summary>
    public static void AddOrUpdate(EngineProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("引擎名称不能为空", nameof(profile));

        var list = GetProfiles();
        var idx = list.FindIndex(p => string.Equals(p.Name, profile.Name, StringComparison.Ordinal));
        if (idx >= 0) list[idx] = profile;
        else list.Add(profile);
        SaveProfiles(list);
    }

    /// <summary>按名称删除。若删除的是当前激活档案，则清空激活标记。</summary>
    public static void Remove(string name)
    {
        var list = GetProfiles();
        list.RemoveAll(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        SaveProfiles(list);

        if (string.Equals(Config.Get<string>(RecognizerConfigTypes.ActiveProfile), name, StringComparison.Ordinal))
            Config.Apply(RecognizerConfigTypes.ActiveProfile, "");
    }

    /// <summary>当前激活的引擎实例名称。</summary>
    public static string GetActiveProfileName()
    {
        return Config.Get<string>(RecognizerConfigTypes.ActiveProfile) ?? "";
    }

    /// <summary>
    /// 激活一个引擎实例：把它写回现有标准配置键，使下次启动识别时直接生效。
    /// 不改动 JobManager，复用原有"选中识别器 + 插件配置"机制。
    /// </summary>
    public static void Activate(EngineProfile profile)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.PluginId))
            throw new InvalidOperationException($"引擎实例「{profile.Name}」未指定插件");

        Config.BatchApply(new Dictionary<string, object>
        {
            [RecognizerConfigTypes.Recognizer] = profile.PluginId,
            [RecognizerConfigTypes.GetPluginConfigKey(profile.PluginId)] = profile.ConfigJson,
            [RecognizerConfigTypes.ActiveProfile] = profile.Name
        });
    }

    /// <summary>按名称激活。</summary>
    public static bool Activate(string name)
    {
        var profile = Find(name);
        if (profile == null) return false;
        Activate(profile);
        return true;
    }
}
