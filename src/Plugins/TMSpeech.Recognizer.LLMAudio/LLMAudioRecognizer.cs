using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TMSpeech.Core.Plugins;

namespace TMSpeech.Recognizer.LLMAudio;

/// <summary>
/// 用多模态音频大模型（LLM）做语音识别（仅识别、不翻译）。
/// 音频从 <see cref="Feed"/> 进入（16kHz / 单声道 / 32-bit float），
/// 基于能量做简单端点检测切句，整段编码为 WAV 后发给 LLM，返回原文。
/// </summary>
public class LLMAudioRecognizer : IRecognizer
{
    public string GUID => "C8E4D3B2-4A1F-4B6C-8D9E-2F3A4B5C6D7E";
    public string Name => "语音识别大模型";
    public string Description => "用多模态音频大模型进行语音识别（仅识别，不翻译）";
    public string Version => "1.0.0";
    public string SupportVersion => "any";
    public string Author => "Built-in";
    public string Url => "";
    public string License => "MIT License";
    public string Note => "需要支持音频输入的多模态模型与 OpenAI 兼容接口";

    public bool Available => true;

    public event EventHandler<SpeechEventArgs>? TextChanged;
    public event EventHandler<SpeechEventArgs>? SentenceDone;
    public event EventHandler<Exception>? ExceptionOccured;

    private const int SampleRate = 16000;
    private const float SilenceThreshold = 0.01f; // RMS 静音阈值（float -1..1）
    private const int MinSpeechMs = 300; // 段内最少语音时长，过短不送识别

    private LLMAudioConfig _config = new();
    private LLMAudioApiClient? _client;

    private volatile bool _running;
    private readonly object _segLock = new();
    private readonly List<float> _segment = new();
    private bool _hasSpeech;
    private int _trailingSilenceSamples;

    private BlockingCollection<float[]>? _sendQueue;
    private Task? _worker;
    private CancellationTokenSource? _cts;

    public IPluginConfigEditor CreateConfigEditor() => new LLMAudioConfigEditor();

    public void LoadConfig(string config)
    {
        if (string.IsNullOrEmpty(config)) return;
        try
        {
            _config = System.Text.Json.JsonSerializer.Deserialize<LLMAudioConfig>(config) ?? new LLMAudioConfig();
        }
        catch
        {
            _config = new LLMAudioConfig();
        }
    }

    public void Init()
    {
    }

    public void Destroy() => Stop();

    public void Start()
    {
        if (_running) throw new InvalidOperationException("语音识别大模型：已在运行中");
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new InvalidOperationException("语音识别大模型：未配置 API Key");
        if (string.IsNullOrWhiteSpace(_config.Model))
            throw new InvalidOperationException("语音识别大模型：未配置模型名");

        _running = true;
        _hasSpeech = false;
        _trailingSilenceSamples = 0;
        lock (_segLock) _segment.Clear();

        _client = new LLMAudioApiClient(_config);
        _cts = new CancellationTokenSource();
        _sendQueue = new BlockingCollection<float[]>(new ConcurrentQueue<float[]>());
        _worker = Task.Run(() => SendLoopAsync(_cts.Token));
    }

    public void Feed(byte[] data)
    {
        if (!_running) return;

        var floats = MemoryMarshal.Cast<byte, float>(data);
        if (floats.Length == 0) return;

        var rms = Rms(floats);
        var voiced = rms > SilenceThreshold;
        var maxSamples = (long)_config.MaxSegmentMs * SampleRate / 1000;
        var silenceLimit = (int)((long)_config.SilenceMs * SampleRate / 1000);

        float[]? toFlush = null;
        lock (_segLock)
        {
            for (int i = 0; i < floats.Length; i++) _segment.Add(floats[i]);

            if (voiced)
            {
                _hasSpeech = true;
                _trailingSilenceSamples = 0;
            }
            else if (_hasSpeech)
            {
                _trailingSilenceSamples += floats.Length;
            }

            var endpoint = _hasSpeech && _trailingSilenceSamples >= silenceLimit;
            var tooLong = _segment.Count >= maxSamples;

            if ((endpoint || tooLong) && _hasSpeech)
            {
                toFlush = _segment.ToArray();
                _segment.Clear();
                _hasSpeech = false;
                _trailingSilenceSamples = 0;
            }
        }

        if (toFlush != null && toFlush.Length >= MinSpeechMs * SampleRate / 1000)
        {
            try { _sendQueue?.Add(toFlush); }
            catch (InvalidOperationException) { }
        }
    }

    private static float Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        for (int i = 0; i < samples.Length; i++) sum += (double)samples[i] * samples[i];
        return (float)Math.Sqrt(sum / Math.Max(1, samples.Length));
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            foreach (var segment in _sendQueue!.GetConsumingEnumerable(ct))
            {
                try
                {
                    var wav = WavWriter.FromFloat(segment, SampleRate);
                    var text = await _client!.TranscribeAsync(wav, ct);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var info = new TextInfo(text.Trim());
                        // 段落级：直接作为完整一句
                        TextChanged?.Invoke(this, new SpeechEventArgs { Text = info });
                        SentenceDone?.Invoke(this, new SpeechEventArgs { Text = info });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_running) ExceptionOccured?.Invoke(this, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Stop()
    {
        if (!_running && _worker == null) return;
        _running = false;

        try { _sendQueue?.CompleteAdding(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _worker?.Wait(2000); } catch { }

        _client?.Dispose();
        _client = null;
        _sendQueue?.Dispose();
        _sendQueue = null;
        _cts?.Dispose();
        _cts = null;
        lock (_segLock) _segment.Clear();
    }
}
