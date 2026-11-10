using System.Collections.Concurrent;

namespace Procurement.Sync.Http;

public sealed class RunMetrics
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private long _retryWaitTicks;

    public void Reset()
    {
        _counters.Clear();
        Interlocked.Exchange(ref _retryWaitTicks, 0);
    }

    public void Increment(string name) => _counters.AddOrUpdate(name, 1, (_, v) => v + 1);

    public void RecordRetry(string client, int? statusCode, TimeSpan wait)
    {
        Increment($"{client}.retries");
        if (statusCode == 429) Increment($"{client}.throttled");
        Interlocked.Add(ref _retryWaitTicks, wait.Ticks);
    }

    public TimeSpan TotalRetryWait => TimeSpan.FromTicks(Interlocked.Read(ref _retryWaitTicks));

    public IReadOnlyDictionary<string, long> Snapshot() => new SortedDictionary<string, long>(_counters);

    public long Get(string name) => _counters.GetValueOrDefault(name);
}
