namespace Procurement.Sync;

public sealed class SyncOptions
{
    public const string Section = "Sync";

    public string SourceBaseUrl { get; set; } = "http://localhost:5101/";
    public string SapBaseUrl { get; set; } = "http://localhost:5102/sap/opu/odata/sap/API_PURCHASEREQ_PROCESS_SRV/";

    // Sent as X-Client-Id so downstream rate limits apply per client.
    public string ClientId { get; set; } = "procsync";

    public int PageSize { get; set; } = 50;
    public int Parallelism { get; set; } = 4;

    // Each dispatch attempt has its own HTTP retries; after this many the record is quarantined as RetryExhausted.
    public int MaxDispatchAttempts { get; set; } = 5;

    // Doubles per attempt.
    public TimeSpan RetryPendingBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan InDoubtRecheckDelay { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(15);

    // Catches an approval withdrawn while the item waited (e.g. SAP was down). Fresh reads need no recheck.
    public ApprovalRecheck ApprovalRecheck { get; set; } = ApprovalRecheck.WhenStale;

    public TimeSpan ApprovalRecheckAfter { get; set; } = TimeSpan.FromMinutes(10);

    // A periodic read from the start finds records a lagging source index skipped. Null disables it.
    public TimeSpan? FullScanInterval { get; set; } = TimeSpan.FromHours(24);

    // SAP timestamps can collide. Re-applying a status is harmless because statuses only move forward.
    public TimeSpan StatusSyncOverlap { get; set; } = TimeSpan.FromSeconds(2);

    public bool VerifyAgainstSap { get; set; } = true;
    public string ReportDirectory { get; set; } = "reports";

    public ResilienceSettings Resilience { get; set; } = new();
}

public enum ApprovalRecheck { Never, WhenStale, Always }

public sealed class ResilienceSettings
{
    public int MaxRetryAttempts { get; set; } = 4;

    // Safe when SAP honours Idempotency-Key or answers 503 only before processing; otherwise turn it off.
    public bool TrustServiceUnavailable { get; set; } = true;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    // Caps a server's Retry-After so one response cannot stall the run.
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    // Tests shrink every retry wait with this.
    public double DelayScale { get; set; } = 1.0;

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public double BreakerFailureRatio { get; set; } = 0.5;
    public int BreakerMinimumThroughput { get; set; } = 10;
    public TimeSpan BreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan BreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(15);

    // Client-side token bucket that keeps us under the server limit instead of bouncing off it. 0 disables it.
    public int PermitsPerSecond { get; set; } = 15;
}
