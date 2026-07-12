using TMSpeech.Core;
using TMSpeech.Core.Plugins;
using TMSpeech.Recognizer.LLMAudio;
using TMSpeech.Recognizer.StreamingAsr;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Loader;

try
{
    // This suite is an offline gate. Any protocol fixture must stay on loopback;
    // external endpoints make the result non-deterministic and are not allowed.
    if (args.Length != 1 || args[0] != "all")
    {
        Console.Error.WriteLine("Usage: TMSpeech.Core.RuntimeChecks all");
        return 2;
    }

var coordinator = new PluginFailureStopCoordinator();
var firstStopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var allowFirstStopToFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var stopCount = 0;

void Stop()
{
    Interlocked.Increment(ref stopCount);
    firstStopEntered.TrySetResult();
    allowFirstStopToFinish.Task.GetAwaiter().GetResult();
}

if (!coordinator.TrySchedule(1, Stop)) throw new InvalidOperationException("首次故障没有排队停止");
await firstStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
if (coordinator.TrySchedule(1, Stop)) throw new InvalidOperationException("同代重复故障排队了第二次停止");
if (Volatile.Read(ref stopCount) != 1) throw new InvalidOperationException("停止次数不是 1");

var newerGenerationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
if (!coordinator.TrySchedule(2, () =>
    {
        Interlocked.Increment(ref stopCount);
        newerGenerationFinished.SetResult();
    }))
    throw new InvalidOperationException("旧代停止等待时，新一代故障被错误吞掉");
await newerGenerationFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));

allowFirstStopToFinish.SetResult();
var secondStopFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var rearmed = false;
for (var i = 0; i < 50 && !rearmed; i++)
{
    rearmed = coordinator.TrySchedule(1, () =>
    {
        Interlocked.Increment(ref stopCount);
        secondStopFinished.SetResult();
    });
    if (!rearmed) await Task.Delay(10);
}
if (!rearmed)
    throw new InvalidOperationException("首次停止完成后协调器没有重新武装");

await secondStopFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
if (Volatile.Read(ref stopCount) != 3) throw new InvalidOperationException("重新武装后的停止没有执行");

Console.WriteLine("PASS plugin-failure-stop");

var lifecycle = new SessionLifecycle();
var firstGeneration = lifecycle.BeginStart();
if (!lifecycle.TryMarkRunning(firstGeneration))
    throw new InvalidOperationException("当前会话不能进入 Running");
lifecycle.BeginStop();
lifecycle.MarkStopped();
var secondGeneration = lifecycle.BeginStart();
if (lifecycle.IsCurrent(firstGeneration) || !lifecycle.IsCurrent(secondGeneration))
    throw new InvalidOperationException("旧会话仍然拥有生命周期");
if (lifecycle.TryMarkRunning(firstGeneration))
    throw new InvalidOperationException("旧会话错误地接管了新会话状态");
if (!lifecycle.TryMarkRunning(secondGeneration))
    throw new InvalidOperationException("新会话不能进入 Running");

Console.WriteLine("PASS single-session-lifecycle-owner");

var pluginContextType = typeof(PluginManager).Assembly.GetType("TMSpeech.Core.Plugins.PluginManagerImpl+PluginLoadContext")
    ?? throw new InvalidOperationException("找不到插件隔离加载上下文");
var aliyunAssemblyPath = Path.GetFullPath(
    "src/Plugins/TMSpeech.Recognizer.AliyunCloud/bin/Debug/net6.0/TMSpeech.Recognizer.AliyunCloud.dll");
var pluginContext = (AssemblyLoadContext?)Activator.CreateInstance(
    pluginContextType, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                       System.Reflection.BindingFlags.NonPublic, null, new object[] { aliyunAssemblyPath }, null)
    ?? throw new InvalidOperationException("不能创建插件隔离加载上下文");
