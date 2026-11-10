namespace Procurement.Sync.Http;

// Registered inside the resilience pipeline so it sees every attempt, retries included.
public sealed class SapAttemptRecorder : DelegatingHandler
{
    private static readonly HttpRequestOptionsKey<List<int>> Key = new("procsync.attempts");

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
