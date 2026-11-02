namespace Mocks.SapApi;

/// <summary>
/// Server side of the IETF Idempotency-Key draft (draft-ietf-httpapi-idempotency-key-header):
/// same key + same body replays the first response, same key + different body is 422,
/// and a duplicate arriving while the first is still processing is 409.
/// </summary>
public sealed class IdempotencyStore
{
    public sealed record StoredResponse(int StatusCode, string Body, string? ETag, string? Location);

    private sealed class Slot(string fingerprint)
    {
        public string Fingerprint { get; } = fingerprint;
        public StoredResponse? Response { get; set; }
    }

    public enum Claim { Acquired, Replay, InFlight, Mismatch }

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    public (Claim Claim, StoredResponse? Response) TryClaim(string key, string fingerprint)
    {
        lock (_gate)
        {
            if (!_slots.TryGetValue(key, out var slot))
            {
                _slots[key] = new Slot(fingerprint);
                return (Claim.Acquired, null);
            }
            if (slot.Fingerprint != fingerprint) return (Claim.Mismatch, null);
            return slot.Response is null ? (Claim.InFlight, null) : (Claim.Replay, slot.Response);
        }
    }

    public void Complete(string key, StoredResponse response)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(key, out var slot)) slot.Response = response;
        }
    }

    /// <summary>Failed attempts release the key so the client can retry with the same key.</summary>
    public void Release(string key)
    {
        lock (_gate) _slots.Remove(key);
    }

    public void Reset()
    {
        lock (_gate) _slots.Clear();
    }
}
