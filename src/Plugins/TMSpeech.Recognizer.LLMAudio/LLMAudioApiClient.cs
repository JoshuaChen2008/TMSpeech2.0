using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TMSpeech.Recognizer.LLMAudio;

/// <summary>
/// 调用 OpenAI 兼容的多模态聊天接口，把一段 WAV 音频转写为文字。
/// 请求体使用 chat/completions + content 内 input_audio（base64, format=wav）。
/// </summary>
public class LLMAudioApiClient : IDisposable
{
    private readonly LLMAudioConfig _config;
    private readonly HttpClient _http;

    public LLMAudioApiClient(LLMAudioConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.ApiKey);
    }

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        // 部分服务商（如阿里云百炼/DashScope）要求 data 为带 MIME 前缀的 data URL，
        // 而非裸 base64，故统一使用 data URL 形式。
        var base64 = "data:audio/wav;base64," + Convert.ToBase64String(wav);
        var userText = string.IsNullOrWhiteSpace(_config.Language)
            ? "请转写这段音频的语音内容。"
            : $"请用{_config.Language}转写这段音频的语音内容。";

        var body = new
        {
            model = _config.Model,
            modalities = new[] { "text" },
            messages = new object[]
            {
                new { role = "system", content = _config.Prompt },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = userText },
                        new
                        {
                            type = "input_audio",
                            input_audio = new { data = base64, format = "wav" }
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var url = _config.BaseUrl.TrimEnd('/') + "/chat/completions";

        using var resp = await _http.PostAsync(url, content, ct);
        var respBody = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"语音识别大模型：接口返回 HTTP {(int)resp.StatusCode}：{Truncate(respBody, 500)}");

        return ExtractContent(respBody);
    }

    /// <summary>从 chat/completions 响应里取 choices[0].message.content（兼容字符串或数组形式）。</summary>
    private static string ExtractContent(string respBody)
    {
        using var doc = JsonDocument.Parse(respBody);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
            choices.GetArrayLength() == 0)
            return "";

        var message = choices[0].GetProperty("message");
        if (!message.TryGetProperty("content", out var contentEl))
            return "";

        if (contentEl.ValueKind == JsonValueKind.String)
            return contentEl.GetString() ?? "";

        // 数组形式：拼接所有 text 片段
        if (contentEl.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var part in contentEl.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
            }

            return sb.ToString();
        }

        return "";
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max) + "…";

    public void Dispose() => _http.Dispose();
}
