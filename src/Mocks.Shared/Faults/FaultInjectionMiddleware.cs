using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Mocks.Shared.Faults;

/// <summary>
/// Applies the decision from <see cref="FaultInjector"/> to each non-admin request.
/// A lost response lets the handler commit its change, then aborts the connection
/// so the client never learns the outcome.
/// </summary>
public sealed class FaultInjectionMiddleware(RequestDelegate next, FaultInjector injector, ILogger<FaultInjectionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (MockPaths.IsInfrastructure(context.Request.Path))
        {
            await next(context);
            return;
        }

        var decision = injector.Decide(context.Request.Method, context.Request.Path.Value ?? "/");
        if (decision.DelayMs > 0)
        {
            await Task.Delay(decision.DelayMs, context.RequestAborted);
        }

        switch (decision.Kind)
        {
            case FaultKind.Throttle:
                logger.LogInformation("Injected 429 for {Method} {Path}", context.Request.Method, context.Request.Path);
                context.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString();
                await Results.Problem(statusCode: 429, title: "Too many requests (injected)",
                    detail: $"Retry after {decision.RetryAfterSeconds}s.").ExecuteAsync(context);
                return;

            case FaultKind.Error:
                logger.LogInformation("Injected {Status} for {Method} {Path}", decision.StatusCode, context.Request.Method, context.Request.Path);
                await Results.Problem(statusCode: decision.StatusCode, title: "Service unavailable (injected)")
                    .ExecuteAsync(context);
                return;

            case FaultKind.LostResponse:
                logger.LogInformation("Injected lost response for {Method} {Path}", context.Request.Method, context.Request.Path);
                // Swap the whole body feature (not just Response.Body) so the server never
                // starts the response: the handler commits, the client sees a dropped connection.
                var original = context.Features.Get<IHttpResponseBodyFeature>();
                using (var buffer = new MemoryStream())
                {
                    context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(buffer));
                    try
                    {
                        await next(context);
                    }
                    finally
                    {
                        context.Features.Set(original);
                    }
                }
                context.Abort();
                return;

            default:
                await next(context);
                return;
        }
    }
}
