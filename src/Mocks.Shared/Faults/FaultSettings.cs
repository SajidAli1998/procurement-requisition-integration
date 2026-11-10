namespace Mocks.Shared.Faults;

// Rates are per-request probabilities between 0 and 1.
public sealed record FaultSettings
{
    public double TransientErrorRate { get; init; }
    public double ThrottleRate { get; init; }
    public int ThrottleRetryAfterSeconds { get; init; } = 1;

    // Writes only: the change is committed, then the connection is dropped.
    public double LostResponseRate { get; init; }
    public int LatencyMs { get; init; }

    // Source mock: each page repeats the last record of the previous page.
    public bool PageOverlap { get; init; }

    // SAP mock: false ignores Idempotency-Key, as a real SAP endpoint does.
    public bool HonorIdempotencyKey { get; init; } = true;

    // SAP mock: ExternalReference queries return nothing, like a search index lagging behind writes.
    public bool ReferenceSearchLag { get; init; }

    public int Seed { get; init; } = 1234;
}

public enum FaultKind
{
    None,
    Error,               // before the handler runs, so nothing changes
    Throttle,
    LostResponse,        // handler commits, then the connection drops
    Delay,
    Abort,               // connection drops before the handler runs
    Respond,             // canned status and body, handler never runs
    CommitThenRespond,   // handler commits, client gets the canned answer instead
    CommitThenTransform, // handler commits, its JSON body is altered on the way out
}

public enum BodyTransform
{
    Truncate,            // half the body, no longer valid JSON
    SetField,            // set top-level Field to Value (a JSON literal), or remove it when Value is null
}

// Faults the next Count requests that match; tests use these to reproduce one exact failure.
public sealed record ScriptedFault
{
    public required FaultKind Kind { get; init; }
    public string? Method { get; init; }
    public string? PathContains { get; init; }
    public int Count { get; init; } = 1;

    // Matching requests to let through first, e.g. "fail after page 3".
    public int Skip { get; init; }
    public int StatusCode { get; init; } = 503;
    public int RetryAfterSeconds { get; init; } = 1;
    public int DelayMs { get; init; }
    public string? BodyContains { get; init; }
    public bool RetryAfterAsDate { get; init; }
    public bool OmitRetryAfter { get; init; }
    public string? Body { get; init; }
    public BodyTransform Transform { get; init; }
    public string? Field { get; init; }
    public string? Value { get; init; }
}

public readonly record struct FaultDecision(FaultKind Kind, int StatusCode = 0, int RetryAfterSeconds = 0, int DelayMs = 0)
{
    public ScriptedFault? Script { get; init; }

    public static readonly FaultDecision None = new(FaultKind.None);
}