var isolatedAliyun = pluginContext.LoadFromAssemblyPath(aliyunAssemblyPath);
if (!isolatedAliyun.GetTypes().Any(t => t.Name == "AliyunCloudRecognizer"))
    throw new InvalidOperationException("隔离上下文未加载阿里云识别器");

Console.WriteLine("PASS plugin-adjacent-managed-dependency-load");

var audio = new FakeAudioSource();
var recognizer = new FailingOnStartRecognizer();
var pluginManager = new FakePluginManager(audio, recognizer);
ConfigManagerFactory.Init(new Dictionary<string, object>
{
    [AudioSourceConfigTypes.AudioSource] = "audio",
    [AudioSourceConfigTypes.GetPluginConfigKey("audio")] = "",
    [RecognizerConfigTypes.Recognizer] = "recognizer",
    [RecognizerConfigTypes.GetPluginConfigKey("recognizer")] = "",
    [NotificationConfigTypes.SensitiveWords] = "",
    [GeneralConfigTypes.ResultLogPath] = ""
});
ConfigManagerFactory.Instance.UserDataDir = Path.Combine(Path.GetTempPath(), "TMSpeech.RuntimeChecks", Guid.NewGuid().ToString("N"));
ConfigManagerFactory.Instance.BatchApply(new Dictionary<string, object>
{
    [AudioSourceConfigTypes.AudioSource] = "audio",
    [AudioSourceConfigTypes.GetPluginConfigKey("audio")] = "",
    [RecognizerConfigTypes.Recognizer] = "recognizer",
    [RecognizerConfigTypes.GetPluginConfigKey("recognizer")] = "",
    [NotificationConfigTypes.SensitiveWords] = "",
    [GeneralConfigTypes.ResultLogPath] = ""
});

var job = new JobManagerImpl(pluginManager);
job.Start();
for (var i = 0; i < 100 && job.Status != JobStatus.Stopped; i++) await Task.Delay(10);

if (job.Status != JobStatus.Stopped) throw new InvalidOperationException("启动期间故障后任务没有回滚为 Stopped");
if (audio.StartCount != 1 || audio.StopCount != 1)
    throw new InvalidOperationException($"音频源生命周期错误: start={audio.StartCount}, stop={audio.StopCount}");
if (recognizer.StartCount != 1 || recognizer.StopCount != 1)
    throw new InvalidOperationException($"识别器生命周期错误: start={recognizer.StartCount}, stop={recognizer.StopCount}");

Console.WriteLine("PASS job-starting-failure-rolls-back");

var retryState = new SessionRetryState();
retryState.RecordFailure();
if (!retryState.RetryLimitReached(3))
    throw new InvalidOperationException("从未成功建立会话时应立即停止重试");

retryState.MarkSessionStarted();
if (retryState.RecordFailure() != 1 || retryState.RetryLimitReached(3))
    throw new InvalidOperationException("成功会话首次断线后应允许重试");
if (retryState.RecordFailure() != 2 || retryState.RetryLimitReached(3))
    throw new InvalidOperationException("成功会话第二次断线后应允许重试");
if (retryState.RecordFailure() != 3 || !retryState.RetryLimitReached(3))
    throw new InvalidOperationException("连续失败达到上限后应停止重试");

retryState.MarkSessionStarted();
if (retryState.ConsecutiveFailures != 0)
    throw new InvalidOperationException("新会话成功后没有清零连续失败次数");

Console.WriteLine("PASS llm-audio-retry-state");

var sessionCalls = 0;
var retrySessionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var runtimeRecognizer = new LLMAudioRecognizer(async (ct, onSessionStarted) =>
{
    var call = Interlocked.Increment(ref sessionCalls);
    onSessionStarted();
    if (call == 1) throw new IOException("simulated disconnect after task-started");
    retrySessionStarted.SetResult();
    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    return LLMAudioRecognizer.SessionResult.Completed;
});
runtimeRecognizer.LoadConfig(
    "{\"apiKey\":\"runtime-check\",\"model\":\"runtime-check\",\"autoSuspend\":true,\"suspendAfterSeconds\":12}");
