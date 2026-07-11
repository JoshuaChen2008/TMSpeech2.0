using TMSpeech.Core;
using TMSpeech.Core.Plugins;

if (args.Length != 1 || args[0] != "plugin-failure-stop")
{
    Console.Error.WriteLine("Usage: TMSpeech.Core.RuntimeChecks plugin-failure-stop");
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
return 0;

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
