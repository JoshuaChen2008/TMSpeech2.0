using System.Text.Json;
using TMSpeech.Core.Plugins;

namespace TMSpeech.Recognizer.StreamingAsr;

/// <summary>
/// 配置编辑器：先选「协议预设」，再按所选预设动态展示需要填的字段
/// （API Key / 模型 / 区域；NLS 则换成 AppKey + AccessKey；custom 则给 JSON 文本框）。
/// </summary>
public class StreamingAsrConfigEditor : IPluginConfigEditor
{
    private readonly Dictionary<string, object> _values = new();

    public event EventHandler<EventArgs>? FormItemsUpdated;
    public event EventHandler<EventArgs>? ValueUpdated;

    public StreamingAsrConfigEditor()
    {
        var def = new StreamingAsrConfig();
        _values["preset"] = def.Preset;
        _values["apiKey"] = def.ApiKey;
        _values["model"] = def.Model;
        _values["region"] = def.Region;
        _values["appKey"] = def.AppKey;
        _values["accessKeyId"] = def.AccessKeyId;
        _values["accessKeySecret"] = def.AccessKeySecret;
        _values["customProfileJson"] = def.CustomProfileJson;
    }

    public IReadOnlyList<PluginConfigFormItem> GetFormItems()
    {
        var items = new List<PluginConfigFormItem>();

        var presetOptions = ProfilePresets.Options()
            .ToDictionary(kv => (object)kv.Key, kv => kv.Value);
        items.Add(new PluginConfigFormItemOption("preset", "协议预设", presetOptions));

        var preset = Str("preset");

        if (preset == ProfilePresets.Custom)
        {
            items.Add(new PluginConfigFormItemPassword("apiKey", "API Key（如该协议需要）"));
            items.Add(new PluginConfigFormItemText("model", "模型"));
            items.Add(new PluginConfigFormItemText("region", "区域"));
            items.Add(new PluginConfigFormItemText("customProfileJson", "协议 JSON",
                Description: "粘贴一份 StreamingAsrProfile JSON"));
            return items;
        }

        var profile = ProfilePresets.Get(preset);
        if (profile == null) return items;

        // 区域：来自预设的 RegionUrls
        if (profile.RegionUrls.Count > 1)
        {
            var regionOptions = profile.RegionUrls.Keys
                .ToDictionary(k => (object)k, k => k);
            items.Add(new PluginConfigFormItemOption("region", "服务区域", regionOptions));
        }

        // 鉴权字段
        if (profile.NeedsAccessKey)
        {
            items.Add(new PluginConfigFormItemText("appKey", "Appkey"));
            items.Add(new PluginConfigFormItemText("accessKeyId", "AccessKeyId"));
            items.Add(new PluginConfigFormItemPassword("accessKeySecret", "AccessKeySecret"));
        }
        else
        {
            items.Add(new PluginConfigFormItemPassword("apiKey", "API Key"));
        }

        // 模型（NLS 不需要模型，DefaultModel 为空时不显示）
        if (!string.IsNullOrEmpty(profile.DefaultModel))
        {
            items.Add(new PluginConfigFormItemText("model", "模型",
                Placeholder: profile.DefaultModel));
        }

        return items;
    }

    public IReadOnlyDictionary<string, object> GetAll() => _values;

    public void SetValue(string key, object value)
    {
        _values[key] = value;
        ValueUpdated?.Invoke(this, EventArgs.Empty);
        if (key == "preset")
            FormItemsUpdated?.Invoke(this, EventArgs.Empty);
    }

    public object GetValue(string key) => _values.TryGetValue(key, out var v) ? v : "";

    public string GenerateConfig()
    {
        var config = new StreamingAsrConfig
        {
            Preset = string.IsNullOrEmpty(Str("preset")) ? ProfilePresets.DashScopeFunAsr : Str("preset"),
            ApiKey = Str("apiKey"),
            Model = Str("model"),
            Region = Str("region"),
            AppKey = Str("appKey"),
            AccessKeyId = Str("accessKeyId"),
            AccessKeySecret = Str("accessKeySecret"),
            CustomProfileJson = Str("customProfileJson")
        };
        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        });
    }

    public void LoadConfigString(string config)
    {
        if (string.IsNullOrEmpty(config)) return;
        try
        {
            var cfg = JsonSerializer.Deserialize<StreamingAsrConfig>(config);
            if (cfg != null)
            {
                _values["preset"] = string.IsNullOrEmpty(cfg.Preset) ? ProfilePresets.DashScopeFunAsr : cfg.Preset;
                _values["apiKey"] = cfg.ApiKey;
                _values["model"] = cfg.Model;
                _values["region"] = cfg.Region;
                _values["appKey"] = cfg.AppKey;
                _values["accessKeyId"] = cfg.AccessKeyId;
                _values["accessKeySecret"] = cfg.AccessKeySecret;
                _values["customProfileJson"] = cfg.CustomProfileJson;
            }

            FormItemsUpdated?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 保留默认值
        }
    }

    private string Str(string key) => _values.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
}
