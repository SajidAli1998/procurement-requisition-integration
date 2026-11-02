using System.Net;
using Polly;
using Polly.Timeout;

namespace Procurement.Sync.Http;

/// <summary>
/// Decides whether the transport may retry a request on its own.
/// The rule: retry only when the server certainly did nothing, or when doing it twice is harmless.
/// </summary>
public static class RetryClassifier
{
    public static bool ShouldRetry(Outcome<HttpResponseMessage> outcome, HttpRequestMessage? request)
    {
        if (outcome.Result is { } response)
        {
            return response.StatusCode switch
            {
                // The server explicitly refused before doing any work: always safe.
                HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => true,
                // The request may have been processed: only repeat it if repeating is harmless.
                HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                    or HttpStatusCode.GatewayTimeout => IsSafeToRepeat(request),
                _ => false,
            };
        }

        return outcome.Exception switch
        {
            // Connection reset, timeout, dropped response: outcome unknown.
            HttpRequestException or TimeoutRejectedException or IOException => IsSafeToRepeat(request),
            _ => false,
        };
    }

    /// <summary>
    /// GET/HEAD/PUT/DELETE are idempotent by definition; a PATCH guarded by If-Match cannot apply twice
    /// because the first success changes the ETag. A POST is never repeated blindly: an unknown outcome
    /// is handed back to the pipeline, which checks SAP before sending again.
    /// </summary>
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
