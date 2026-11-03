namespace Procurement.Sync.Http;

/// <summary>
/// Sits inside the resilience pipeline, so it sees every attempt, retries included, and records each one's
/// HTTP status on the request. The reconciliation report uses this to say, per requisition, how many tries
/// a create took and why (for example "created after two rate-limit responses").
/// </summary>
public sealed class SapAttemptRecorder : DelegatingHandler
{
    private static readonly HttpRequestOptionsKey<List<int>> Key = new("procsync.attempts");

    /// <summary>Starts recording attempts for this request; the returned list fills in as the request runs.</summary>
    public static List<int> Track(HttpRequestMessage request)
    {
        var attempts = new List<int>();
        request.Options.Set(Key, attempts);
        return attempts;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.TryGetValue(Key, out var attempts);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            attempts?.Add((int)response.StatusCode);
            return response;
        }
        catch
        {
            attempts?.Add(0);
            throw;
        }
    }
}
