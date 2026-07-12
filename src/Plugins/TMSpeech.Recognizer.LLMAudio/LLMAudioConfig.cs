using System.Text.Json.Serialization;

namespace TMSpeech.Recognizer.LLMAudio;

/// <summary>
/// 阿里云百炼（DashScope）实时语音识别配置。
/// 通过 WebSocket 流式协议接入 Fun-ASR / Paraformer 等实时识别模型，仅需一个 API Key。
/// </summary>
public class LLMAudioConfig
{
    /// <summary>百炼 API Key（sk-xxx）。北京与新加坡地域的 Key 不同。</summary>
    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";

    /// <summary>实时识别模型名，例如 fun-asr-realtime、paraformer-realtime-v2。</summary>
    [JsonPropertyName("model")] public string Model { get; set; } = "fun-asr-realtime";

    /// <summary>服务地域：beijing（华北2·北京）/ singapore（新加坡）。决定 WebSocket 网关地址。</summary>
    [JsonPropertyName("region")] public string Region { get; set; } = "beijing";

    /// <summary>
    /// VAD 断句静音阈值（毫秒，Fun-ASR/Paraformer 的 max_sentence_silence）。
    /// 0 表示用服务端默认值。对话/聊天等需快速断句的场景可调小（如 400）。
    /// </summary>
    [JsonPropertyName("maxSentenceSilence")] public int MaxSentenceSilence { get; set; } = 0;

    /// <summary>
    /// 静音自动挂起：长时间无声时主动结束云端会话（不弹错误），检测到声音后自动重连恢复。
    /// 服务端约 23 秒收不到音频会强制断开（request timeout），开启此项可彻底避免该错误弹窗，
    /// 且静音期间不占用识别时长。
    /// </summary>
    [JsonPropertyName("autoSuspend")] public bool AutoSuspend { get; set; } = true;

    /// <summary>静音多少秒后挂起会话（建议小于服务端 23 秒超时，默认 12）。</summary>
    [JsonPropertyName("suspendAfterSeconds")] public int SuspendAfterSeconds { get; set; } = 12;

    /// <summary>声音判定阈值（RMS 千分比，0-1000）。低于该能量视为静音，默认 5（约 0.005）。</summary>
    [JsonPropertyName("voiceThresholdPermille")] public int VoiceThresholdPermille { get; set; } = 5;
}
