using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Procurement.Sync.Pipeline;
using Procurement.Sync.Store;
using Procurement.Sync.Worker;

var root = new RootCommand("procsync: synchronises approved purchase requisitions into SAP.");

var run = new Command("run", "Run one sync cycle and print a summary. Exit code 0 = clean, 1 = completed with issues, 3 = skipped (another run active).");
run.SetAction(async (_, ct) =>
{
    using var host = SyncHost.Build();
    var result = await host.Services.GetRequiredService<SyncRunner>().RunAsync("cli", ct);
    Console.WriteLine(result.ToConsoleSummary());
    return result.Skipped ? 3 : result.Status == RunStatus.Completed ? 0 : 1;
});

var interval = new Option<int>("--interval") { Description = "Seconds between cycles", DefaultValueFactory = _ => 60 };
var serve = new Command("serve", "Run sync cycles continuously.") { interval };
serve.SetAction(async (parse, ct) =>
{
    var every = TimeSpan.FromSeconds(Math.Max(5, parse.GetValue(interval)));
    using var host = SyncHost.Build(services => services.AddHostedService(sp =>
        new SyncWorker(sp, every, sp.GetRequiredService<ILogger<SyncWorker>>())));
    await host.RunAsync(ct);
    return 0;
});

root.Subcommands.Add(run);
root.Subcommands.Add(serve);
return await root.Parse(args).InvokeAsync();
