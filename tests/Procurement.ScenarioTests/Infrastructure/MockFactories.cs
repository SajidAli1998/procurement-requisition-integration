using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Procurement.AssistantApi;
using Procurement.AssistantApi.Infrastructure;
using Procurement.AssistantApi.Model;
using Procurement.AssistantApi.Services;
using Mocks.SapApi;
using Mocks.SourceApi;

namespace Procurement.ScenarioTests.Infrastructure;

// dataset is "Workbook" (the client's data) or "Generated" (the 250-record stress set most scenarios use).
public sealed class SourceApiFactory(string dataset = "Generated") : WebApplicationFactory<SourceApiMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Dataset:Source", dataset);
        builder.UseSetting("RateLimit:PermitsPerSecond", "0");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }
}

public sealed class SapApiFactory(string dataset = "Generated") : WebApplicationFactory<SapApiMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Dataset:Source", dataset);
        builder.UseSetting("RateLimit:PermitsPerSecond", "0");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }
}

public sealed class AssistantApiFactory(SourceApiFactory source, string syncStore, string dataset = "Generated") : WebApplicationFactory<AssistantApiMarker>
{
    public static readonly AuthOptions Auth = new()
    {
        Issuer = "procurement-dev",
        Audience = "procurement-assistant-api",
        SigningKey = "DEV-ONLY-signing-key-not-for-production-0123456789",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Assistant:SourceBaseUrl", "http://localhost/");
        builder.UseSetting("Assistant:RequestsPerMinute", "1000");
        builder.UseSetting("ConnectionStrings:SyncStore", syncStore);
        builder.UseSetting("Dataset:Source", dataset);
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(nameof(SourceRequisitionsClient)).ConfigurePrimaryHttpMessageHandler(() => source.Server.CreateHandler()));
    }

    public HttpClient ClientFor(string user, params string[] scopes)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            DevTokens.Issue(Auth, user, scopes.Length == 0 ? [Scopes.Read, Scopes.Draft] : scopes));
        return client;
    }
}
