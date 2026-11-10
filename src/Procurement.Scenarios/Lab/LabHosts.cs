using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mocks.SapApi;
using Mocks.SourceApi;
using Procurement.AssistantApi;
using Procurement.AssistantApi.Infrastructure;
using Procurement.AssistantApi.Model;
using Procurement.AssistantApi.Services;

namespace Procurement.Scenarios.Lab;

// Hosted in memory: nothing listens on a port and no base URL leaves the process, so no scenario can reach a real SAP or source system.
public sealed class SourceHost(string dataset, IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<SourceApiMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Lab");
        // The lab runs from wherever the build output is; the hosts need no content files.
        builder.UseContentRoot(AppContext.BaseDirectory);
        builder.UseSetting("Dataset:Source", dataset);
        builder.UseSetting("RateLimit:PermitsPerSecond", "0");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        foreach (var (k, v) in settings) builder.UseSetting(k, v);
    }
}

public sealed class SapHost(string dataset, IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<SapApiMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Lab");
        builder.UseContentRoot(AppContext.BaseDirectory);
        builder.UseSetting("Dataset:Source", dataset);
        builder.UseSetting("RateLimit:PermitsPerSecond", "0");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        foreach (var (k, v) in settings) builder.UseSetting(k, v);
    }
}

public sealed class AssistantHost(SourceHost source, string syncStore, string dataset, IReadOnlyDictionary<string, string?> settings, HttpCapture capture)
    : WebApplicationFactory<AssistantApiMarker>
{
    public LabLogSink Logs { get; } = new();

    // Development only: tokens signed with this key are worthless anywhere else.
    public static readonly AuthOptions Auth = new()
    {
        Issuer = "procurement-dev",
        Audience = "procurement-assistant-api",
        SigningKey = "DEV-ONLY-signing-key-not-for-production-0123456789",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Lab");
        builder.UseContentRoot(AppContext.BaseDirectory);
        // Everything goes to the lab's sink (to check no secret is logged); the console stays quiet.
        builder.UseSetting("Logging:LogLevel:Default", "Debug");
        builder.UseSetting("Logging:Console:LogLevel:Default", "None");
        builder.ConfigureLogging(l => l.AddProvider(Logs));
        builder.UseSetting("Assistant:SourceBaseUrl", "http://source.lab/");
        builder.UseSetting("Assistant:RequestsPerMinute", "1000");
        builder.UseSetting("ConnectionStrings:SyncStore", syncStore);
        builder.UseSetting("Dataset:Source", dataset);
        builder.UseSetting("Auth:Issuer", Auth.Issuer);
        builder.UseSetting("Auth:Audience", Auth.Audience);
        builder.UseSetting("Auth:SigningKey", Auth.SigningKey);
        foreach (var (k, v) in settings) builder.UseSetting(k, v);
        builder.ConfigureTestServices(services =>
            services.AddHttpClient(nameof(SourceRequisitionsClient))
                .ConfigurePrimaryHttpMessageHandler(() => source.Server.CreateHandler())
                .AddHttpMessageHandler(() => new CaptureHandler(capture, "assistant→source")));
    }

    /// <summary>Without explicit scopes the client gets the ones the employee directory grants.</summary>
    public HttpClient ClientFor(string user, string[]? scopes = null, TimeSpan? lifetime = null)
    {
        var client = CreateDefaultClient(new CaptureHandler(capture, $"assistant ({user})"));
        var directory = Services.GetRequiredService<Procurement.SyntheticData.EmployeeDirectory>();
        var token = DevTokens.Issue(Auth, user, scopes ?? DevTokens.ScopesFor(directory.Find(user)), lifetime);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient Anonymous() => CreateDefaultClient(new CaptureHandler(capture, "assistant (anonymous)"));
}
