using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TMSpeech.Core.Plugins;

namespace TMSpeech.Recognizer.LLMAudio;

/// <summary>
/// 阿里云百炼（DashScope）实时语音识别器（Fun-ASR / Paraformer 等）。
/// 通过 WebSocket 流式协议：连接后发 run-task，收到 task-started 后持续推送 16-bit PCM 音频，
/// 服务端自动断句并通过 result-generated 事件回传中间/最终文本。仅需一个 API Key。
/// 协议参考：阿里云百炼「实时语音识别」WebSocket 文档。
///
/// 静音自动挂起：服务端超过约 23 秒收不到音频会以 task-failed 断开任务（request timeout）。
/// 开启自动挂起后，本识别器在静音超过阈值时主动优雅结束会话进入挂起状态（不弹错误），
/// 再次检测到声音时自动重连恢复识别，唤醒前的音频通过预滚缓冲补发，不丢字。
/// </summary>
public class LLMAudioRecognizer : IRecognizer
{
    internal delegate Task<SessionResult> SessionRunner(CancellationToken ct, Action onSessionStarted);

    public string GUID => "C8E4D3B2-4A1F-4B6C-8D9E-2F3A4B5C6D7E";
    public string Name => "语音识别Fun-ASR（阿里云）";
    public string Description => "接入阿里云百炼 DashScope 实时语音识别（Fun-ASR / Paraformer）";
    public string Version => "1.1.0";
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

    private CancellationTokenSource? _cts;
    private Task? _worker;
    private BlockingCollection<byte[]>? _sendQueue;

    /// <summary>世代计数：Start/Stop 各自增一次。旧会话残留的异步回调据此丢弃，
    /// 避免"停止后立即重启"时旧会话的错误弹到新会话上。</summary>
    private long _generation;

    private volatile bool _running;
    private readonly SessionRunner? _sessionRunner;

    /// <summary>当前是否有活跃的 WebSocket 会话正在收音（false = 挂起等待声音）。</summary>
    private volatile bool _sessionActive;

    /// <summary>最近一次检测到有效声音的时间（UTC ticks，Interlocked 访问）。</summary>
    private long _lastVoiceTicks;

    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    /// <summary>挂起期间的预滚缓冲：保留唤醒前最近约 1.5 秒音频，重连后先补发。</summary>
    private readonly Queue<byte[]> _preRoll = new();
    private int _preRollBytes;
    private readonly object _preRollLock = new();

    private const int MaxQueuedChunks = 200;
    private const int MaxPreRollBytes = 48 * 1024; // 16kHz * 2B ≈ 1.5s
    private const int MaxConsecutiveFailures = 5;

    public LLMAudioRecognizer()
    {
    }

    internal LLMAudioRecognizer(SessionRunner sessionRunner)
    {
        _sessionRunner = sessionRunner;
    }

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
        var queue = _sendQueue;
        if (!_running || queue == null || queue.IsAddingCompleted) return;

        var (pcm, rms) = AudioFrameConverter.FloatBytesToPcm16(data);
        if (pcm.Length == 0) return;

        var voiced = rms >= VoiceThreshold();
        if (voiced)
        {
            Interlocked.Exchange(ref _lastVoiceTicks, DateTime.UtcNow.Ticks);
        }

        if (_sessionActive)
        {
            while (queue.Count >= MaxQueuedChunks && queue.TryTake(out _))
            {
            }

            try
            {
                queue.Add(pcm);
            }
            catch (InvalidOperationException)
            {
            }
        }
        else
        {
            // 挂起中：积累预滚缓冲，检测到声音则唤醒工作循环
            lock (_preRollLock)
            {
                _preRoll.Enqueue(pcm);
                _preRollBytes += pcm.Length;
                while (_preRollBytes > MaxPreRollBytes && _preRoll.Count > 1)
                {
                    _preRollBytes -= _preRoll.Dequeue().Length;
                }
            }

            if (voiced)
            {
                try
                {
                    _wakeSignal.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            }
        }
    }

    private float VoiceThreshold() => Math.Clamp(_config.VoiceThresholdPermille, 0, 1000) / 1000f;

    /// <summary>16kHz 32-bit float 字节流 → 16-bit PCM（小端）字节流，同时计算 RMS 能量。</summary>
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

        Interlocked.Increment(ref _generation);
        _running = true;
        _sessionActive = false;
        Interlocked.Exchange(ref _lastVoiceTicks, DateTime.UtcNow.Ticks);
        _cts = new CancellationTokenSource();
        _sendQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        lock (_preRollLock)
        {
            _preRoll.Clear();
            _preRollBytes = 0;
        }

        while (_wakeSignal.CurrentCount > 0) _wakeSignal.Wait(0);

