using System.Text.Json;
using TMSpeech.Core.Plugins;

namespace TMSpeech.Recognizer.LLMAudio;

public class LLMAudioConfigEditor : IPluginConfigEditor
{
    private readonly Dictionary<string, object> _values = new();
    private readonly List<PluginConfigFormItem> _formItems = new();

    public event EventHandler<EventArgs>? FormItemsUpdated;
    public event EventHandler<EventArgs>? ValueUpdated;

    public LLMAudioConfigEditor()
    {
        var def = new LLMAudioConfig();
        _values["apiKey"] = def.ApiKey;
        _values["model"] = def.Model;
        _values["region"] = def.Region;
        _values["maxSentenceSilence"] = def.MaxSentenceSilence;

        _formItems.Add(new PluginConfigFormItemPassword("apiKey", "API Key",
            Description: "百炼 API Key（sk-xxx），北京/新加坡地域的 Key 不同"));
        _formItems.Add(new PluginConfigFormItemText("model", "模型",
            Placeholder: "fun-asr-realtime / paraformer-realtime-v2"));
        _formItems.Add(new PluginConfigFormItemOption("region", "服务地域",
            new Dictionary<object, string>
            {
                ["beijing"] = "华北2（北京）",
                ["singapore"] = "新加坡"
            }));
        _formItems.Add(new PluginConfigFormItemNumber("maxSentenceSilence", "断句静音(ms，0=默认)",
            Min: 0, Max: 6000));
    }

    public IReadOnlyList<PluginConfigFormItem> GetFormItems() => _formItems.AsReadOnly();

    public IReadOnlyDictionary<string, object> GetAll() => _values;

    public void SetValue(string key, object value)
    {
        _values[key] = value;
        ValueUpdated?.Invoke(this, EventArgs.Empty);
    }

    public object GetValue(string key) => _values.TryGetValue(key, out var value) ? value : "";

    public string GenerateConfig()
    {
        var config = new LLMAudioConfig
        {
            ApiKey = Str("apiKey"),
            Model = string.IsNullOrWhiteSpace(Str("model")) ? "fun-asr-realtime" : Str("model"),
            Region = string.IsNullOrWhiteSpace(Str("region")) ? "beijing" : Str("region"),
            MaxSentenceSilence = Int("maxSentenceSilence", 0)
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
            var cfg = JsonSerializer.Deserialize<LLMAudioConfig>(config);
            if (cfg != null)
            {
                _values["apiKey"] = cfg.ApiKey;
                _values["model"] = string.IsNullOrWhiteSpace(cfg.Model) ? "fun-asr-realtime" : cfg.Model;
                _values["region"] = string.IsNullOrWhiteSpace(cfg.Region) ? "beijing" : cfg.Region;
                _values["maxSentenceSilence"] = cfg.MaxSentenceSilence;
            }
        }
        catch
        {
            // 加载失败保留默认值
        }
    }

    private string Str(string key) => _values.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

    private int Int(string key, int fallback)
    {
        if (_values.TryGetValue(key, out var v) && v != null)
        {
            try { return Convert.ToInt32(v); }
            catch { return fallback; }
        }

        return fallback;
    }
}
