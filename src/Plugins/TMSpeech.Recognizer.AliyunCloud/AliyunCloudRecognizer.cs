using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TMSpeech.Core.Plugins;

namespace TMSpeech.Recognizer.AliyunCloud;

/// <summary>
/// 阿里云智能语音交互（NLS）实时识别器。
/// 通过 WebSocket 连接 NLS 网关，使用 SpeechTranscriber 进行流式识别。
/// 音频从 <see cref="Feed"/> 进入（16kHz / 单声道 / 32-bit float），转成 16-bit PCM 后推送。
/// </summary>
public class AliyunCloudRecognizer : IRecognizer
{
    public string GUID => "B7F3B2A1-3C2D-4E5F-9A8B-1C2D3E4F5A6B";
    public string Name => "语音识别阿里云";
    public string Description => "接入阿里云智能语音交互（NLS）实时语音识别 API";
    public string Version => "1.0.0";
    public string SupportVersion => "any";
    public string Author => "Built-in";
    public string Url => "https://help.aliyun.com/product/30413.html";
    public string License => "MIT License";
    public string Note => "需在阿里云控制台开通智能语音交互并创建项目，填入 Appkey 与 AccessKey";

    public bool Available => true;

    public event EventHandler<SpeechEventArgs>? TextChanged;
    public event EventHandler<SpeechEventArgs>? SentenceDone;
    public event EventHandler<Exception>? ExceptionOccured;

    private AliyunCloudConfig _config = new();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private Task? _receiver;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private BlockingCollection<byte[]>? _sendQueue;
    private TaskCompletionSource<bool>? _startedTcs;

    private volatile bool _running;
    private volatile bool _started;
    private string _taskId = "";

    private const int MaxQueuedChunks = 200; // 背压上限，超过则丢最旧的帧

    public IPluginConfigEditor CreateConfigEditor() => new AliyunCloudConfigEditor();

    public void LoadConfig(string config)
    {
        if (string.IsNullOrEmpty(config)) return;
        try
        {
            _config = JsonSerializer.Deserialize<AliyunCloudConfig>(config) ?? new AliyunCloudConfig();
        }
        catch
        {
            _config = new AliyunCloudConfig();
        }
    }

    public void Init()
    {
    }

    public void Destroy() => Stop();

    // ---- 音频输入 -------------------------------------------------------

    public void Feed(byte[] data)
    {
        if (!_running || _sendQueue == null || _sendQueue.IsAddingCompleted) return;

        var pcm = FloatBytesToPcm16(data);
        if (pcm.Length == 0) return;

        // 简单背压：队列过长时丢弃最旧帧，避免内存膨胀
        while (_sendQueue.Count >= MaxQueuedChunks && _sendQueue.TryTake(out _))
        {
        }

        try
        {
            _sendQueue.Add(pcm);
        }
        catch (InvalidOperationException)
        {
            // 队列已完成添加，忽略
        }
    }

    /// <summary>16kHz 32-bit float 字节流 → 16-bit PCM（小端）字节流。</summary>
    private static byte[] FloatBytesToPcm16(byte[] data)
    {
        var floats = MemoryMarshal.Cast<byte, float>(data);
        var pcm = new byte[floats.Length * 2];
        for (int i = 0; i < floats.Length; i++)
        {
            var clamped = Math.Clamp(floats[i] * 32767f, -32768f, 32767f);
            short s = (short)clamped;
            pcm[i * 2] = (byte)(s & 0xff);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }

        return pcm;
    }

    // ---- 生命周期 -------------------------------------------------------

