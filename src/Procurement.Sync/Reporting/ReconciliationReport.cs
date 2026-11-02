namespace Procurement.Sync.Reporting;

public enum InvariantOutcome { Pass, Warn, Fail, Skipped }

public sealed record InvariantResult(string Name, InvariantOutcome Outcome, string Detail);

public sealed record CurrencyTotal(string Currency, int LocalCount, decimal LocalAmount, int SapCount, decimal SapAmount)
{
    public bool Matches => LocalCount == SapCount && LocalAmount == SapAmount;
}

public sealed record FailureRow(string Key, long? Version, string Stage, string Reason, string Summary, string SuggestedAction,
    DateTime FirstSeen, DateTime LastSeen, int Occurrences);

public sealed record ChangeRow(string Key, string Kind, string? Detail);

public sealed record PendingRow(string Key, string State, int Attempts, DateTime? NextAttemptAt, string? LastError);

/// <summary>Explains what one run did, what is still wrong and what to do about it.</summary>
public sealed record ReconciliationReport
{
    public required string RunId { get; init; }
    public required string Trigger { get; init; }
    public required DateTime StartedAt { get; init; }
    public required DateTime GeneratedAt { get; init; }
    public required string Status { get; init; }
    public string? Message { get; init; }

    /// <summary>What happened in this run, by event kind.</summary>
    public required IReadOnlyDictionary<string, int> RunCounts { get; init; }

    /// <summary>Where every known requisition stands now, by state.</summary>
    public required IReadOnlyDictionary<string, int> StateTotals { get; init; }

    public required IReadOnlyDictionary<string, long> Transport { get; init; }
    public required double RetryWaitSeconds { get; init; }
    public required IReadOnlyList<InvariantResult> Invariants { get; init; }
    public required IReadOnlyList<CurrencyTotal> Totals { get; init; }
    public required IReadOnlyList<FailureRow> OpenFailures { get; init; }
    public required IReadOnlyList<ChangeRow> NotableChanges { get; init; }
    public required IReadOnlyList<PendingRow> Pending { get; init; }
    public int ExcludedFromTotals { get; init; }
}
