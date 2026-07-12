using System.Text.Json;

namespace TMSpeech.Recognizer.LLMAudio;

internal static class LLMAudioProtocol
{
    public static string BuildRunTaskMessage(LLMAudioConfig config, string taskId)
    {
        var parameters = new Dictionary<string, object> { ["format"] = "pcm", ["sample_rate"] = 16000 };
        if (config.MaxSentenceSilence > 0) parameters["max_sentence_silence"] = config.MaxSentenceSilence;
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["header"] = Header("run-task", taskId),
            ["payload"] = new Dictionary<string, object>
            {
                ["task_group"] = "audio", ["task"] = "asr", ["function"] = "recognition",
                ["model"] = config.Model, ["parameters"] = parameters,
                ["input"] = new Dictionary<string, object>()
            }
        });
    }

    public static string BuildFinishTaskMessage(string taskId) => JsonSerializer.Serialize(
        new Dictionary<string, object>
        {
            ["header"] = Header("finish-task", taskId),
            ["payload"] = new Dictionary<string, object> { ["input"] = new Dictionary<string, object>() }
        });

    private static Dictionary<string, object> Header(string action, string taskId) => new()
    {
        ["action"] = action, ["task_id"] = taskId, ["streaming"] = "duplex"
    };
}