    public void Start()
    {
        if (_running) throw new InvalidOperationException("阿里云识别器：已在运行中");

        if (string.IsNullOrWhiteSpace(_config.AppKey))
            throw new InvalidOperationException("阿里云识别器：未配置 Appkey");
        if (string.IsNullOrWhiteSpace(_config.Token) &&
            (string.IsNullOrWhiteSpace(_config.AccessKeyId) || string.IsNullOrWhiteSpace(_config.AccessKeySecret)))
            throw new InvalidOperationException("阿里云识别器：未配置 AccessKey（或直接填写 Token）");

        _running = true;
        _started = false;
        _taskId = Guid.NewGuid().ToString("N");
        _cts = new CancellationTokenSource();
        _sendQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        _startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // 1. 取 Token
            var token = !string.IsNullOrWhiteSpace(_config.Token)
                ? _config.Token.Trim()
                : await AliyunTokenClient.GetTokenAsync(_config.AccessKeyId, _config.AccessKeySecret,
                    _config.Region, ct);

            // 2. 连接 WebSocket
            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("X-NLS-Token", token);
            var gatewayRegion = _config.Region == "ap-southeast-1" ? "ap-southeast-1" : "cn-shanghai";
            var url = $"wss://nls-gateway-{gatewayRegion}.aliyuncs.com/ws/v1";
            await _ws.ConnectAsync(new Uri(url), ct);

            // 3. 接收循环
            _receiver = Task.Run(() => ReceiveLoopAsync(ct), ct);

            // 4. 发送 StartTranscription，等待 TranscriptionStarted
            await SendJsonAsync(BuildStartMessage(), ct);
            var startedTask = _startedTcs!.Task;
            var timeout = Task.Delay(TimeSpan.FromSeconds(10), ct);
            if (await Task.WhenAny(startedTask, timeout) == timeout)
                throw new InvalidOperationException("阿里云识别器：等待 TranscriptionStarted 超时");

            // 5. 发送音频
            foreach (var pcm in _sendQueue!.GetConsumingEnumerable(ct))
            {
                await _sendLock.WaitAsync(ct);
                try
                {
                    if (_ws.State != WebSocketState.Open) break;
                    await _ws.SendAsync(new ReadOnlyMemory<byte>(pcm),
                        WebSocketMessageType.Binary, true, ct);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            if (_running) ExceptionOccured?.Invoke(this, ex);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var sb = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
            {
                sb.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    sb.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;

                var json = Encoding.UTF8.GetString(sb.GetBuffer(), 0, (int)sb.Length);
                HandleMessage(json);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_running) ExceptionOccured?.Invoke(this, ex);
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("header", out var header)) return;
            var name = header.TryGetProperty("name", out var n) ? n.GetString() : null;

            switch (name)
            {
                case "TranscriptionStarted":
                    _started = true;
                    _startedTcs?.TrySetResult(true);
                    break;

                case "TranscriptionResultChanged":
                    if (TryGetResult(doc.RootElement, out var partial))
                        TextChanged?.Invoke(this, new SpeechEventArgs { Text = new TextInfo(partial) });
                    break;

                case "SentenceEnd":
                    if (TryGetResult(doc.RootElement, out var final))
                        SentenceDone?.Invoke(this, new SpeechEventArgs { Text = new TextInfo(final) });
                    break;

                case "TaskFailed":
                    var status = header.TryGetProperty("status_text", out var st) ? st.GetString() : "未知错误";
                    if (_running)
                        ExceptionOccured?.Invoke(this,
                            new InvalidOperationException($"阿里云识别器：任务失败：{status}"));
                    break;

                case "TranscriptionCompleted":
                    // 会话正常结束
                    break;
            }
        }
        catch
        {
            // 单条消息解析失败不影响整体
        }
    }

    private static bool TryGetResult(JsonElement root, out string text)
    {
        text = "";
        if (root.TryGetProperty("payload", out var payload) &&
            payload.TryGetProperty("result", out var result))
        {
            text = result.GetString() ?? "";
            return !string.IsNullOrEmpty(text);
        }

        return false;
    }

    private async Task SendJsonAsync(string json, CancellationToken ct)
    {
        if (_ws == null) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(new ReadOnlyMemory<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private string BuildStartMessage()
    {
        var msg = new Dictionary<string, object>
        {
            ["header"] = new Dictionary<string, object>
            {
                ["message_id"] = Guid.NewGuid().ToString("N"),
                ["task_id"] = _taskId,
                ["namespace"] = "SpeechTranscriber",
                ["name"] = "StartTranscription",
                ["appkey"] = _config.AppKey
            },
            ["payload"] = new Dictionary<string, object>
            {
                ["format"] = "pcm",
                ["sample_rate"] = 16000,
                ["enable_intermediate_result"] = true,
                ["enable_punctuation_prediction"] = true,
                ["enable_inverse_text_normalization"] = true
            }
        };
        return JsonSerializer.Serialize(msg);
    }

    private string BuildStopMessage()
    {
        var msg = new Dictionary<string, object>
        {
            ["header"] = new Dictionary<string, object>
            {
                ["message_id"] = Guid.NewGuid().ToString("N"),
                ["task_id"] = _taskId,
                ["namespace"] = "SpeechTranscriber",
                ["name"] = "StopTranscription",
                ["appkey"] = _config.AppKey
            }
        };
        return JsonSerializer.Serialize(msg);
    }

    public void Stop()
    {
        if (!_running && _ws == null) return;
        _running = false;
        _started = false;

        // 停止接收音频
        try { _sendQueue?.CompleteAdding(); } catch { }

        // 尽力发送 StopTranscription（独立超时，不依赖已取消的 token）
        try
        {
            if (_ws is { State: WebSocketState.Open })
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                SendJsonAsync(BuildStopMessage(), stopCts.Token).Wait(stopCts.Token);
            }
        }
        catch
        {
        }

        try { _cts?.Cancel(); } catch { }

        // 关闭并回收
        try
        {
            if (_ws is { State: WebSocketState.Open or WebSocketState.CloseReceived })
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", closeCts.Token).Wait(closeCts.Token);
            }
        }
        catch
        {
        }

        try { _worker?.Wait(2000); } catch { }
        try { _receiver?.Wait(2000); } catch { }

        _ws?.Dispose();
        _ws = null;
        _cts?.Dispose();
        _cts = null;
        _sendQueue?.Dispose();
        _sendQueue = null;
        _startedTcs = null;
    }
}
