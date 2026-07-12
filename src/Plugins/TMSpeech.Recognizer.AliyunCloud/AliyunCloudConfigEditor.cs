using System.Text.Json;
using TMSpeech.Core.Plugins;

namespace TMSpeech.Recognizer.AliyunCloud;

public class AliyunCloudConfigEditor : IPluginConfigEditor
{
    private readonly Dictionary<string, object> _values = new();
    private readonly List<PluginConfigFormItem> _formItems = new();

    public event EventHandler<EventArgs>? FormItemsUpdated { add { } remove { } }
    public event EventHandler<EventArgs>? ValueUpdated;

    public AliyunCloudConfigEditor()
    {
        _values["appKey"] = "";
        _values["accessKeyId"] = "";
        _values["accessKeySecret"] = "";
        _values["region"] = "cn-shanghai";
        _values["token"] = "";

        _formItems.Add(new PluginConfigFormItemText("appKey", "Appkey",
            Description: "在阿里云智能语音交互控制台创建项目后获得"));
        _formItems.Add(new PluginConfigFormItemText("accessKeyId", "AccessKeyId"));
        _formItems.Add(new PluginConfigFormItemPassword("accessKeySecret", "AccessKeySecret"));
        _formItems.Add(new PluginConfigFormItemOption("region", "服务区域",
            new Dictionary<object, string>
            {
                ["cn-shanghai"] = "中国（上海）",
                ["ap-southeast-1"] = "海外（新加坡）"
            }));
        _formItems.Add(new PluginConfigFormItemPassword("token", "Token（可选）",
            Description: "留空则用 AccessKey 自动签发；也可直接粘贴临时 Token"));
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
        var config = new AliyunCloudConfig
        {
            AppKey = Str("appKey"),
            AccessKeyId = Str("accessKeyId"),
            AccessKeySecret = Str("accessKeySecret"),
            Region = string.IsNullOrEmpty(Str("region")) ? "cn-shanghai" : Str("region"),
            Token = Str("token")
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
            var cfg = JsonSerializer.Deserialize<AliyunCloudConfig>(config);
            if (cfg != null)
            {
                _values["appKey"] = cfg.AppKey;
                _values["accessKeyId"] = cfg.AccessKeyId;
                _values["accessKeySecret"] = cfg.AccessKeySecret;
                _values["region"] = string.IsNullOrEmpty(cfg.Region) ? "cn-shanghai" : cfg.Region;
                _values["token"] = cfg.Token;
            }
        }
        catch
        {
            // 加载失败保留默认值
        }
    }

    private string Str(string key) => _values.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
}
