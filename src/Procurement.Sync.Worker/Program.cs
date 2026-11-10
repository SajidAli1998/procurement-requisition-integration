using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Procurement.Contracts;
using Procurement.Sync.Pipeline;
using Procurement.Sync.Reporting;
using Procurement.Sync.Store;
using Procurement.Sync.Worker;

var root = new RootCommand("procsync: synchronises approved purchase requisitions into SAP.");

var run = new Command("run", "Run one sync cycle and print a summary. Exit code 0 = clean, 1 = completed with issues, 3 = skipped (another run active).");
run.SetAction((_, ct) => WithServices(async (services, token) =>
{
    var result = await services.GetRequiredService<SyncRunner>().RunAsync("cli", token);
    Console.WriteLine(result.ToConsoleSummary());
    return ExitCode(result);
}, ct));

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

var statusId = new Argument<string>("id") { Description = "Source requisition id" };
var status = new Command("status", "Show where one requisition stands: state, SAP number, status history and quarantine.") { statusId };
status.SetAction((parse, ct) => WithServices(async (services, token) =>
{
    var view = await services.GetRequiredService<QuarantineService>().GetAsync(parse.GetValue(statusId)!, token);
    if (view is null)
    {
        Console.WriteLine("Not known to the integration.");
        return 2;
    }
    var r = view.Record;
    Console.WriteLine($"{r.SourceId}  v{r.SourceVersion}  {r.SourceStatus}");
    Console.WriteLine($"  state        {r.State}");
    Console.WriteLine($"  SAP number   {r.SapNumber ?? "-"} (holds v{r.SyncedSourceVersion?.ToString() ?? "-"})");
    Console.WriteLine($"  ERP status   {r.ErpStatus?.ToString() ?? "-"}{(r.PurchaseOrder is null ? "" : $"  PO {r.PurchaseOrder}")}");
    Console.WriteLine($"  write-back   {(r.WritebackPending ? "pending" : "done")}");
    if (r.LastError is not null) Console.WriteLine($"  last error   {r.LastError}");
    foreach (var h in view.History) Console.WriteLine($"  history      {h.ChangedAt:u}  {h.FromStatus?.ToString() ?? "-"} → {h.ToStatus}");
    foreach (var q in view.Quarantine) Console.WriteLine($"  quarantine   {(q.ResolvedAt is null ? "OPEN" : "resolved")}  {q.Reason}: {q.Summary}");
    return 0;
}, ct));

var quarantine = new Command("quarantine", "Inspect and act on quarantined requisitions.");

var reasonOption = new Option<QuarantineReason?>("--reason") { Description = "Only this reason" };
var allOption = new Option<bool>("--all") { Description = "Include resolved entries" };
var list = new Command("list", "List open quarantine entries.") { reasonOption, allOption };
list.SetAction((parse, ct) => WithServices(async (services, token) =>
{
    var entries = await services.GetRequiredService<QuarantineService>().ListAsync(parse.GetValue(reasonOption), parse.GetValue(allOption), token);
    Console.WriteLine($"{"Requisition",-24}{"Ver",4}  {"Reason",-27}{"Open",-6}Summary");
    foreach (var q in entries)
        Console.WriteLine($"{q.SourceKey,-24}{q.SourceVersion,4}  {q.Reason,-27}{(q.ResolvedAt is null ? "yes" : "no"),-6}{q.Summary}");
    Console.WriteLine($"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}");
    return 0;
}, ct));

var showId = new Argument<string>("id");
var show = new Command("show", "Show a quarantine entry with its suggested action and raw payload.") { showId };
show.SetAction((parse, ct) => WithServices(async (services, token) =>
{
    var view = await services.GetRequiredService<QuarantineService>().GetAsync(parse.GetValue(showId)!, token);
    var entry = view?.Quarantine is [.., var last] ? last : null;
    if (entry is null)
    {
        Console.WriteLine("No quarantine entry for this requisition.");
        return 2;
    }
    Console.WriteLine($"{entry.SourceKey} v{entry.SourceVersion}  {entry.Stage}  {entry.Reason}  ({(entry.ResolvedAt is null ? "open" : entry.Resolution)})");
    Console.WriteLine($"  summary  {entry.Summary}");
    Console.WriteLine($"  action   {SuggestedActions.For(entry.Reason, entry.SourceKey)}");
    Console.WriteLine($"  seen     {entry.Occurrences}x, first {entry.FirstSeenAt:u}, last {entry.LastSeenAt:u}");
    Console.WriteLine($"  details  {entry.Details}");
    Console.WriteLine($"  payload  {entry.RawPayload}");
    return 0;
}, ct));

var replayId = new Argument<string>("id");
var replay = new Command("replay", "Re-read one requisition from the source and push it through the pipeline again.") { replayId };
replay.SetAction((parse, ct) => WithServices(async (services, token) =>
{
    var result = await services.GetRequiredService<SyncRunner>().ReplayAsync(parse.GetValue(replayId)!, token);
    Console.WriteLine(result.ToConsoleSummary());
    return ExitCode(result);
}, ct));

