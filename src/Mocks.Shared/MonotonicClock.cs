namespace Mocks.Shared;

/// <summary>
/// Hands out strictly increasing UTC timestamps so keyset ordering on (timestamp, id)
/// is stable even when several records change within the same clock tick.
/// </summary>
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

    public DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
}
