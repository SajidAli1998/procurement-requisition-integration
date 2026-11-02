using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Mocks.SapApi;
using Mocks.SourceApi;

namespace Procurement.ScenarioTests.Infrastructure;

public sealed class SourceApiFactory : WebApplicationFactory<SourceApiMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("RateLimit:PermitsPerSecond", "0");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }
}

public sealed class SapApiFactory : WebApplicationFactory<SapApiMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("RateLimit:PermitsPerSecond", "0");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }
}