var resolveId = new Argument<string>("id");
var note = new Option<string>("--note") { Description = "What was done about it", Required = true };
var resolve = new Command("resolve", "Close an open quarantine entry that was handled outside the integration.") { resolveId, note };
resolve.SetAction((parse, ct) => WithServices(async (services, token) =>
{
    var done = await services.GetRequiredService<QuarantineService>().ResolveAsync(parse.GetValue(resolveId)!, parse.GetValue(note)!, token);
    Console.WriteLine(done ? "Resolved." : "No open quarantine entry for this requisition.");
    return done ? 0 : 2;
}, ct));

quarantine.Subcommands.Add(list);
quarantine.Subcommands.Add(show);
quarantine.Subcommands.Add(replay);
quarantine.Subcommands.Add(resolve);

var reconcile = new Command("reconcile", "Check invariants against SAP and write a report without running a sync.");
reconcile.SetAction((_, ct) => WithServices(async (services, token) =>
{
    await services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<SyncDbContext>>().EnsureSyncStoreAsync(token);
    var now = DateTime.UtcNow;
    var report = await services.GetRequiredService<ReportBuilder>().BuildAsync($"reconcile-{now:yyyyMMdd-HHmmss}", "reconcile", now, "ReconciliationOnly", null, token);
    var path = await services.GetRequiredService<ReportWriter>().WriteAsync(report, token);
    foreach (var i in report.Invariants) Console.WriteLine($"[{i.Outcome.ToString().ToUpperInvariant(),-7}] {i.Name}: {i.Detail}");
    Console.WriteLine($"{report.OpenFailures.Count} open failure(s). Report: {path}");
    return report.Invariants.Any(i => i.Outcome == InvariantOutcome.Fail) ? 1 : 0;
}, ct));

var reportPath = new Option<string?>("--report") { Description = "Report JSON to check (default: latest.json in the report directory)" };
var checkExpected = new Command("check-expected",
    "Assessment data only: compare a run's per-requisition outcomes with the client's Reconciliation_Expected sheet. Use the report of the first run over the workbook.") { reportPath };
checkExpected.SetAction((parse, ct) => WithServices(async (services, token) =>
{
    var directory = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Procurement.Sync.SyncOptions>>().Value.ReportDirectory;
    var path = parse.GetValue(reportPath) ?? Path.Combine(directory, "latest.json");
    using var json = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path, token));
    if (!json.RootElement.TryGetProperty("records", out var records))
    {
        Console.WriteLine($"{path} has no per-requisition outcomes (written by an older version); run a sync first.");
        return 2;
    }
    var actual = records.EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!);
    var expected = Procurement.SyntheticData.DataPack.Load().ReconciliationExpected;

    var mismatches = new List<string>();
    foreach (var e in expected)
    {
        if (!actual.TryGetValue(e.RequisitionId, out var a)) { mismatches.Add($"{e.RequisitionId}: not in the report"); continue; }
        string? Str(string name) => a.GetProperty(name).ValueKind == System.Text.Json.JsonValueKind.Null ? null : a.GetProperty(name).ToString();
        foreach (var (column, want, got) in new[]
        {
            ("Source_Version", e.SourceVersion.ToString(), Str("sourceVersion")),
            ("Processing_Result", e.ProcessingResult, Str("result")),
            ("Remote_Document_ID", e.RemoteDocumentId, Str("sapNumber")),
            ("Attempts", e.Attempts.ToString(), Str("attempts")),
            ("Failure_Category", e.FailureCategory, Str("failureCategory")),
        })
        {
            if (want != got) mismatches.Add($"{e.RequisitionId} {column}: expected {want ?? "(blank)"}, got {got ?? "(blank)"}");
        }
    }

    var matching = expected.Count - mismatches.Select(m => m.Split(' ')[0]).Distinct().Count();
    Console.WriteLine($"{matching} of {expected.Count} rows of Reconciliation_Expected match {path}");
    Console.WriteLine("  compared: Source_Version, Processing_Result, Remote_Document_ID, Attempts, Failure_Category");
    foreach (var m in mismatches.Take(40)) Console.WriteLine($"  {m}");
    return mismatches.Count == 0 ? 0 : 1;
}, ct));

root.Subcommands.Add(run);
root.Subcommands.Add(serve);
root.Subcommands.Add(status);
root.Subcommands.Add(quarantine);
root.Subcommands.Add(reconcile);
root.Subcommands.Add(checkExpected);
return await root.Parse(args).InvokeAsync();

static async Task<int> WithServices(Func<IServiceProvider, CancellationToken, Task<int>> action, CancellationToken ct)
{
    using var host = SyncHost.Build();
    return await action(host.Services, ct);
}

static int ExitCode(RunResult result) => result.Skipped ? 3 : result.Status == RunStatus.Completed ? 0 : 1;
