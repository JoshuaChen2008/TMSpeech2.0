using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TMSpeech.Recognizer.StreamingAsr;

/// <summary>
/// 协议内核（厂商无关）。按 Profile + 变量字典完成：连接 → 鉴权 → 发开始指令 → 等回执
/// → 推音频（二进制 / base64-JSON）→ 解析结果 → 收尾。
/// 上层只需提供 Profile、vars 和 16-bit PCM 帧。
/// </summary>
public class StreamingAsrEngine
{
    /// <summary>(text, isFinal)。</summary>
    public event Action<string, bool>? OnText;

    public event Action<Exception>? OnError;

    private readonly StreamingAsrProfile _profile;
    private readonly Dictionary<string, string> _vars;

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private Task? _receiver;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private BlockingCollection<byte[]>? _sendQueue;
    private TaskCompletionSource<bool>? _startedTcs;

    private volatile bool _running;
    private int _failureSignaled;
    private const int MaxQueuedChunks = 200;

    internal Task WorkerCompletion => _worker ?? Task.CompletedTask;

    public StreamingAsrEngine(StreamingAsrProfile profile, Dictionary<string, string> vars)
    {
        _profile = profile;
        _vars = vars;
    }

    // ---- 生命周期 ----

    public void Start()
    {
        if (_running) throw new InvalidOperationException("流式识别引擎：已在运行中");
        _running = true;
        _failureSignaled = 0;
        _cts = new CancellationTokenSource();
        _sendQueue = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        _startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_vars.ContainsKey("task_id")) _vars["task_id"] = Hex32();
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>喂入 16-bit PCM 字节（已由上层从 float 转换）。</summary>
    public void FeedPcm(byte[] pcm16)
    {
        if (!_running || _sendQueue == null || _sendQueue.IsAddingCompleted || pcm16.Length == 0) return;
        while (_sendQueue.Count >= MaxQueuedChunks && _sendQueue.TryTake(out _))
        {
        }

        try
        {
            _sendQueue.Add(pcm16);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            _ws = new ClientWebSocket();
            await ApplyAuthAsync(_ws, ct);

            var url = ResolveUrl();
            await _ws.ConnectAsync(new Uri(url), ct);

            _receiver = Task.Run(() => ReceiveLoopAsync(ct), ct);

            // 开始指令 + 等回执
            if (!string.IsNullOrEmpty(_profile.StartMessageTemplate))
                await SendTemplateAsync(_profile.StartMessageTemplate!, ct);

            if (!string.IsNullOrEmpty(_profile.StartAckEvent))
            {
                var ackTask = _startedTcs!.Task;
                var timeout = Task.Delay(TimeSpan.FromSeconds(10), ct);
                if (await Task.WhenAny(ackTask, timeout) == timeout)
                    throw new InvalidOperationException($"流式识别引擎：等待 {_profile.StartAckEvent} 超时");
            }

            // 推音频
            var base64Mode = string.Equals(_profile.Audio.Mode, "base64json", StringComparison.OrdinalIgnoreCase);
            foreach (var pcm in _sendQueue!.GetConsumingEnumerable(ct))
            {
                if (_ws.State != WebSocketState.Open) break;
                if (base64Mode)
                {
                    _vars["message_id"] = Hex32();
                    _vars["audio_base64"] = Convert.ToBase64String(pcm);
                    await SendTextAsync(TemplateRenderer.Render(_profile.Audio.MessageTemplate, _vars), ct);
                }
                else
                {
                    await SendBinaryAsync(pcm, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SignalFailure(ex);
        }
    }

    private async Task ApplyAuthAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var a = _profile.Auth;
        switch (a.Provider)
        {
            case "apiKeyHeader":
                var apiKey = _vars.TryGetValue("api_key", out var k) ? k : "";
                var headerName = string.IsNullOrEmpty(a.HeaderName) ? "Authorization" : a.HeaderName!;
                var value = string.IsNullOrEmpty(a.Scheme) ? apiKey : a.Scheme + " " + apiKey;
                ws.Options.SetRequestHeader(headerName, value);
                break;

            case "nlsToken":
                var token = await AliyunSign.GetTokenAsync(
                    _vars.GetValueOrDefault("access_key_id", ""),
                    _vars.GetValueOrDefault("access_key_secret", ""),
                    _vars.GetValueOrDefault("region", "cn-shanghai"), ct);
                ws.Options.SetRequestHeader(string.IsNullOrEmpty(a.HeaderName) ? "X-NLS-Token" : a.HeaderName!, token);
                break;

            case "none":
            default:
                break;
        }

        if (a.ExtraHeaders != null)
            foreach (var h in a.ExtraHeaders)
                ws.Options.SetRequestHeader(h.Key, h.Value);
    }

    private string ResolveUrl()
    {
        var region = _vars.GetValueOrDefault("region", _profile.DefaultRegion);
        if (_profile.RegionUrls.Count > 0)
        {
            if (!string.IsNullOrEmpty(region) && _profile.RegionUrls.TryGetValue(region, out var u))
                return TemplateRenderer.Render(u, _vars);
            // 区域未命中：用第一个
            return TemplateRenderer.Render(_profile.RegionUrls.Values.First(), _vars);
        }

        return TemplateRenderer.Render(_profile.UrlTemplate ?? "", _vars);
    }

    // ---- 接收 ----

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
                    {
                        SignalFailure(new WebSocketException(
                            $"流式识别连接被服务端关闭：{result.CloseStatus} {result.CloseStatusDescription}"));
                        return;
                    }
                    sb.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;
                HandleMessage(Encoding.UTF8.GetString(sb.GetBuffer(), 0, (int)sb.Length));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SignalFailure(ex);
        }
    }

