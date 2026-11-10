using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Mocks.Shared.Faults;

public sealed class FaultInjectionMiddleware(RequestDelegate next, FaultInjector injector, ILogger<FaultInjectionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (MockPaths.IsInfrastructure(context.Request.Path))
        {
            await next(context);
            return;
        }

        string? body = null;
        var method = context.Request.Method;
        if (injector.NeedsBody && (HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method)))
        {
            context.Request.EnableBuffering();
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            body = await reader.ReadToEndAsync(context.RequestAborted);
            context.Request.Body.Position = 0;
        }

        // Match on the decoded query string too, so a fault can target a single $filter lookup.
        var target = (context.Request.Path.Value ?? "/") + Uri.UnescapeDataString(context.Request.QueryString.Value ?? "");
        var decision = injector.Decide(context.Request.Method, target, body);
        if (decision.DelayMs > 0)
        {
            await Task.Delay(decision.DelayMs, context.RequestAborted);
        }

        var script = decision.Script;
        switch (decision.Kind)
        {
            case FaultKind.Throttle:
                logger.Throttled(context.Request.Method, context.Request.Path);
                if (script?.OmitRetryAfter != true)
                {
                    context.Response.Headers.RetryAfter = script?.RetryAfterAsDate == true
                        ? DateTimeOffset.UtcNow.AddSeconds(decision.RetryAfterSeconds).ToString("R", CultureInfo.InvariantCulture)
                        : decision.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                }
                await Results.Problem(statusCode: 429, title: "Too many requests (injected)",
                    detail: $"Retry after {decision.RetryAfterSeconds}s.").ExecuteAsync(context);
                return;

            case FaultKind.Error:
                logger.Errored(decision.StatusCode, context.Request.Method, context.Request.Path);
                await Results.Problem(statusCode: decision.StatusCode, title: "Service unavailable (injected)")
                    .ExecuteAsync(context);
                return;

            case FaultKind.Abort:
                logger.Aborted(context.Request.Method, context.Request.Path);
                context.Abort();
                return;

            case FaultKind.Respond:
                logger.Responded(decision.StatusCode, context.Request.Method, context.Request.Path);
                await WriteAsync(context, decision.StatusCode, script?.Body);
                return;

            case FaultKind.LostResponse:
            case FaultKind.CommitThenRespond:
            case FaultKind.CommitThenTransform:
                await CommitThenAsync(context, decision);
                return;

            default:
                await next(context);
                return;
        }
    }

    private async Task CommitThenAsync(HttpContext context, FaultDecision decision)
    {
        logger.Committed(decision.Kind, context.Request.Method, context.Request.Path);
        // Swap the whole body feature, not just Response.Body, so the server never starts the
        // response and we decide what (if anything) the client sees.
        var original = context.Features.Get<IHttpResponseBodyFeature>();
        using var buffer = new MemoryStream();
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(buffer));
        try
        {
            await next(context);
        }
        finally
        {
            context.Features.Set(original);
        }

        switch (decision.Kind)
        {
            case FaultKind.LostResponse:
                context.Abort();
                return;

            case FaultKind.CommitThenRespond:
                context.Response.Headers.Remove("Location");
                context.Response.Headers.Remove("ETag");
                await WriteAsync(context, decision.StatusCode, decision.Script?.Body);
                return;

            default:
                var produced = Encoding.UTF8.GetString(buffer.ToArray());
                var script = decision.Script!;
                string altered;
                if (script.Transform == BodyTransform.Truncate)
                {
                    altered = produced[..(produced.Length / 2)];
                }
                else
                {
                    var node = JsonNode.Parse(produced)!.AsObject();
                    if (script.Value is null) node.Remove(script.Field!);
                    else node[script.Field!] = JsonNode.Parse(script.Value);
                    altered = node.ToJsonString();
                }
                context.Response.ContentLength = null;
                var bytes = Encoding.UTF8.GetBytes(altered);
                context.Response.ContentLength = bytes.Length;
                await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
                return;
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, string? body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body ?? "");
        context.Response.ContentLength = bytes.Length;
        if (bytes.Length > 0) await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }
}

internal static partial class FaultLog
{
    [LoggerMessage(LogLevel.Information, "Injected 429 for {Method} {Path}")]
    public static partial void Throttled(this ILogger logger, string method, PathString path);

    [LoggerMessage(LogLevel.Information, "Injected {Status} for {Method} {Path}")]
    public static partial void Errored(this ILogger logger, int status, string method, PathString path);

    [LoggerMessage(LogLevel.Information, "Injected connection drop (before processing) for {Method} {Path}")]
    public static partial void Aborted(this ILogger logger, string method, PathString path);

    [LoggerMessage(LogLevel.Information, "Injected canned {Status} for {Method} {Path}")]
    public static partial void Responded(this ILogger logger, int status, string method, PathString path);

    [LoggerMessage(LogLevel.Information, "Injected {Kind} for {Method} {Path}")]
    public static partial void Committed(this ILogger logger, FaultKind kind, string method, PathString path);
}
