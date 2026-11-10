using System.Net;
using Polly;
using Polly.Timeout;

namespace Procurement.Sync.Http;

// The transport retries on its own only when the server certainly did nothing, or when doing it twice is harmless.
public static class RetryClassifier
{
    // trustServiceUnavailable: a 503 means "refused before any work" (SAP throttling, and this mock). Turn it off
    // when a gateway can answer 503 after the backend committed; a POST then goes back to the pipeline, which
    // looks in SAP before sending again.
    public static bool ShouldRetry(Outcome<HttpResponseMessage> outcome, HttpRequestMessage? request, bool trustServiceUnavailable = true)
    {
        if (outcome.Result is { } response)
        {
            return response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => true,
                HttpStatusCode.ServiceUnavailable => trustServiceUnavailable || IsSafeToRepeat(request),
                // The request may have been processed: only repeat it if repeating is harmless.
                HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                    or HttpStatusCode.GatewayTimeout => IsSafeToRepeat(request),
                _ => false,
            };
        }

        return outcome.Exception switch
        {
            HttpRequestException or TimeoutRejectedException or IOException => IsSafeToRepeat(request),
            _ => false,
        };
    }

    // A PATCH guarded by If-Match cannot apply twice because the first success changes the ETag.
    public static bool IsSafeToRepeat(HttpRequestMessage? request)
    {
        if (request is null) return false;
        var method = request.Method;
        if (method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Put
            || method == HttpMethod.Delete || method == HttpMethod.Options)
        {
            return true;
        }
        return method == HttpMethod.Patch && request.Headers.IfMatch.Count > 0;
    }
}
