namespace Procurement.Sync;

public sealed class SyncOptions
{
    public const string Section = "Sync";

    /// <summary>Base URL of the internal requisition API (must end with '/').</summary>
    public string SourceBaseUrl { get; set; } = "http://localhost:5101/";

    /// <summary>Base URL of the SAP OData service (must end with '/').</summary>
    public string SapBaseUrl { get; set; } = "http://localhost:5102/sap/opu/odata/sap/API_PURCHASEREQ_PROCESS_SRV/";

    /// <summary>Sent as X-Client-Id so downstream rate limits are applied per client.</summary>
    public string ClientId { get; set; } = "procsync";

    public int PageSize { get; set; } = 50;

    /// <summary>How many requisitions are sent to SAP concurrently.</summary>
    public int Parallelism { get; set; } = 4;

    /// <summary>Dispatch attempts (each with its own HTTP retries) before a record is quarantined as RetryExhausted.</summary>
    public int MaxDispatchAttempts { get; set; } = 5;

    /// <summary>First wait before a RetryPending record is tried again; doubles per attempt.</summary>
    public TimeSpan RetryPendingBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Wait before re-checking SAP for a request whose outcome is unknown.</summary>
    public TimeSpan InDoubtRecheckDelay { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Directory for reconciliation reports.</summary>
    public string ReportDirectory { get; set; } = "reports";

    public ResilienceSettings Resilience { get; set; } = new();
}

/// <summary>Settings for the standard resilience handler on each downstream HttpClient.</summary>
public sealed class ResilienceSettings
{
    public int MaxRetryAttempts { get; set; } = 4;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound on a server's Retry-After, so one response cannot stall the run.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Multiplier for every retry wait. 1 in production; tests shrink it to run instantly.</summary>
    public double DelayScale { get; set; } = 1.0;

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public double BreakerFailureRatio { get; set; } = 0.5;
    public int BreakerMinimumThroughput { get; set; } = 10;
    public TimeSpan BreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan BreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Client-side token bucket; 0 disables it. Keeps us under the server limit instead of bouncing off it.</summary>
    public int PermitsPerSecond { get; set; } = 15;
}
