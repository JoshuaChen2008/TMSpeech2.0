namespace TMSpeech.Recognizer.StreamingAsr;

/// <summary>
/// 内置协议预设。预设在代码里构造（编译期可校验），用户也可选 "custom" 自行粘贴 Profile JSON。
/// 新增一家流式 ASR：多数情况只需在此加一个预设；遇到新签名/新音频结构才在引擎里加分支。
/// </summary>
public static class ProfilePresets
{
    public const string DashScopeFunAsr = "dashscope-funasr";
    public const string AliyunNls = "aliyun-nls";
    public const string OpenAiRealtime = "openai-realtime";
    public const string Custom = "custom";

    /// <summary>下拉显示用：id -> 中文名。</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Options() => new[]
    {
        new KeyValuePair<string, string>(DashScopeFunAsr, "阿里云 Fun-ASR（百炼）"),
        new KeyValuePair<string, string>(AliyunNls, "阿里云 NLS"),
        new KeyValuePair<string, string>(OpenAiRealtime, "OpenAI Realtime（实验性）"),
        new KeyValuePair<string, string>(Custom, "自定义（粘贴协议 JSON）"),
    };

    public static StreamingAsrProfile? Get(string id) => id switch
    {
        DashScopeFunAsr => DashScope(),
        AliyunNls => Nls(),
        OpenAiRealtime => OpenAi(),
        _ => null
    };

    private static StreamingAsrProfile DashScope() => new()
    {
        Name = "阿里云 Fun-ASR（百炼）",
        DefaultModel = "fun-asr-realtime",
        DefaultRegion = "beijing",
        RegionUrls = new Dictionary<string, string>
        {
            ["beijing"] = "wss://dashscope.aliyuncs.com/api-ws/v1/inference/",
            ["singapore"] = "wss://dashscope-intl.aliyuncs.com/api-ws/v1/inference/"
        },
        Auth = new AuthSpec { Provider = "apiKeyHeader", HeaderName = "Authorization", Scheme = "bearer" },
        Audio = new AudioSpec { Mode = "binary", Format = "pcm", SampleRate = 16000 },
        StartMessageTemplate =
            @"{""header"":{""action"":""run-task"",""task_id"":""{task_id}"",""streaming"":""duplex""},""payload"":{""task_group"":""audio"",""task"":""asr"",""function"":""recognition"",""model"":""{model}"",""parameters"":{""format"":""pcm"",""sample_rate"":16000},""input"":{}}}",
        StartAckEvent = "task-started",
        StopMessageTemplate =
            @"{""header"":{""action"":""finish-task"",""task_id"":""{task_id}"",""streaming"":""duplex""},""payload"":{""input"":{}}}",
        Result = new ResultSpec
        {
            EventPath = "header.event",
            PartialEvents = new[] { "result-generated" },
            FinalEvents = Array.Empty<string>(),
            TextPath = "payload.output.sentence.text",
            FinalFlagPath = "payload.output.sentence.sentence_end"
        },
        Error = new ErrorSpec { EventValue = "task-failed", MessagePath = "header.error_message" }
    };

    private static StreamingAsrProfile Nls() => new()
    {
        Name = "阿里云 NLS",
        DefaultModel = "",
        DefaultRegion = "cn-shanghai",
        NeedsAccessKey = true,
        RegionUrls = new Dictionary<string, string>
        {
            ["cn-shanghai"] = "wss://nls-gateway-cn-shanghai.aliyuncs.com/ws/v1",
            ["ap-southeast-1"] = "wss://nls-gateway-ap-southeast-1.aliyuncs.com/ws/v1"
        },
        Auth = new AuthSpec { Provider = "nlsToken", HeaderName = "X-NLS-Token" },
        Audio = new AudioSpec { Mode = "binary", Format = "pcm", SampleRate = 16000 },
        StartMessageTemplate =
            @"{""header"":{""message_id"":""{message_id}"",""task_id"":""{task_id}"",""namespace"":""SpeechTranscriber"",""name"":""StartTranscription"",""appkey"":""{app_key}""},""payload"":{""format"":""pcm"",""sample_rate"":16000,""enable_intermediate_result"":true,""enable_punctuation_prediction"":true,""enable_inverse_text_normalization"":true}}",
        StartAckEvent = "TranscriptionStarted",
        StopMessageTemplate =
            @"{""header"":{""message_id"":""{message_id}"",""task_id"":""{task_id}"",""namespace"":""SpeechTranscriber"",""name"":""StopTranscription"",""appkey"":""{app_key}""}}",
        Result = new ResultSpec
        {
            EventPath = "header.name",
            PartialEvents = new[] { "TranscriptionResultChanged" },
            FinalEvents = new[] { "SentenceEnd" },
            TextPath = "payload.result",
            FinalFlagPath = null
        },
        Error = new ErrorSpec { EventValue = "TaskFailed", MessagePath = "header.status_text" }
    };

    private static StreamingAsrProfile OpenAi() => new()
    {
        Name = "OpenAI Realtime",
        DefaultModel = "gpt-4o-transcribe",
        DefaultRegion = "default",
        Experimental = true,
        RegionUrls = new Dictionary<string, string>
        {
            ["default"] = "wss://api.openai.com/v1/realtime?intent=transcription"
        },
        Auth = new AuthSpec
        {
            Provider = "apiKeyHeader",
            HeaderName = "Authorization",
            Scheme = "Bearer",
            ExtraHeaders = new Dictionary<string, string> { ["OpenAI-Beta"] = "realtime=v1" }
        },
        Audio = new AudioSpec
        {
            Mode = "base64json",
            Format = "pcm16",
            SampleRate = 16000,
            MessageTemplate = @"{""type"":""input_audio_buffer.append"",""audio"":""{audio_base64}""}"
        },
        StartMessageTemplate =
            @"{""type"":""session.update"",""session"":{""input_audio_format"":""pcm16"",""input_audio_transcription"":{""model"":""{model}""}}}",
        StartAckEvent = "session.updated",
        StopMessageTemplate = null,
        Result = new ResultSpec
        {
            EventPath = "type",
            PartialEvents = new[] { "conversation.item.input_audio_transcription.delta" },
            FinalEvents = new[] { "conversation.item.input_audio_transcription.completed" },
            TextPath = "transcript",
            FinalFlagPath = null
        },
        Error = new ErrorSpec { EventValue = "error", MessagePath = "error.message" }
    };
}
