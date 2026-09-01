namespace UnifiedAudio.Core.Engine;

public readonly record struct EngineCrashDecision(
    int RecentExitCount,
    TimeSpan RetryAfter,
    bool CircuitOpen);

public sealed class EngineCrashCircuit
{
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _exits = new();
    private readonly object _sync = new();
    private DateTimeOffset _nextConnectAt;

    public EngineCrashCircuit(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(30);
    }

    public EngineCrashDecision RecordExit(DateTimeOffset now)
    {
        lock (_sync)
        {
            Prune(now);
            _exits.Enqueue(now);
            var count = _exits.Count;
            var delay = count >= 3
                ? _window - (now - _exits.Peek())
                : TimeSpan.FromSeconds(Math.Pow(2, count - 1));
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            _nextConnectAt = now + delay;
            return new EngineCrashDecision(count, delay, count >= 3);
        }
    }

    public TimeSpan RecoveryDelay(DateTimeOffset now)
    {
        lock (_sync)
        {
            Prune(now);
            if (_exits.Count == 0) return TimeSpan.Zero;
            var delay = _nextConnectAt - now;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        while (_exits.Count > 0 && now - _exits.Peek() >= _window)
            _exits.Dequeue();
        if (_exits.Count == 0) _nextConnectAt = default;
    }
}
