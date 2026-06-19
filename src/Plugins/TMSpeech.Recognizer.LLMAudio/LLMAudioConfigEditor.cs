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
        _values["baseUrl"] = def.BaseUrl;
        _values["apiKey"] = def.ApiKey;
        _values["model"] = def.Model;
        _values["language"] = def.Language;
        _values["prompt"] = def.Prompt;
        _values["maxSegmentMs"] = def.MaxSegmentMs;
        _values["silenceMs"] = def.SilenceMs;

        _formItems.Add(new PluginConfigFormItemText("baseUrl", "API 地址",
            Placeholder: "https://api.openai.com/v1"));
        _formItems.Add(new PluginConfigFormItemPassword("apiKey", "API Key"));
        _formItems.Add(new PluginConfigFormItemText("model", "模型",
            Placeholder: "gpt-4o-audio-preview / qwen-audio-turbo"));
        _formItems.Add(new PluginConfigFormItemText("language", "识别语言",
            Placeholder: "中文 / English / auto"));
        _formItems.Add(new PluginConfigFormItemText("prompt", "提示词",
            Description: "务必强调只输出原文、不要翻译"));
        _formItems.Add(new PluginConfigFormItemNumber("maxSegmentMs", "最长断句(ms)",
            Min: 1000, Max: 30000));
        _formItems.Add(new PluginConfigFormItemNumber("silenceMs", "静音断句(ms)",
            Min: 200, Max: 5000));
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
            BaseUrl = Str("baseUrl"),
            ApiKey = Str("apiKey"),
            Model = Str("model"),
            Language = Str("language"),
            Prompt = Str("prompt"),
            MaxSegmentMs = Int("maxSegmentMs", 8000),
            SilenceMs = Int("silenceMs", 700)
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
                _values["baseUrl"] = cfg.BaseUrl;
                _values["apiKey"] = cfg.ApiKey;
                _values["model"] = cfg.Model;
                _values["language"] = cfg.Language;
                _values["prompt"] = cfg.Prompt;
                _values["maxSegmentMs"] = cfg.MaxSegmentMs;
                _values["silenceMs"] = cfg.SilenceMs;
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