runtimeRecognizer.Start();

var loudSamples = Enumerable.Repeat(0.5f, 320).ToArray();
var loudBytes = new byte[loudSamples.Length * sizeof(float)];
Buffer.BlockCopy(loudSamples, 0, loudBytes, 0, loudBytes.Length);
for (var i = 0; i < 100 && !retrySessionStarted.Task.IsCompleted; i++)
{
    runtimeRecognizer.Feed(loudBytes);
    await Task.Delay(50);
}

await retrySessionStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
runtimeRecognizer.Stop();
if (Volatile.Read(ref sessionCalls) < 2)
    throw new InvalidOperationException("已建立的会话断线后没有进入第二次会话");

Console.WriteLine("PASS llm-audio-worker-retries-established-session");

await RunStreamingFailureCase("abrupt-disconnect", async stream => await Task.Delay(100));
await RunStreamingFailureCase("close-frame", stream => SendServerFrame(stream, 0x8, new byte[] { 0x03, 0xE8 }));
await RunStreamingFailureCase("protocol-error", stream =>
    SendServerFrame(stream, 0x1, Encoding.UTF8.GetBytes("{\"type\":\"error\",\"message\":\"expected\"}")),
    profile =>
    {
        profile.Result.EventPath = "type";
        profile.Error.EventValue = "error";
        profile.Error.MessagePath = "message";
    });
await RunStreamingNormalStopCase();

Console.WriteLine("PASS streaming-asr-terminal-failures-stop-worker");
return 0;

async Task RunStreamingFailureCase(string name, Func<NetworkStream, Task> serverAction,
    Action<StreamingAsrProfile>? configureProfile = null)
{
    var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await CompleteWebSocketHandshake(stream);
        await serverAction(stream);
        await stream.FlushAsync();
        await Task.Delay(100);
    });

    var profile = new StreamingAsrProfile
    {
        Name = name,
        UrlTemplate = $"ws://127.0.0.1:{port}/",
        Auth = new AuthSpec { Provider = "none" },
        Audio = new AudioSpec { Mode = "binary" }
    };
    configureProfile?.Invoke(profile);

    var engine = new StreamingAsrEngine(profile, new Dictionary<string, string>());
    var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
    var errorCount = 0;
    engine.OnError += ex =>
    {
        Interlocked.Increment(ref errorCount);
        error.TrySetResult(ex);
    };

    try
    {
        engine.Start();
        await error.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        if (Volatile.Read(ref errorCount) != 1)
            throw new InvalidOperationException($"{name} 错误上报次数错误: {errorCount}");
    }
    finally
    {
        engine.Stop();
        listener.Stop();
    }
}

static async Task CompleteWebSocketHandshake(NetworkStream stream)
{
    var requestBuffer = new byte[4096];
    var requestLength = 0;
    while (requestLength < requestBuffer.Length)
    {
        var read = await stream.ReadAsync(requestBuffer.AsMemory(requestLength));
        if (read == 0) throw new IOException("WebSocket 握手请求提前结束");
        requestLength += read;
        if (Encoding.ASCII.GetString(requestBuffer, 0, requestLength).Contains("\r\n\r\n")) break;
    }

    var request = Encoding.ASCII.GetString(requestBuffer, 0, requestLength);
    var keyLine = request.Split("\r\n")
        .First(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase));
    var key = keyLine[(keyLine.IndexOf(':') + 1)..].Trim();
    var acceptBytes = SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
    var response = Encoding.ASCII.GetBytes(
        $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {Convert.ToBase64String(acceptBytes)}\r\n\r\n");
    await stream.WriteAsync(response);
    await stream.FlushAsync();
}

