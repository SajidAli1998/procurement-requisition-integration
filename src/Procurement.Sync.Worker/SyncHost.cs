using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Procurement.Sync;

namespace Procurement.Sync.Worker;

internal static class SyncHost
{
    /// <summary>Configuration: appsettings.json next to the binary, then environment variables (e.g. Sync__SapBaseUrl).</summary>
    public static IHost Build(Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddProcurementSync(builder.Configuration);
        configure?.Invoke(builder.Services);
        return builder.Build();
    }
}
