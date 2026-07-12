using System.Runtime.InteropServices;
using System.Text.Json;
using TMSpeech.Core.Plugins;
using TMSpeech.Recognizer.StreamingAsr;

namespace TMSpeech.Recognizer.AliyunCloud;

public sealed class AliyunCloudRecognizer : IRecognizer
{
    public string GUID => "B7F3B2A1-3C2D-4E5F-9A8B-1C2D3E4F5A6B";
    public string Name => "语音识别阿里云";
    public string Description => "接入阿里云智能语音交互（NLS）实时语音识别 API";
    public string Version => "1.0.0";
    public string SupportVersion => "any";
    public string Author => "Built-in";
    public string Url => "https://help.aliyun.com/product/30413.html";
    public string License => "MIT License";
    public string Note => "需开通智能语音交互并填入 Appkey 与 AccessKey";
    public bool Available => true;

    public event EventHandler<SpeechEventArgs>? TextChanged;
    public event EventHandler<SpeechEventArgs>? SentenceDone;
    public event EventHandler<Exception>? ExceptionOccured;

    private AliyunCloudConfig _config = new();
    private StreamingAsrEngine? _engine;

    public IPluginConfigEditor CreateConfigEditor() => new AliyunCloudConfigEditor();
    public void Init() { }
    public void Destroy() => Stop();

    public void LoadConfig(string config)
    {
        if (string.IsNullOrEmpty(config)) return;
        try { _config = JsonSerializer.Deserialize<AliyunCloudConfig>(config) ?? new(); }
        catch { _config = new(); }
    }

    public void Feed(byte[] data)
    {
        var pcm = FloatBytesToPcm16(data);
        if (pcm.Length > 0) _engine?.FeedPcm(pcm);
    }

    public void Start()
    {
        if (_engine != null) throw new InvalidOperationException("阿里云识别器：已在运行中");
        if (string.IsNullOrWhiteSpace(_config.AppKey)) throw new InvalidOperationException("阿里云识别器：未配置 Appkey");
        if (string.IsNullOrWhiteSpace(_config.Token) &&
            (string.IsNullOrWhiteSpace(_config.AccessKeyId) || string.IsNullOrWhiteSpace(_config.AccessKeySecret)))
            throw new InvalidOperationException("阿里云识别器：未配置 AccessKey（或直接填写 Token）");

        var vars = new Dictionary<string, string>
        {
            ["app_key"] = _config.AppKey,
            ["api_key"] = _config.Token.Trim(),
            ["access_key_id"] = _config.AccessKeyId,
            ["access_key_secret"] = _config.AccessKeySecret,
            ["region"] = _config.Region
        };
        var engine = new StreamingAsrEngine(CreateProfile(_config), vars);
        engine.OnText += OnText;
        engine.OnError += OnError;
        _engine = engine;
        try { engine.Start(); }
        catch { Detach(engine); throw; }
    }

    public void Stop()
    {
        var engine = _engine;
        if (engine == null) return;
        try { engine.Stop(); }
        finally { Detach(engine); }
    }

    private void OnText(string text, bool final)
    {
        var args = new SpeechEventArgs { Text = new TextInfo(text) };
        if (final) SentenceDone?.Invoke(this, args); else TextChanged?.Invoke(this, args);
    }

    private void OnError(Exception ex) => ExceptionOccured?.Invoke(this, ex);
    private void Detach(StreamingAsrEngine engine)
    {
        engine.OnText -= OnText;
        engine.OnError -= OnError;
        if (ReferenceEquals(_engine, engine)) _engine = null;
    }

    private static StreamingAsrProfile CreateProfile(AliyunCloudConfig c) => new()
    {
        Name = "Aliyun NLS",
        UrlTemplate = c.Region == "ap-southeast-1" ? "wss://nls-gateway-ap-southeast-1.aliyuncs.com/ws/v1" : "wss://nls-gateway-cn-shanghai.aliyuncs.com/ws/v1",
        Auth = new AuthSpec { Provider = string.IsNullOrWhiteSpace(c.Token) ? "nlsToken" : "apiKeyHeader", HeaderName = "X-NLS-Token" },
        Audio = new AudioSpec { Mode = "binary" },
        StartMessageTemplate = "{\"header\":{\"message_id\":\"{message_id}\",\"task_id\":\"{task_id}\",\"namespace\":\"SpeechTranscriber\",\"name\":\"StartTranscription\",\"appkey\":\"{app_key}\"},\"payload\":{\"format\":\"pcm\",\"sample_rate\":16000,\"enable_intermediate_result\":true,\"enable_punctuation_prediction\":true,\"enable_inverse_text_normalization\":true}}",
        StopMessageTemplate = "{\"header\":{\"message_id\":\"{message_id}\",\"task_id\":\"{task_id}\",\"namespace\":\"SpeechTranscriber\",\"name\":\"StopTranscription\",\"appkey\":\"{app_key}\"}}",
        StartAckEvent = "TranscriptionStarted",
        Result = new ResultSpec { EventPath = "header.name", TextPath = "payload.result", PartialEvents = new[] { "TranscriptionResultChanged" }, FinalEvents = new[] { "SentenceEnd" } },
        Error = new ErrorSpec { EventValue = "TaskFailed", MessagePath = "header.status_text" }
    };

    private static byte[] FloatBytesToPcm16(byte[] data)
    {
        var input = MemoryMarshal.Cast<byte, float>(data);
        var pcm = new byte[input.Length * 2];
        for (var i = 0; i < input.Length; i++)
        {
            var sample = (short)Math.Clamp(input[i] * 32767f, -32768f, 32767f);
            pcm[i * 2] = (byte)sample;
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }
}