static async Task SendServerFrame(NetworkStream stream, byte opcode, byte[] payload)
{
    if (payload.Length > 125) throw new ArgumentOutOfRangeException(nameof(payload));
    var frame = new byte[payload.Length + 2];
    frame[0] = (byte)(0x80 | opcode);
    frame[1] = (byte)payload.Length;
    Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
    await stream.WriteAsync(frame);
}

async Task RunStreamingNormalStopCase()
{
    var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var handshakeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var serverTask = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        await CompleteWebSocketHandshake(stream);
        handshakeCompleted.SetResult();
        await Task.Delay(250);
        await SendServerFrame(stream, 0x8, new byte[] { 0x03, 0xE8 });
        await stream.FlushAsync();
    });

    var engine = new StreamingAsrEngine(new StreamingAsrProfile
    {
        Name = "normal-stop",
        UrlTemplate = $"ws://127.0.0.1:{port}/",
        Auth = new AuthSpec { Provider = "none" },
        Audio = new AudioSpec { Mode = "binary" }
    }, new Dictionary<string, string>());
    var errorCount = 0;
    engine.OnError += _ => Interlocked.Increment(ref errorCount);

    try
    {
        engine.Start();
        await handshakeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        engine.Stop();
        await engine.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        if (Volatile.Read(ref errorCount) != 0)
            throw new InvalidOperationException($"主动停止被错误上报为故障: {errorCount}");
    }
    finally
    {
        engine.Stop();
        listener.Stop();
    }

    Console.WriteLine("PASS streaming-asr-normal-stop-has-no-error");
}
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}");
    return 1;
}

sealed class FakePluginManager : PluginManager
{
    private readonly IReadOnlyDictionary<string, IAudioSource> _audioSources;
    private readonly IReadOnlyDictionary<string, IRecognizer> _recognizers;

    public FakePluginManager(IAudioSource audio, IRecognizer recognizer)
    {
        _audioSources = new Dictionary<string, IAudioSource> { ["audio"] = audio };
        _recognizers = new Dictionary<string, IRecognizer> { ["recognizer"] = recognizer };
    }

    public override void LoadPlugins() { }
    public override IReadOnlyDictionary<string, IPlugin> Plugins => new Dictionary<string, IPlugin>();
    public override IReadOnlyDictionary<string, IAudioSource> AudioSources => _audioSources;
    public override IReadOnlyDictionary<string, IRecognizer> Recognizers => _recognizers;
    public override IReadOnlyDictionary<string, ITranslator> Translators => new Dictionary<string, ITranslator>();
}

abstract class FakePluginBase : IPlugin
{
    public string GUID => GetType().Name;
    public string Name => GUID;
    public string Description => "runtime check";
    public string Version => "1";
    public string SupportVersion => "any";
    public string Author => "runtime check";
    public string Url => "";
    public string License => "MIT";
    public string Note => "";
    public bool Available => true;
    public IPluginConfigEditor CreateConfigEditor() => null!;
    public void LoadConfig(string config) { }
    public void Init() { }
    public void Destroy() { }
}

sealed class FakeAudioSource : FakePluginBase, IAudioSource
{
    public int StartCount;
    public int StopCount;
    public event EventHandler<SourceStatus>? StatusChanged;
    public event EventHandler<byte[]>? DataAvailable;
    public event EventHandler<Exception>? ExceptionOccured;
    public void Start() => Interlocked.Increment(ref StartCount);
    public void Stop() => Interlocked.Increment(ref StopCount);
}

sealed class FailingOnStartRecognizer : FakePluginBase, IRecognizer
{
    public int StartCount;
    public int StopCount;
    public event EventHandler<SpeechEventArgs>? TextChanged;
    public event EventHandler<SpeechEventArgs>? SentenceDone;
    public event EventHandler<Exception>? ExceptionOccured;

    public void Start()
    {
        Interlocked.Increment(ref StartCount);
        ExceptionOccured?.Invoke(this, new InvalidOperationException("expected runtime-check failure"));
    }

    public void Stop() => Interlocked.Increment(ref StopCount);
    public void Feed(byte[] data) { }
}