        var gen = Interlocked.Read(ref _generation);
        _worker = Task.Run(() => WorkerLoopAsync(gen, _cts.Token));
    }

    /// <summary>
    /// 工作循环：负责会话的建立、静音挂起与声音唤醒、失败重试。
    /// 开启自动挂起时启动即处于挂起状态，检测到声音才建立会话（避免无声时白白连接又被服务端超时断开）。
    /// </summary>
    private async Task WorkerLoopAsync(long gen, CancellationToken ct)
    {
        var retryState = new SessionRetryState();

        try
        {
            while (_running && !ct.IsCancellationRequested)
            {
                if (_config.AutoSuspend)
                {
                    // 挂起：等待 Feed 检测到声音后唤醒
                    await _wakeSignal.WaitAsync(ct);
                    if (!_running || ct.IsCancellationRequested) break;
                }

                SessionResult result;
                string? failMessage = null;
                try
                {
                    result = _sessionRunner == null
                        ? await RunSessionAsync(ct, retryState.MarkSessionStarted)
                        : await _sessionRunner(ct, retryState.MarkSessionStarted);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    result = SessionResult.Failed;
                    failMessage = ex.Message;
                }
                finally
                {
                    _sessionActive = false;
                }

                switch (result)
                {
                    case SessionResult.SuspendedBySilence:
                        continue;

                    case SessionResult.Completed:
                        continue;

                    case SessionResult.Failed:
                        var consecutiveFailures = retryState.RecordFailure();
                        if (!_config.AutoSuspend)
                        {
                            // 未开启自动挂起：保持旧行为，报错并结束
                            RaiseException(gen, new InvalidOperationException(
                                $"Fun-ASR 识别器：任务失败：{failMessage ?? "未知错误"}"));
                            return;
                        }

                        if (retryState.RetryLimitReached(MaxConsecutiveFailures))
                        {
                            // 从未成功建立会话（多半是 Key/网络配置问题）或连续失败过多：报错并停止重试
                            RaiseException(gen, new InvalidOperationException(
                                $"Fun-ASR 识别器：连接失败（已重试 {consecutiveFailures} 次）：{failMessage ?? "未知错误"}"));
                            return;
                        }

                        // 会话曾经正常，多半是网络抖动或服务端静音超时：退避后静默重试
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(1 << consecutiveFailures, 15)), ct);
                        continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            RaiseException(gen, ex);
        }
    }

    internal enum SessionResult
    {
        /// <summary>静音超时，已优雅结束会话进入挂起。</summary>
        SuspendedBySilence,

        /// <summary>被 Stop 取消等正常结束。</summary>
        Completed,

        /// <summary>会话异常（连接失败 / task-failed / 连接意外断开）。</summary>
        Failed
    }

    /// <summary>会话内的接收端状态。</summary>
    private sealed class SessionState
    {
        public readonly TaskCompletionSource<bool> Started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly TaskCompletionSource<bool> Finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile string? FailMessage;
    }

    /// <summary>运行一个完整的 WebSocket 识别会话，直到静音挂起、取消或失败。</summary>
    private async Task<SessionResult> RunSessionAsync(CancellationToken ct, Action onSessionStarted)
    {
        var gen = Interlocked.Read(ref _generation);
        var taskId = Guid.NewGuid().ToString("N");
        var session = new SessionState();
        var sendLock = new SemaphoreSlim(1, 1);

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "bearer " + _config.ApiKey.Trim());
        await ws.ConnectAsync(new Uri(GatewayUrl()), ct);

        var receiver = Task.Run(() => ReceiveLoopAsync(ws, session, gen, ct), CancellationToken.None);

        try
        {
            await SendJsonAsync(ws, sendLock, LLMAudioProtocol.BuildRunTaskMessage(_config, taskId), ct);

            var timeout = Task.Delay(TimeSpan.FromSeconds(10), ct);
            if (await Task.WhenAny(session.Started.Task, timeout) == timeout)
                throw new InvalidOperationException("等待 task-started 超时");
            if (session.FailMessage != null)
                throw new InvalidOperationException(session.FailMessage);
            if (!session.Started.Task.Result)
                throw new InvalidOperationException("连接在任务启动前被关闭");
            onSessionStarted();

            // 会话就绪：先补发挂起期间的预滚缓冲，再切换 Feed 直通队列
            lock (_preRollLock)
            {
                while (_preRoll.TryDequeue(out var chunk))
                {
                    try
                    {
                        _sendQueue?.Add(chunk);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                _preRollBytes = 0;
                _sessionActive = true;
            }

            var suspendAfter = TimeSpan.FromSeconds(Math.Max(3, _config.SuspendAfterSeconds));

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (session.FailMessage != null)
                    throw new InvalidOperationException(session.FailMessage);
                if (ws.State != WebSocketState.Open)
                    throw new InvalidOperationException("连接已断开");

                if (_sendQueue!.TryTake(out var pcm, 200, ct))
                {
                    await sendLock.WaitAsync(ct);
                    try
                    {
                        await ws.SendAsync(new ReadOnlyMemory<byte>(pcm), WebSocketMessageType.Binary, true, ct);
                    }
                    finally
                    {
                        sendLock.Release();
                    }
                }

                if (_config.AutoSuspend)
                {
                    var lastVoice = new DateTime(Interlocked.Read(ref _lastVoiceTicks), DateTimeKind.Utc);
                    if (DateTime.UtcNow - lastVoice > suspendAfter)
                    {
                        _sessionActive = false;
                        await GracefulFinishAsync(ws, sendLock, session, taskId, ct);
                        return SessionResult.SuspendedBySilence;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _sessionActive = false;
            await TryCloseAsync(ws, sendLock, session, taskId);
            return SessionResult.Completed;
        }
        finally
        {
            _sessionActive = false;
            try
            {
                await Task.WhenAny(receiver, Task.Delay(2000, CancellationToken.None));
            }
            catch
            {
            }
        }
    }

    /// <summary>静音挂起：发送 finish-task 让服务端结算最后的句子，等待 task-finished 后关闭连接。</summary>
    private async Task GracefulFinishAsync(ClientWebSocket ws, SemaphoreSlim sendLock, SessionState session,
        string taskId, CancellationToken ct)
    {
        try
        {
            using var finishCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            finishCts.CancelAfter(TimeSpan.FromSeconds(3));
            await SendJsonAsync(ws, sendLock, LLMAudioProtocol.BuildFinishTaskMessage(taskId), finishCts.Token);
            await Task.WhenAny(session.Finished.Task, Task.Delay(3000, finishCts.Token));
        }
        catch
        {
        }

        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "silence suspend", closeCts.Token);
            }
        }
        catch
        {
        }
    }

    /// <summary>Stop/取消时尽力优雅关闭会话。</summary>
    private async Task TryCloseAsync(ClientWebSocket ws, SemaphoreSlim sendLock, SessionState session, string taskId)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendJsonAsync(ws, sendLock, LLMAudioProtocol.BuildFinishTaskMessage(taskId), cts.Token);
                await Task.WhenAny(session.Finished.Task, Task.Delay(1500, cts.Token));
            }
        }
        catch
        {
        }

        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "stop", cts.Token);
            }
        }
        catch
        {
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, SessionState session, long gen, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var sb = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                sb.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    sb.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;

                var json = Encoding.UTF8.GetString(sb.GetBuffer(), 0, (int)sb.Length);
                HandleMessage(json, session, gen);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // 连接层异常交给会话循环统一判定（挂起重连或报错）
            session.FailMessage ??= ex.Message;
        }
        finally
        {
            session.Started.TrySetResult(false);
            session.Finished.TrySetResult(false);
        }
    }

    private void HandleMessage(string json, SessionState session, long gen)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("header", out var header)) return;
            var ev = header.TryGetProperty("event", out var e) ? e.GetString() : null;

            switch (ev)
            {
                case "task-started":
                    session.Started.TrySetResult(true);
                    break;

                case "result-generated":
                    HandleResult(doc.RootElement, gen);
                    break;

                case "task-failed":
                    var msg = header.TryGetProperty("error_message", out var em) ? em.GetString() : "未知错误";
                    session.FailMessage = msg ?? "未知错误";
                    session.Started.TrySetResult(false);
                    session.Finished.TrySetResult(false);
                    break;

                case "task-finished":
                    session.Finished.TrySetResult(true);
                    break;
            }
        }
        catch
        {
            // 单条消息解析失败不影响整体
        }
    }

    /// <summary>解析 result-generated：payload.output.sentence.{text, sentence_end}。</summary>
    private void HandleResult(JsonElement root, long gen)
    {
        // 旧世代（已被 Stop/重启淘汰）的结果直接丢弃
        if (!_running || gen != Interlocked.Read(ref _generation)) return;

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

    /// <summary>只有当前世代且仍在运行时才对外抛异常，避免旧会话的错误在重启后弹窗。</summary>
    private void RaiseException(long gen, Exception ex)
    {
        if (_running && gen == Interlocked.Read(ref _generation))
        {
            ExceptionOccured?.Invoke(this, ex);
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, SemaphoreSlim sendLock, string json,
        CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(new ReadOnlyMemory<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            sendLock.Release();
        }
    }

    public void Stop()
    {
        if (!_running && _worker == null) return;
        _running = false;
        Interlocked.Increment(ref _generation);
        _sessionActive = false;

        try
        {
            _sendQueue?.CompleteAdding();
        }
        catch
        {
        }

        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        // 唤醒可能正在等声音的工作循环，让它感知取消
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }

        try
        {
            _worker?.Wait(5000);
        }
        catch
        {
        }

        _cts?.Dispose();
        _cts = null;
        _worker = null;
        _sendQueue?.Dispose();
        _sendQueue = null;
        lock (_preRollLock)
        {
            _preRoll.Clear();
            _preRollBytes = 0;
        }
    }
}
