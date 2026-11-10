using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Procurement.Sync.Pipeline;

namespace Procurement.Sync.Worker;

internal sealed class SyncWorker(IServiceProvider services, TimeSpan interval, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                using var scope = services.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<SyncRunner>().RunAsync("schedule", stoppingToken);
                logger.CycleFinished(result.ToConsoleSummary());
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.CycleFailed(ex, interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

internal static partial class WorkerLog
{
    [LoggerMessage(LogLevel.Information, "{Summary}")]
    public static partial void CycleFinished(this ILogger logger, string summary);

    [LoggerMessage(LogLevel.Error, "Sync cycle failed; will try again in {Interval}")]
    public static partial void CycleFailed(this ILogger logger, Exception exception, TimeSpan interval);
}
