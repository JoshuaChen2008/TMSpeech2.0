using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TMSpeech.Core.Plugins;

namespace TMSpeech.Recognizer.LLMAudio;

/// <summary>
/// 阿里云百炼（DashScope）实时语音识别器（Fun-ASR / Paraformer 等）。
/// 通过 WebSocket 流式协议：连接后发 run-task，收到 task-started 后持续推送 16-bit PCM 音频，
/// 服务端自动断句并通过 result-generated 事件回传中间/最终文本。仅需一个 API Key。
/// 协议参考：阿里云百炼「实时语音识别」WebSocket 文档。
/// </summary>
public class LLMAudioRecognizer : IRecognizer
{
    public string GUID => "C8E4D3B2-4A1F-4B6C-8D9E-2F3A4B5C6D7E";
    public string Name => "语音识别Fun-ASR（阿里云）";
    public string Description => "接入阿里云百炼 DashScope 实时语音识别（Fun-ASR / Paraformer）";
    public string Version => "1.0.0";
    public string SupportVersion => "any";
    public string Author => "Built-in";
    public string Url => "https://help.aliyun.com/zh/model-studio/realtime-speech-recognition";
    public string License => "MIT License";
    public string Note => "需要百炼 API Key；模型默认 fun-asr-realtime，可改 paraformer-realtime-v2 等";

    public bool Available => true;

    public event EventHandler<SpeechEventArgs>? TextChanged;
    public event EventHandler<SpeechEventArgs>? SentenceDone;
    public event EventHandler<Exception>? ExceptionOccured;

    private LLMAudioConfig _config = new();

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

    private const int MaxQueuedChunks = 200;

    public IPluginConfigEditor CreateConfigEditor() => new LLMAudioConfigEditor();

    public void LoadConfig(string config)
    {
        if (string.IsNullOrEmpty(config)) return;
        try
        {
            _config = JsonSerializer.Deserialize<LLMAudioConfig>(config) ?? new LLMAudioConfig();
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

    // ---- 音频输入 -------------------------------------------------------

    public void Feed(byte[] data)
    {
        if (!_running || _sendQueue == null || _sendQueue.IsAddingCompleted) return;

        var pcm = FloatBytesToPcm16(data);
        if (pcm.Length == 0) return;

        while (_sendQueue.Count >= MaxQueuedChunks && _sendQueue.TryTake(out _))
        {
        }

        try
        {
            _sendQueue.Add(pcm);
        }
        catch (InvalidOperationException)
        {
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

    private string GatewayUrl() => _config.Region == "singapore"
        ? "wss://dashscope-intl.aliyuncs.com/api-ws/v1/inference/"
        : "wss://dashscope.aliyuncs.com/api-ws/v1/inference/";

    // ---- 生命周期 -------------------------------------------------------

    public void Start()
    {
        if (_running) throw new InvalidOperationException("Fun-ASR 识别器：已在运行中");
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
            throw new InvalidOperationException("Fun-ASR 识别器：未配置 API Key");
        if (string.IsNullOrWhiteSpace(_config.Model))
            throw new InvalidOperationException("Fun-ASR 识别器：未配置模型名");

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
            // 1. 连接 WebSocket（鉴权仅需一个 API Key）
            _ws = new ClientWebSocket();
            _ws.Options.SetRequestHeader("Authorization", "bearer " + _config.ApiKey.Trim());
            await _ws.ConnectAsync(new Uri(GatewayUrl()), ct);

            // 2. 接收循环
            _receiver = Task.Run(() => ReceiveLoopAsync(ct), ct);

            // 3. 发送 run-task，等待 task-started
            await SendJsonAsync(BuildRunTaskMessage(), ct);
            var startedTask = _startedTcs!.Task;
            var timeout = Task.Delay(TimeSpan.FromSeconds(10), ct);
            if (await Task.WhenAny(startedTask, timeout) == timeout)
                throw new InvalidOperationException("Fun-ASR 识别器：等待 task-started 超时");

            // 4. 推送音频（二进制 PCM 帧）
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
                    if (result.MessageType == WebSocketMessageType.Close) return;
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
            var ev = header.TryGetProperty("event", out var e) ? e.GetString() : null;

            switch (ev)
            {
                case "task-started":
                    _started = true;
                    _startedTcs?.TrySetResult(true);
                    break;

                case "result-generated":
                    HandleResult(doc.RootElement);
                    break;

                case "task-failed":
                    var msg = header.TryGetProperty("error_message", out var em) ? em.GetString() : "未知错误";
                    if (_running)
                        ExceptionOccured?.Invoke(this,
                            new InvalidOperationException($"Fun-ASR 识别器：任务失败：{msg}"));
                    break;

                case "task-finished":
                    break;
            }
        }
        catch
        {
            // 单条消息解析失败不影响整体
        }
    }

    /// <summary>解析 result-generated：payload.output.sentence.{text, sentence_end}。</summary>
    private void HandleResult(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload)) return;
        if (!payload.TryGetProperty("output", out var output)) return;
        if (!output.TryGetProperty("sentence", out var sentence)) return;
        if (!sentence.TryGetProperty("text", out var textEl)) return;

        var text = textEl.GetString() ?? "";
        if (string.IsNullOrEmpty(text)) return;

        var isEnd = sentence.TryGetProperty("sentence_end", out var se) &&
                    se.ValueKind == JsonValueKind.True;

        var info = new TextInfo(text);
        if (isEnd)
            SentenceDone?.Invoke(this, new SpeechEventArgs { Text = info });
        else
            TextChanged?.Invoke(this, new SpeechEventArgs { Text = info });
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

    private string BuildRunTaskMessage()
    {
        var parameters = new Dictionary<string, object>
        {
            ["format"] = "pcm",
            ["sample_rate"] = 16000
        };
        if (_config.MaxSentenceSilence > 0)
            parameters["max_sentence_silence"] = _config.MaxSentenceSilence;

        var msg = new Dictionary<string, object>
        {
            ["header"] = new Dictionary<string, object>
            {
                ["action"] = "run-task",
                ["task_id"] = _taskId,
                ["streaming"] = "duplex"
            },
            ["payload"] = new Dictionary<string, object>
            {
                ["task_group"] = "audio",
                ["task"] = "asr",
                ["function"] = "recognition",
                ["model"] = _config.Model,
                ["parameters"] = parameters,
                ["input"] = new Dictionary<string, object>()
            }
        };
        return JsonSerializer.Serialize(msg);
    }

    private string BuildFinishTaskMessage()
    {
        var msg = new Dictionary<string, object>
        {
            ["header"] = new Dictionary<string, object>
            {
                ["action"] = "finish-task",
                ["task_id"] = _taskId,
                ["streaming"] = "duplex"
            },
            ["payload"] = new Dictionary<string, object>
            {
                ["input"] = new Dictionary<string, object>()
            }
        };
        return JsonSerializer.Serialize(msg);
    }

    public void Stop()
    {
        if (!_running && _ws == null) return;
        _running = false;
        _started = false;

        try { _sendQueue?.CompleteAdding(); } catch { }

        // 尽力发送 finish-task
        try
        {
            if (_ws is { State: WebSocketState.Open })
            {
                using var finishCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                SendJsonAsync(BuildFinishTaskMessage(), finishCts.Token).Wait(finishCts.Token);
            }
        }
        catch
        {
        }

        try { _cts?.Cancel(); } catch { }

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
