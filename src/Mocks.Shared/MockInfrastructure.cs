using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mocks.Shared.Faults;

namespace Mocks.Shared;

public sealed class MockRateLimitOptions
{
    // 0 turns rate limiting off.
    public int PermitsPerSecond { get; set; } = 20;

    public int Burst { get; set; } = 20;
}

public static class MockInfrastructure
{
    public static IServiceCollection AddMockInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<MonotonicClock>();
        services.AddSingleton(new FaultInjector(configuration.GetSection("Faults").Get<FaultSettings>()));

        var limits = configuration.GetSection("RateLimit").Get<MockRateLimitOptions>() ?? new MockRateLimitOptions();
        services.AddSingleton(limits);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (limits.PermitsPerSecond <= 0 || MockPaths.IsInfrastructure(context.Request.Path))
                    return RateLimitPartition.GetNoLimiter("unlimited");

                var client = context.Request.Headers["X-Client-Id"].ToString();
                return RateLimitPartition.GetTokenBucketLimiter(string.IsNullOrEmpty(client) ? "anonymous" : client,
                    _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = limits.Burst,
                        TokensPerPeriod = limits.PermitsPerSecond,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    });
            });
            options.OnRejected = async (context, token) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
                    ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))
                    : 1;
                var response = context.HttpContext.Response;
                response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                // Headers from draft-ietf-httpapi-ratelimit-headers.
                response.Headers["RateLimit-Policy"] = $"\"default\";q={limits.Burst};w=1";
                response.Headers["RateLimit"] = $"\"default\";r=0;t={retryAfter}";
                await Results.Problem(statusCode: 429, title: "Too many requests",
                    detail: $"Rate limit of {limits.PermitsPerSecond}/s exceeded. Retry after {retryAfter}s.")
                    .ExecuteAsync(context.HttpContext);
            };
        });
        return services;
    }

    public static WebApplication UseMockInfrastructure(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<FaultInjectionMiddleware>();
        app.UseRateLimiter();
        return app;
    }

    public static IEndpointRouteBuilder MapFaultAdmin(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/_admin/faults").WithTags("Admin");
        admin.MapGet("/", (FaultInjector faults) => Results.Ok(faults.Snapshot()));
        admin.MapPut("/", (FaultInjector faults, FaultSettings settings) =>
        {
            faults.Configure(settings);
            return Results.Ok(faults.Snapshot());
        });
        admin.MapPost("/script", (FaultInjector faults, ScriptedFault[] script) =>
        {
            foreach (var fault in script) faults.Enqueue(fault);
            return Results.Ok(faults.Snapshot());
        });
        admin.MapDelete("/", (FaultInjector faults) =>
        {
            faults.Reset();
            return Results.NoContent();
        });
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy" })).WithTags("Admin");
        return app;
    }
}
