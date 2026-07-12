using System.Text.Json.Serialization;

namespace TMSpeech.Recognizer.StreamingAsr;

/// <summary>
/// 协议模板：用数据描述一家流式 ASR 的 WebSocket 协议差异。
/// 「可数据化」的部分都在这里；签名、音频结构等「不可纯数据化」的部分由
/// Provider / Mode 字段按名字选中对应代码（见 StreamingAsrEngine）。
/// 该类同时用于内置预设（代码构造）与用户自定义（JSON 反序列化）。
/// </summary>
public class StreamingAsrProfile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>区域 -> WebSocket 地址。优先于 UrlTemplate。</summary>
    [JsonPropertyName("regionUrls")] public Dictionary<string, string> RegionUrls { get; set; } = new();

    /// <summary>无区域映射时使用，支持 {region}{model} 占位。</summary>
    [JsonPropertyName("urlTemplate")] public string? UrlTemplate { get; set; }

    [JsonPropertyName("defaultModel")] public string DefaultModel { get; set; } = "";
    [JsonPropertyName("defaultRegion")] public string DefaultRegion { get; set; } = "";

    [JsonPropertyName("auth")] public AuthSpec Auth { get; set; } = new();
    [JsonPropertyName("audio")] public AudioSpec Audio { get; set; } = new();

    /// <summary>开始指令 JSON 模板（含占位符）；null = 无开始指令。</summary>
    [JsonPropertyName("startMessageTemplate")] public string? StartMessageTemplate { get; set; }

    /// <summary>需等待的回执事件取值；null = 不等待，连接后直接发音频。</summary>
    [JsonPropertyName("startAckEvent")] public string? StartAckEvent { get; set; }

    /// <summary>结束指令 JSON 模板；null = 直接关连接。</summary>
    [JsonPropertyName("stopMessageTemplate")] public string? StopMessageTemplate { get; set; }

    [JsonPropertyName("result")] public ResultSpec Result { get; set; } = new();
    [JsonPropertyName("error")] public ErrorSpec Error { get; set; } = new();

    /// <summary>该预设是否需要 NLS 风格的 AppKey/AccessKey 字段（用于动态表单）。</summary>
    [JsonPropertyName("needsAccessKey")] public bool NeedsAccessKey { get; set; } = false;

    /// <summary>是否实验性（如 OpenAI Realtime 字段未充分联调）。</summary>
    [JsonPropertyName("experimental")] public bool Experimental { get; set; } = false;
}

public class AuthSpec
{
    /// <summary>"apiKeyHeader" | "nlsToken" | "none"</summary>
    [JsonPropertyName("provider")] public string Provider { get; set; } = "apiKeyHeader";

    [JsonPropertyName("headerName")] public string? HeaderName { get; set; }

    /// <summary>"Bearer" | "bearer" | "" （空表示直接放值）</summary>
    [JsonPropertyName("scheme")] public string Scheme { get; set; } = "";

    [JsonPropertyName("extraHeaders")] public Dictionary<string, string>? ExtraHeaders { get; set; }
}

public class AudioSpec
{
    /// <summary>"binary" | "base64json"</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "binary";

    [JsonPropertyName("format")] public string Format { get; set; } = "pcm";

    [JsonPropertyName("sampleRate")] public int SampleRate { get; set; } = 16000;

    /// <summary>base64json 模式下的消息 JSON 模板，含 {audio_base64}（可含 {message_id}）。</summary>
    [JsonPropertyName("messageTemplate")] public string? MessageTemplate { get; set; }
}

public class ResultSpec
{
    /// <summary>事件判别字段路径，如 "header.event" / "type"。</summary>
    [JsonPropertyName("eventPath")] public string EventPath { get; set; } = "";

    /// <summary>表示"中间结果"的事件取值。</summary>
    [JsonPropertyName("partialEvents")] public string[] PartialEvents { get; set; } = Array.Empty<string>();

    /// <summary>表示"整句完成"的事件取值（事件模式）。</summary>
    [JsonPropertyName("finalEvents")] public string[] FinalEvents { get; set; } = Array.Empty<string>();

    /// <summary>识别文本路径，如 "payload.output.sentence.text"。</summary>
    [JsonPropertyName("textPath")] public string TextPath { get; set; } = "";

    /// <summary>布尔标志模式：整句完成标志的路径；null 表示用事件模式区分。</summary>
    [JsonPropertyName("finalFlagPath")] public string? FinalFlagPath { get; set; }
}

public class ErrorSpec
{
    [JsonPropertyName("eventValue")] public string? EventValue { get; set; }
    [JsonPropertyName("messagePath")] public string? MessagePath { get; set; }
}
