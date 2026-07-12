namespace TMSpeech.Recognizer.LLMAudio;

/// <summary>记录工作循环是否曾建立过有效会话，以及当前连续失败次数。</summary>
internal sealed class SessionRetryState
{
    public bool EverStarted { get; private set; }
    public int ConsecutiveFailures { get; private set; }

    public void MarkSessionStarted()
    {
        EverStarted = true;
        ConsecutiveFailures = 0;
    }

    public int RecordFailure() => ++ConsecutiveFailures;

    public bool RetryLimitReached(int maxConsecutiveFailures) =>
        !EverStarted || ConsecutiveFailures >= maxConsecutiveFailures;
}
