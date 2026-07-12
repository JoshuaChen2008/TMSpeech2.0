namespace TMSpeech.Core;

internal enum SessionLifecycleState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Paused
}

/// <summary>Single owner for a job session's generation and lifecycle state.</summary>
internal sealed class SessionLifecycle
{
    public long Generation { get; private set; }
    public SessionLifecycleState State { get; private set; } = SessionLifecycleState.Stopped;

    public long BeginStart()
    {
        Generation++;
        State = SessionLifecycleState.Starting;
        return Generation;
    }

    public bool TryMarkRunning(long generation)
    {
        if (generation != Generation || State != SessionLifecycleState.Starting) return false;
        State = SessionLifecycleState.Running;
        return true;
    }

    public bool IsCurrent(long generation) => generation == Generation;

    public void BeginStop()
    {
        Generation++;
        State = SessionLifecycleState.Stopping;
    }

    public void MarkStopped() => State = SessionLifecycleState.Stopped;
    public void MarkPaused() => State = SessionLifecycleState.Paused;
}
