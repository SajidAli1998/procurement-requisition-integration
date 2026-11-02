namespace Mocks.Shared.Faults;

/// <summary>
/// Probabilistic chaos settings, changeable at runtime via <c>PUT /_admin/faults</c>.
/// Rates are probabilities between 0 and 1 applied per request.
/// </summary>
public sealed record FaultSettings
{
    /// <summary>Chance of answering 503 Service Unavailable before the handler runs.</summary>
    public double TransientErrorRate { get; init; }

    /// <summary>Chance of answering 429 Too Many Requests with a Retry-After header.</summary>
    public double ThrottleRate { get; init; }

    public int ThrottleRetryAfterSeconds { get; init; } = 1;

    /// <summary>Chance, on writes, of committing the change and then dropping the connection.</summary>
    public double LostResponseRate { get; init; }

    /// <summary>Fixed latency added to every request.</summary>
    public int LatencyMs { get; init; }

    /// <summary>Source mock only: each page repeats the last record of the previous page.</summary>
    public bool PageOverlap { get; init; }

    /// <summary>SAP mock only: when false, Idempotency-Key is ignored, like a real SAP endpoint.</summary>
    public bool HonorIdempotencyKey { get; init; } = true;

    /// <summary>Seed for the random generator so chaos runs are reproducible.</summary>
    public int Seed { get; init; } = 1234;
}

public enum FaultKind { None, Error, Throttle, LostResponse, Delay }

/// <summary>
/// A deterministic fault for the next <see cref="Count"/> requests matching the method and path.
/// Used by tests and the demo to reproduce one exact failure.
/// </summary>
public sealed record ScriptedFault
{
    public required FaultKind Kind { get; init; }
    public string? Method { get; init; }
    public string? PathContains { get; init; }
    public int Count { get; init; } = 1;

    /// <summary>Matching requests to let through before the fault starts, e.g. "fail after page 3".</summary>
    public int Skip { get; init; }
    public int StatusCode { get; init; } = 503;
    public int RetryAfterSeconds { get; init; } = 1;
    public int DelayMs { get; init; }
}

public readonly record struct FaultDecision(FaultKind Kind, int StatusCode = 0, int RetryAfterSeconds = 0, int DelayMs = 0)
{
    public static readonly FaultDecision None = new(FaultKind.None);
}
