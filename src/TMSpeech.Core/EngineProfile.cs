using System.Text.Json.Serialization;

namespace TMSpeech.Core;

/// <summary>
/// 命名引擎实例：把"一个识别器插件 + 一份配置"打包成一个有名字的档案，
/// 用于在设置界面"新建引擎 / 删除 / 切换"（对应截图交互）。
/// 例如：多个阿里云账号、不同区域，或一个云 ASR + 一个识别 LLM 并存。
/// </summary>
public class EngineProfile
{
    /// <summary>用户可见的引擎名称，例如"我的阿里云"。作为唯一标识。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>所用识别器插件的 ID（即 PluginManager.Recognizers 的键，也就是 recognizer.source 的取值）。</summary>
    [JsonPropertyName("pluginId")]
    public string PluginId { get; set; } = "";

    /// <summary>该插件配置编辑器 GenerateConfig() 产出的 JSON 配置字符串。</summary>
    [JsonPropertyName("configJson")]
    public string ConfigJson { get; set; } = "";

    public EngineProfile()
    {
    }

    public EngineProfile(string name, string pluginId, string configJson)
    {
        Name = name;
        PluginId = pluginId;
        ConfigJson = configJson;
    }
}
