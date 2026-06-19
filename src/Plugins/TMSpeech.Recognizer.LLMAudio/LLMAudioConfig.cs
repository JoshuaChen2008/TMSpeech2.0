using System.Text.Json.Serialization;

namespace TMSpeech.Recognizer.LLMAudio;

/// <summary>
/// 语音识别大模型配置。使用 OpenAI 兼容的多模态（音频）聊天接口，仅做识别，不翻译。
/// </summary>
public class LLMAudioConfig
{
    /// <summary>OpenAI 兼容端点，例如 https://api.openai.com/v1 或 DashScope 兼容模式地址。</summary>
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    [JsonPropertyName("apiKey")] public string ApiKey { get; set; } = "";

    /// <summary>音频多模态模型名，例如 gpt-4o-audio-preview、qwen-audio-turbo。</summary>
    [JsonPropertyName("model")] public string Model { get; set; } = "gpt-4o-audio-preview";

    /// <summary>识别语言提示，例如 中文 / English / auto。</summary>
    [JsonPropertyName("language")] public string Language { get; set; } = "中文";

    /// <summary>提示词。务必强调"只输出原文、不要翻译/解释"。</summary>
    [JsonPropertyName("prompt")] public string Prompt { get; set; } =
        "你是一个语音转写器。请逐字转写音频中的语音内容，只输出听到的原文，不要翻译、不要解释、不要添加任何额外文字。";

    /// <summary>单段最长时长（毫秒），到时强制切段送识别。</summary>
    [JsonPropertyName("maxSegmentMs")] public int MaxSegmentMs { get; set; } = 8000;

    /// <summary>检测到多长的尾部静音就认为一句结束（毫秒）。</summary>
    [JsonPropertyName("silenceMs")] public int SilenceMs { get; set; } = 700;
}