    private void HandleMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var ev = JsonPathResolver.GetString(root, _profile.Result.EventPath);

            // 错误
            if (!string.IsNullOrEmpty(_profile.Error.EventValue) && ev == _profile.Error.EventValue)
            {
                if (_running)
                {
                    var m = string.IsNullOrEmpty(_profile.Error.MessagePath)
                        ? "任务失败"
                        : JsonPathResolver.GetString(root, _profile.Error.MessagePath!);
                    SignalFailure(new InvalidOperationException($"流式识别：任务失败：{m}"));
                }

                return;
            }

            // 开始回执
            if (!string.IsNullOrEmpty(_profile.StartAckEvent) && ev == _profile.StartAckEvent)
            {
                _startedTcs?.TrySetResult(true);
                return;
            }

            // 结果
            var r = _profile.Result;
            var isResultEvent = r.PartialEvents.Contains(ev) || r.FinalEvents.Contains(ev);
            if (!isResultEvent) return;

            var text = JsonPathResolver.GetString(root, r.TextPath);
            if (string.IsNullOrEmpty(text)) return;

            bool isFinal = r.FinalFlagPath != null
                ? JsonPathResolver.GetBool(root, r.FinalFlagPath)
                : r.FinalEvents.Contains(ev);

            OnText?.Invoke(text, isFinal);
        }
        catch
        {
            // 单条消息解析失败不影响整体
        }
    }

    private void SignalFailure(Exception ex)
    {
        if (!_running || Interlocked.Exchange(ref _failureSignaled, 1) != 0) return;

        try { _sendQueue?.CompleteAdding(); } catch (InvalidOperationException) { }
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        OnError?.Invoke(ex);
    }

    // ---- 发送 ----

    private async Task SendTemplateAsync(string template, CancellationToken ct)
    {
        _vars["message_id"] = Hex32();
        await SendTextAsync(TemplateRenderer.Render(template, _vars), ct);
    }

    private async Task SendTextAsync(string text, CancellationToken ct)
    {
        if (_ws == null) return;
        var bytes = Encoding.UTF8.GetBytes(text);
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

    private async Task SendBinaryAsync(byte[] data, CancellationToken ct)
    {
        if (_ws == null) return;
        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(new ReadOnlyMemory<byte>(data), WebSocketMessageType.Binary, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Stop()
    {
        if (!_running && _ws == null) return;
        _running = false;

        try { _sendQueue?.CompleteAdding(); } catch { }

        try
        {
            if (_ws is { State: WebSocketState.Open } && !string.IsNullOrEmpty(_profile.StopMessageTemplate))
            {
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                SendTemplateAsync(_profile.StopMessageTemplate!, stopCts.Token).Wait(stopCts.Token);
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

    private static string Hex32() => Guid.NewGuid().ToString("N");
}
