using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Procurement.Sync.Clients;
using Procurement.Sync.Http;
using Procurement.Sync.Pipeline;
using Procurement.Sync.Reporting;
using Procurement.Sync.Store;
using Procurement.Sync.Validation;

namespace Procurement.Sync;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddProcurementSync(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SyncOptions>()
            .Bind(configuration.GetSection(SyncOptions.Section))
            .Validate(o => o.SourceBaseUrl.EndsWith('/') && o.SapBaseUrl.EndsWith('/'), "Base URLs must end with '/'")
            .Validate(o => o.PageSize is > 0 and <= 100, "PageSize must be between 1 and 100")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<RunMetrics>();
        services.AddSyncStore(configuration);
        services.AddSingleton<LeaseManager>();
        services.AddSingleton<RequisitionValidator>();

        services.AddHttpClient<SourceClient>((sp, http) => Configure(sp, http, o => o.SourceBaseUrl))
            .AddSyncResilience("source");
        services.AddTransient<SapAttemptRecorder>();
        services.AddHttpClient<SapClient>((sp, http) => Configure(sp, http, o => o.SapBaseUrl))
            .AddSyncResilience("sap")
            // Added after the resilience handler, so it runs inside it and sees each individual attempt.
            .AddHttpMessageHandler<SapAttemptRecorder>();

        services.AddTransient<ExtractStage>();
        services.AddTransient<DispatchStage>();
        services.AddTransient<StatusSyncStage>();
        services.AddTransient<WritebackStage>();
        services.AddTransient<ReportBuilder>();
        services.AddSingleton<ReportWriter>();
        services.AddTransient<SyncRunner>();
        services.AddTransient<QuarantineService>();
        return services;
    }

    private static void Configure(IServiceProvider services, HttpClient http, Func<SyncOptions, string> baseUrl)
    {
        var options = services.GetRequiredService<IOptions<SyncOptions>>().Value;
        http.BaseAddress = new Uri(baseUrl(options));
        http.DefaultRequestHeaders.Add("X-Client-Id", options.ClientId);
        // Timeouts are owned by the resilience pipeline (per attempt and in total).
        http.Timeout = Timeout.InfiniteTimeSpan;
    }
}
