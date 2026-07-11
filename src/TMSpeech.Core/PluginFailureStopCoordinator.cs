namespace TMSpeech.Core;

/// <summary>
/// 将插件后台线程上报的故障合并为一次异步停止，避免在插件自己的工作线程中执行清理。
/// </summary>
internal sealed class PluginFailureStopCoordinator
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _scheduledGenerations = new();

    public bool TrySchedule(long generation, Action stop)
    {
        if (!_scheduledGenerations.TryAdd(generation, 0)) return false;

        _ = Task.Run(() =>
        {
            try
            {
                stop();
            }
            finally
            {
                _scheduledGenerations.TryRemove(generation, out _);
            }
        });

        return true;
    }
}
