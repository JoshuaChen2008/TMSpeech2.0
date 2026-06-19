using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using TMSpeech.Core.Plugins;

namespace TMSpeech.Recognizer.StreamingAsr;

/// <summary>
/// 通用流式语音识别配置：选一个协议预设 + 填该预设需要的参数。
/// </summary>
public class StreamingAsrConfig
{
    [JsonPropertyName("preset")] public string Preset { get; set; } = ProfilePresets.DashScopeFunAsr;
    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("region")] public string Region { get; set; } = "";

    // NLS 专用
    [JsonPropertyName("appKey")] public string AppKey { get; set; } = "";
    [JsonPropertyName("accessKeyId")] public string AccessKeyId { get; set; } = "";
    [JsonPropertyName("accessKeySecret")] public string AccessKeySecret { get; set; } = "";

    /// <summary>preset=custom 时的协议 JSON。</summary>
    [JsonPropertyName("customProfileJson")] public string CustomProfileJson { get; set; } = "";
}

/// <summary>
/// 通用流式语音识别器：把所选预设（或自定义）的协议模板交给 StreamingAsrEngine 驱动。
/// 新增厂商通常只需在 ProfilePresets 加一个预设，无需改本类。
/// </summary>
public class StreamingAsrRecognizer : IRecognizer
{
    public string GUID => "D9F5E4C3-5B2A-4C7D-9E0F-3A4B5C6D7E8F";
    public string Name => "通用流式语音识别";
    public string Description => "数据驱动的流式 ASR：内置阿里云 Fun-ASR / NLS / OpenAI Realtime 预设，可自定义协议";
    public string Version => "1.0.0";
    public string SupportVersion => "any";
    public string Author => "Built-in";
    public string Url => "";
    public string License => "MIT License";
    public string Note => "选协议预设后填写对应密钥即可";

    public bool Available => true;

    public event EventHandler<SpeechEventArgs>? TextChanged;
    public event EventHandler<SpeechEventArgs>? SentenceDone;
    public event EventHandler<Exception>? ExceptionOccured;

    private StreamingAsrConfig _config = new();
    private StreamingAsrEngine? _engine;
    private volatile bool _running;

    public IPluginConfigEditor CreateConfigEditor() => new StreamingAsrConfigEditor();

    public void LoadConfig(string config)
    {
        if (string.IsNullOrEmpty(config)) return;
        try
        {
            _config = JsonSerializer.Deserialize<StreamingAsrConfig>(config) ?? new StreamingAsrConfig();
        }
        catch
        {
            _config = new StreamingAsrConfig();
        }
    }

    public void Init()
    {
    }

    public void Destroy() => Stop();

    public void Start()
    {
        if (_running) throw new InvalidOperationException("通用流式识别器：已在运行中");

        var profile = ResolveProfile();
        if (profile == null)
            throw new InvalidOperationException("通用流式识别器：协议预设无效或自定义 JSON 解析失败");

        var vars = BuildVars(profile);
        ValidateVars(profile, vars);

        _engine = new StreamingAsrEngine(profile, vars);
        _engine.OnText += OnEngineText;
        _engine.OnError += OnEngineError;

        _running = true;
        _engine.Start();
    }

    private StreamingAsrProfile? ResolveProfile()
    {
        if (_config.Preset == ProfilePresets.Custom)
        {
            if (string.IsNullOrWhiteSpace(_config.CustomProfileJson)) return null;
            try
            {
                return JsonSerializer.Deserialize<StreamingAsrProfile>(_config.CustomProfileJson);
            }
            catch
            {
                return null;
            }
        }

        return ProfilePresets.Get(_config.Preset);
    }

    private Dictionary<string, string> BuildVars(StreamingAsrProfile profile)
    {
        var vars = new Dictionary<string, string>
        {
            ["api_key"] = _config.ApiKey.Trim(),
            ["model"] = string.IsNullOrWhiteSpace(_config.Model) ? profile.DefaultModel : _config.Model.Trim(),
            ["region"] = string.IsNullOrWhiteSpace(_config.Region) ? profile.DefaultRegion : _config.Region.Trim(),
            ["app_key"] = _config.AppKey.Trim(),
            ["access_key_id"] = _config.AccessKeyId.Trim(),
            ["access_key_secret"] = _config.AccessKeySecret.Trim(),
            ["sample_rate"] = "16000"
        };
        return vars;
    }

    private static void ValidateVars(StreamingAsrProfile profile, IReadOnlyDictionary<string, string> vars)
    {
        if (profile.Auth.Provider == "apiKeyHeader" && string.IsNullOrEmpty(vars["api_key"]))
            throw new InvalidOperationException("通用流式识别器：未配置 API Key");
        if (profile.Auth.Provider == "nlsToken" &&
            (string.IsNullOrEmpty(vars["access_key_id"]) || string.IsNullOrEmpty(vars["access_key_secret"])))
            throw new InvalidOperationException("通用流式识别器：NLS 需要 AccessKeyId / AccessKeySecret");
        if (profile.NeedsAccessKey && string.IsNullOrEmpty(vars["app_key"]))
            throw new InvalidOperationException("通用流式识别器：NLS 需要 AppKey");
    }

    private void OnEngineText(string text, bool isFinal)
    {
        var info = new TextInfo(text);
        if (isFinal)
            SentenceDone?.Invoke(this, new SpeechEventArgs { Text = info });
        else
            TextChanged?.Invoke(this, new SpeechEventArgs { Text = info });
    }

    private void OnEngineError(Exception ex)
    {
        if (_running) ExceptionOccured?.Invoke(this, ex);
    }

    public void Feed(byte[] data)
    {
        if (!_running || _engine == null) return;
        _engine.FeedPcm(FloatBytesToPcm16(data));
    }

    private static byte[] FloatBytesToPcm16(byte[] data)
    {
        var floats = MemoryMarshal.Cast<byte, float>(data);
        var pcm = new byte[floats.Length * 2];
        for (int i = 0; i < floats.Length; i++)
        {
            short s = (short)Math.Clamp(floats[i] * 32767f, -32768f, 32767f);
            pcm[i * 2] = (byte)(s & 0xff);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }

        return pcm;
    }

    public void Stop()
    {
        _running = false;
        if (_engine != null)
        {
            _engine.OnText -= OnEngineText;
            _engine.OnError -= OnEngineError;
            _engine.Stop();
            _engine = null;
        }
    }
}
