using System.Collections.Concurrent;

namespace Procurement.AssistantApi.Infrastructure;

/// <summary>
/// Idempotency-Key handling for create (IETF draft semantics): same key and body → replay,
/// different body → 422, still running → 409. Keys are scoped per user and expire after the retention period.
/// In-memory here; a multi-instance deployment would use a shared store (e.g. SQL or Redis).
/// </summary>
public sealed class IdempotencyStore(TimeProvider time)
{
    public sealed record StoredResponse(int StatusCode, string Body, string? Location, string? ETag);

    private sealed record Slot(string Fingerprint, DateTime CreatedAt, StoredResponse? Response);

    public enum Claim { Acquired, Replay, InFlight, Mismatch }

    private readonly ConcurrentDictionary<string, Slot> _slots = new();

    public (Claim Claim, StoredResponse? Response) TryClaim(string key, string fingerprint, TimeSpan retention)
    {
        var now = time.GetUtcNow().UtcDateTime;
        while (true)
        {
            if (_slots.TryGetValue(key, out var existing))
            {
                if (now - existing.CreatedAt > retention)
                {
                    _slots.TryRemove(new KeyValuePair<string, Slot>(key, existing));
                    continue;
                }
                if (existing.Fingerprint != fingerprint) return (Claim.Mismatch, null);
                return existing.Response is null ? (Claim.InFlight, null) : (Claim.Replay, existing.Response);
            }
            if (_slots.TryAdd(key, new Slot(fingerprint, now, null))) return (Claim.Acquired, null);
        }
    }

    public void Complete(string key, StoredResponse response)
    {
        if (_slots.TryGetValue(key, out var slot)) _slots[key] = slot with { Response = response };
    }

    /// <summary>Nothing was created, so the caller may retry with the same key.</summary>
    public void Release(string key) => _slots.TryRemove(key, out _);
}
