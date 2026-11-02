using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Procurement.Sync.Pipeline;

namespace Procurement.Sync.Worker;

/// <summary>Runs a sync cycle on a fixed interval. A failed cycle is logged and the next one still runs.</summary>
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
                logger.LogInformation("{Summary}", result.ToConsoleSummary());
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Sync cycle failed; will try again in {Interval}", interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
