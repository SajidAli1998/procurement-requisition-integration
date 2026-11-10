namespace Mocks.Shared;

// Strictly increasing, so keyset ordering stays stable when several records change in the same tick.
public sealed class MonotonicClock(TimeProvider time)
{
    private readonly Lock _gate = new();
    private DateTime _last = DateTime.MinValue;

    public DateTime UtcNow
    {
        get
        {
            lock (_gate)
            {
                var now = time.GetUtcNow().UtcDateTime;
                _last = now > _last ? now : _last.AddTicks(1);
                return _last;
            }
        }
    }
}
