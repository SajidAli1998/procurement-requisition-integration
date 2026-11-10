using Procurement.Contracts;

namespace Procurement.Sync.Store;

public enum RecordState
{
    Ignored,
    Queued,
    Sending,
    /// <summary>The request may or may not have reached SAP; resolved by lookup before any resend.</summary>
    InDoubt,
    RetryPending,
    Synced,
    Quarantined,
    /// <summary>An operator closed the quarantine entry after handling it outside the integration.</summary>
    Dismissed,
}

public sealed class RequisitionRecord
{
    public required string SourceId { get; set; }
    public long SourceVersion { get; set; }
    public string? SourceStatus { get; set; }
    public string? ContentHash { get; set; }
    public string? CanonicalJson { get; set; }
    public RecordState State { get; set; }

    public string? SapNumber { get; set; }
    public string? SapETag { get; set; }
    // The source version whose content SAP currently holds.
    public long? SyncedSourceVersion { get; set; }

    public ErpStatus? ErpStatus { get; set; }
    public DateTime? ErpStatusAt { get; set; }
    public string? PurchaseOrder { get; set; }
    public bool WritebackPending { get; set; }

    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    public DateTime FirstSeenAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Concurrency token, bumped on every save.
    public long Revision { get; set; }
}

public enum OutboxKind { Create, Amend }

// Written in the same transaction as the record's state change.
public sealed class OutboxMessage
{
    public long Id { get; set; }
    public required string SourceId { get; set; }
    public long SourceVersion { get; set; }
    public OutboxKind Kind { get; set; }
    public required string Payload { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}

public enum PipelineStage { Extract, Validate, Load, StatusSync, Writeback }

public sealed class QuarantineEntry
{
    public long Id { get; set; }
    // A content-hash key when the record had no usable id.
    public required string SourceKey { get; set; }
    public long? SourceVersion { get; set; }
    public PipelineStage Stage { get; set; }
    public QuarantineReason Reason { get; set; }
    public required string Summary { get; set; }
    public string? Details { get; set; }
    public string? RawPayload { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
    public int Occurrences { get; set; } = 1;
    public DateTime? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
}

public sealed class Checkpoint
{
    public required string Stream { get; set; }
    public string? Cursor { get; set; }
    public DateTime? Watermark { get; set; }
    public string? RunId { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum RunStatus { Running, Completed, CompletedWithIssues, Aborted, Failed }

public sealed class SyncRun
{
    public required string Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public RunStatus Status { get; set; }
    public string? Trigger { get; set; }
    public string? Message { get; set; }
}

public enum RunEventKind
{
    Extracted, Staged, Unchanged, Ignored, Quarantined, QuarantineResolved,
    Created, AlreadyInSap, Amended, InDoubt, InDoubtResolved, RetryScheduled, RetryExhausted,
    StatusChanged, StatusIgnored, WrittenBack, Conflict, SourceChangedAfterTransfer,
    /// <summary>SAP refused a stale version, so the requisition was re-read from the source before resending.</summary>
    VersionRefreshed,
}

public sealed class RunEvent
{
    public long Id { get; set; }
    public required string RunId { get; set; }
    public required string SourceKey { get; set; }
    public RunEventKind Kind { get; set; }
    public string? Detail { get; set; }
    // Comma-separated HTTP status per SAP write attempt; 0 means no response.
    public string? SapResponses { get; set; }
    public DateTime At { get; set; }
}

public sealed class StatusHistoryEntry
{
    public long Id { get; set; }
    public required string SourceId { get; set; }
    public required string SapNumber { get; set; }
    public ErpStatus? FromStatus { get; set; }
    public ErpStatus ToStatus { get; set; }
    public DateTime ChangedAt { get; set; }
    public DateTime RecordedAt { get; set; }
    public string? RunId { get; set; }
}

public sealed class Lease
{
    public required string Name { get; set; }
    public required string Holder { get; set; }
    public DateTime ExpiresAt { get; set; }
}
