using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Procurement.Sync.Worker;

internal static class SyncHost
{
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
