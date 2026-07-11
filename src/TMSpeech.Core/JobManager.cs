using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using TMSpeech.Core.Plugins;
using TMSpeech.Core.Services.Notification;

namespace TMSpeech.Core
{
    public enum JobStatus
    {
        Stopped,
        Running,
        Paused,
    }

    public static class JobManagerFactory
    {
        private static Lazy<JobManager> _instance = new(() => new JobManagerImpl());
        public static JobManager Instance => _instance.Value;
    }

    public abstract class JobManager
    {
        private JobStatus _status;

        public JobStatus Status
        {
            get => _status;
            set
            {
                _status = value;
                StatusChanged?.Invoke(this, value);
            }
        }

        public long RunningSeconds { get; protected set; }

        public event EventHandler<JobStatus> StatusChanged;
        public event EventHandler<SpeechEventArgs> TextChanged;
        public event EventHandler<SpeechEventArgs> SentenceDone;
        public event EventHandler<long> RunningSecondsChanged;

        protected void OnTextChanged(SpeechEventArgs e) => TextChanged?.Invoke(this, e);
        protected void OnSentenceDone(SpeechEventArgs e) => SentenceDone?.Invoke(this, e);
        protected void OnUpdateRunningSeconds(long seconds) => RunningSecondsChanged?.Invoke(this, seconds);

        public abstract void Start();
        public abstract void Pause();
        public abstract void Stop();
    }

    public class JobManagerImpl : JobManager
    {
        private readonly PluginManager _pluginManager;


        internal JobManagerImpl()
            : this(PluginManagerFactory.GetInstance())
        {
        }

        internal JobManagerImpl(PluginManager pluginManager)
        {
            _pluginManager = pluginManager;
        }

        private IAudioSource? _audioSource;
        private IRecognizer? _recognizer;
        private HashSet<string> _sensitiveWords;
        private bool _disableInThisSentence = false;
        private string logFile;
        private string currentText = "";
        private readonly PluginFailureStopCoordinator _failureStopCoordinator = new();
        private readonly object _lifecycleLock = new();
        private long _sessionGeneration;
        private EventHandler<Exception>? _audioSourceExceptionHandler;
        private EventHandler<Exception>? _recognizerExceptionHandler;

        private void InitAudioSource(long generation)
        {
            var configAudioSource = ConfigManagerFactory.Instance.Get<string>(AudioSourceConfigTypes.AudioSource);
            var config = ConfigManagerFactory.Instance.Get<string>(AudioSourceConfigTypes.GetPluginConfigKey(configAudioSource));

            _audioSource = _pluginManager.AudioSources[configAudioSource];
            if (_audioSource != null)
            {
                _audioSource.LoadConfig(config);
                _audioSource.DataAvailable -= OnAudioSourceOnDataAvailable;
                _audioSource.DataAvailable += OnAudioSourceOnDataAvailable;
                _audioSourceExceptionHandler = (sender, ex) => OnPluginRunningExceptionOccurs(sender, ex, generation);
                _audioSource.ExceptionOccured += _audioSourceExceptionHandler;
            }
        }

        private Timer? _timer;


        private void OnAudioSourceOnDataAvailable(object? o, byte[] data)
        {
            // Console.WriteLine(o?.GetHashCode().ToString("x8") ?? "<null>");
            _recognizer?.Feed(data);
        }

        private void InitRecognizer(long generation)
        {
            var configRecognizer = ConfigManagerFactory.Instance.Get<string>(RecognizerConfigTypes.Recognizer);
            var config = ConfigManagerFactory.Instance.Get<string>(RecognizerConfigTypes.GetPluginConfigKey(configRecognizer));
            // default config
            if ((configRecognizer == null || configRecognizer.Length == 0) && _pluginManager.Recognizers.Count > 0)
            {
                configRecognizer = _pluginManager.Recognizers.Keys.First();
            }
            _recognizer = _pluginManager.Recognizers[configRecognizer];

            if (_recognizer != null)
            {
                _recognizer.LoadConfig(config);
                // https://stackoverflow.com/a/1104269
                // use -= first to prevent duplication.
                _recognizer.TextChanged -= OnRecognizerOnTextChanged;
                _recognizer.TextChanged += OnRecognizerOnTextChanged;
                _recognizer.SentenceDone -= OnRecognizerOnSentenceDone;
                _recognizer.SentenceDone += OnRecognizerOnSentenceDone;
                _recognizerExceptionHandler = (sender, ex) => OnPluginRunningExceptionOccurs(sender, ex, generation);
                _recognizer.ExceptionOccured += _recognizerExceptionHandler;
            }
        }

        private void OnRecognizerOnSentenceDone(object? sender, SpeechEventArgs args)
        {
            // Save the sentense to log
            if (logFile != null && logFile.Length > 0)
            {
                try
                {
                    File.AppendAllText(logFile, string.Format("{0:T}: {1}\n", DateTime.Now, args.Text.Text));
                }
                catch (Exception ex)
                {
                    NotificationManager.Instance.Notify(
                        $"写入识别日志失败: {ex.Message}",
                        "日志写入错误",
                        NotificationType.Warning);
                    System.Diagnostics.Debug.WriteLine($"Failed to write recognition log: {ex.Message}");
                    // 清空 logFile 避免重复通知
                    logFile = "";
                }
            }

            _disableInThisSentence = false;
            OnSentenceDone(args);
            currentText = "";
        }

        private void OnRecognizerOnTextChanged(object? sender, SpeechEventArgs args)
        {
            currentText = args.Text.Text;
            if (!_disableInThisSentence)
            {
                var s = _sensitiveWords.FirstOrDefault(x => args.Text.Text.Contains(x));
                if (!string.IsNullOrEmpty(s))
                {
                    NotificationManager.Instance.Notify($"检测到敏感词：{s}", "敏感词", NotificationType.Warning);
                    _disableInThisSentence = true;
                }
            }

            OnTextChanged(args);
        }

