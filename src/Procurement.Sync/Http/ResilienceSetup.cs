using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;

namespace Procurement.Sync.Http;

public static class ResilienceSetup
{
    /// <summary>
    /// Standard resilience handler (rate limiter → total timeout → retry → circuit breaker → attempt timeout),
    /// tuned so retries respect <see cref="RetryClassifier"/> and server Retry-After hints.
    /// </summary>
    public static IHttpClientBuilder AddSyncResilience(this IHttpClientBuilder builder, string clientName)
    {
        builder.AddStandardResilienceHandler().Configure((options, services) =>
        {
            var settings = services.GetRequiredService<IOptions<SyncOptions>>().Value.Resilience;
            var metrics = services.GetRequiredService<RunMetrics>();
            var scale = settings.DelayScale;

            if (settings.PermitsPerSecond > 0)
            {
                var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                {
                    TokenLimit = settings.PermitsPerSecond,
                    TokensPerPeriod = settings.PermitsPerSecond,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                    QueueLimit = int.MaxValue,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                });
                options.RateLimiter.RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken);
            }

            options.TotalRequestTimeout.Timeout = settings.TotalTimeout;
            options.AttemptTimeout.Timeout = settings.AttemptTimeout;

            options.Retry.MaxRetryAttempts = settings.MaxRetryAttempts;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.Retry.Delay = settings.BaseDelay * scale;
            options.Retry.MaxDelay = settings.MaxRetryAfter * scale > options.Retry.Delay ? settings.MaxRetryAfter * scale : options.Retry.Delay;
            options.Retry.ShouldRetryAfterHeader = false; // handled below so it can be capped and scaled
            options.Retry.DelayGenerator = args =>
            {
                var retryAfter = args.Outcome.Result?.Headers.RetryAfter;
                TimeSpan? wait = retryAfter?.Delta
                    ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                if (wait is null) return ValueTask.FromResult<TimeSpan?>(null);
                var capped = wait.Value < TimeSpan.Zero ? TimeSpan.Zero
                    : wait.Value > settings.MaxRetryAfter ? settings.MaxRetryAfter : wait.Value;
                return ValueTask.FromResult<TimeSpan?>(capped * scale);
            };
            options.Retry.ShouldHandle = args =>
                ValueTask.FromResult(RetryClassifier.ShouldRetry(args.Outcome, args.Context.GetRequestMessage()));
            options.Retry.OnRetry = args =>
            {
                metrics.RecordRetry(clientName, (int?)args.Outcome.Result?.StatusCode, args.RetryDelay);
                return ValueTask.CompletedTask;
            };

            options.CircuitBreaker.FailureRatio = settings.BreakerFailureRatio;
            options.CircuitBreaker.MinimumThroughput = settings.BreakerMinimumThroughput;
            options.CircuitBreaker.SamplingDuration = settings.BreakerSamplingDuration;
            options.CircuitBreaker.BreakDuration = settings.BreakerBreakDuration;
            options.CircuitBreaker.OnOpened = _ =>
            {
                metrics.Increment($"{clientName}.circuitOpened");
                return ValueTask.CompletedTask;
            };
        });
        return builder;
    }
}