        private void StartRecognize(long generation)
        {
            InitSensitiveWords();
            InitAudioSource(generation);
            InitRecognizer(generation);

            if (_audioSource == null || _recognizer == null)
            {
                Status = JobStatus.Stopped;
                NotificationManager.Instance.Notify("语音源或识别器初始化失败", "语音源或识别器为空！", NotificationType.Error);
                return;
            }


            try
            {
                _recognizer.Start();
            }
            catch (InvalidOperationException ex)
            {
                NotificationManager.Instance.Notify($"识别器启动失败：\n{ex.Message}", "启动失败",
                    NotificationType.Error);
                return;
            }
            catch (Exception ex)
            {
                NotificationManager.Instance.Notify($"识别器启动失败：\n{ex.Message}\n{ex.StackTrace}", "启动失败",
                    NotificationType.Error);
                return;
            }

            try
            {
                _audioSource.Start();
            }
            catch (InvalidOperationException ex)
            {
                _recognizer?.Stop();
                NotificationManager.Instance.Notify($"语音源启动失败：\n{ex.Message}", "启动失败",
                    NotificationType.Error);
                return;
            }
            catch (Exception ex)
            {
                _recognizer?.Stop();
                NotificationManager.Instance.Notify($"语音源启动失败：\n{ex.Message}\n{ex.StackTrace}", "启动失败",
                    NotificationType.Error);
                return;
            }

            var logPath = ConfigManagerFactory.Instance.Get<string>(GeneralConfigTypes.ResultLogPath).Trim();
            if (logPath.Length > 0)
            {
                Directory.CreateDirectory(logPath);
                logFile = Path.Combine(logPath, string.Format("{0:yy-MM-dd-HH-mm-ss}.txt", DateTime.Now));
            } else
            {
                logFile = "";
            }

            if (Status == JobStatus.Stopped) RunningSeconds = 0;

            Status = JobStatus.Running;

            _timer = new Timer(TimerCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        }

        private void InitSensitiveWords()
        {
            var sensitiveWords = ConfigManagerFactory.Instance.Get<string>(NotificationConfigTypes.SensitiveWords);
            if (string.IsNullOrWhiteSpace(sensitiveWords))
            {
                _sensitiveWords = new HashSet<string>();
                return;
            }

            _sensitiveWords = new HashSet<string>(sensitiveWords.Split(new[] { ',', '，', '\n' },
                StringSplitOptions.RemoveEmptyEntries));
        }

        private void OnPluginRunningExceptionOccurs(object? e, Exception ex, long generation)
        {
            NotificationManager.Instance.Notify($"插件运行异常:\n ({e?.GetType().Module.Name})：{ex.Message}",
                "插件异常", NotificationType.Error);

            // 插件异常通常由其后台收发任务触发。不要在该回调线程中同步 Stop，
            // 否则插件清理代码可能等待当前任务结束而形成自等待。
            // 收发两端可能同时上报同一故障，因此只排队一次统一清理。
            _failureStopCoordinator.TrySchedule(generation, () => StopFailedSession(generation));
        }

        private void StopFailedSession(long generation)
        {
            lock (_lifecycleLock)
            {
                if (generation != _sessionGeneration) return;
                _sessionGeneration++;
                StopCore();
            }
        }


        private void TimerCallback(object? state)
        {
            RunningSeconds++;
            OnUpdateRunningSeconds(RunningSeconds);
        }

        private void StopRecognize()
        {
            try
            {
                _audioSource?.Stop();
                _recognizer?.Stop();
            }
            catch (Exception ex)
            {
                NotificationManager.Instance.Notify($"停止失败：\n{ex.Message}", "停止失败", NotificationType.Fatal);
            }

            if (currentText != null && currentText.Length > 0)
            {
                OnRecognizerOnSentenceDone(_recognizer, new SpeechEventArgs{Text=new TextInfo(currentText)});
                currentText = "";
            }

            _audioSource.DataAvailable -= OnAudioSourceOnDataAvailable;
            if (_audioSourceExceptionHandler != null)
                _audioSource.ExceptionOccured -= _audioSourceExceptionHandler;

            _recognizer.TextChanged -= OnRecognizerOnTextChanged;
            _recognizer.SentenceDone -= OnRecognizerOnSentenceDone;
            if (_recognizerExceptionHandler != null)
                _recognizer.ExceptionOccured -= _recognizerExceptionHandler;


            _audioSource = null;
            _recognizer = null;
            _audioSourceExceptionHandler = null;
            _recognizerExceptionHandler = null;
        }

        public override void Start()
        {
            lock (_lifecycleLock)
            {
                if (Status == JobStatus.Running) return;
                var generation = ++_sessionGeneration;
                StartRecognize(generation);
            }
        }

        public override void Pause()
        {
            lock (_lifecycleLock)
            {
                _sessionGeneration++;
                if (Status == JobStatus.Running) StopRecognize();
                Status = JobStatus.Paused;

                _timer?.Dispose();
                _timer = null;
            }
        }

        public override void Stop()
        {
            lock (_lifecycleLock)
            {
                _sessionGeneration++;
                StopCore();
            }
        }

        private void StopCore()
        {
            if (Status == JobStatus.Running) StopRecognize();
            Status = JobStatus.Stopped;

            // Clear text when stopped
            var emptyTextArg = new SpeechEventArgs();
            emptyTextArg.Text = new TextInfo(string.Empty);
            // OnSentenceDone(emptyTextArg); // TODO unable to save existing text.
            OnTextChanged(emptyTextArg);

            _timer?.Dispose();
            _timer = null;
        }
    }
}
