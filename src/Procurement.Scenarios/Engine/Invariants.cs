using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Procurement.Contracts;
using Procurement.Scenarios.Lab;
using Procurement.Sync.Store;

namespace Procurement.Scenarios.Engine;

/// <summary>Checked against the lab's actual state (SAP documents, the integration database, the HTTP log), never against counters a scenario reports itself.</summary>
public static class Invariants
{
    private static readonly string[] SelfJudged = ["INV-08", "INV-09"];

    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["INV-01"] = "No unintended duplicate SAP creation",
        ["INV-02"] = "Complete source accounting",
        ["INV-03"] = "No phantom success",
        ["INV-04"] = "Safe repeatability",
        ["INV-05"] = "Status integrity",
        ["INV-06"] = "Durable recovery",
        ["INV-07"] = "Data integrity",
        ["INV-08"] = "Authorization",
        ["INV-09"] = "Exact confirmation",
        ["INV-10"] = "Honest reconciliation",
        ["INV-11"] = "No unsafe retry",
        ["INV-12"] = "No unauthorized production effects",
    };

    public static readonly string[] Pipeline = ["INV-01", "INV-02", "INV-03", "INV-04", "INV-05", "INV-06", "INV-07", "INV-10", "INV-11", "INV-12"];

    // 8 and 9 are judged by the scenario's own assertions.
    public static readonly string[] Assistant = ["INV-08", "INV-09", "INV-12"];

    private static readonly HashSet<string> LabHosts = ["source.lab", "sap.lab", "localhost"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<List<InvariantResult>> EvaluateAsync(ScenarioContext ctx, IReadOnlyList<JsonObject> sapBefore)
    {
        var lab = ctx.Lab;
        var wanted = ctx.Definition.Invariants;
        var results = new List<InvariantResult>();
        void Add(string id, InvariantOutcome outcome, string detail) => results.Add(new InvariantResult(id, Names[id], outcome, detail));

        var sap = await lab.SapDocumentsAsync();
        var records = await SafeRecordsAsync(lab);

        if (wanted.Contains("INV-01"))
        {
            var before = sapBefore.Select(d => Str(d, "PurchaseRequisition")).ToHashSet();
            var admin = lab.AdminSapNumbers.ToHashSet();
            var problems = new List<string>();
            foreach (var group in sap.GroupBy(d => Str(d, "ExternalReference")))
            {
                var made = group.Where(d => !before.Contains(Str(d, "PurchaseRequisition")) && !admin.Contains(Str(d, "PurchaseRequisition"))).ToList();
                var existed = group.Count() - made.Count;
                if (made.Count > 1 || (made.Count == 1 && existed > 0))
                    problems.Add($"{group.Key}: {string.Join(", ", group.Select(d => Str(d, "PurchaseRequisition")))} ({made.Count} created by the integration)");
            }
            Add("INV-01", problems.Count == 0 ? InvariantOutcome.Pass : InvariantOutcome.Fail,
                problems.Count == 0 ? $"{sap.Count} SAP document(s); the integration created at most one per requisition and none where one existed." : string.Join("; ", problems));
        }

        if (wanted.Contains("INV-02"))
        {
            if (records is null) Add("INV-02", InvariantOutcome.Fail, "The integration database could not be read.");
            else
            {
                var source = await lab.SourceSnapshotAsync();
                var known = records.Select(r => r.SourceId).ToHashSet();
                var listed = source.Count(s => s?["status"]?.GetValue<string>() == "Approved");
                var unknownApproved = source.Where(s => s?["status"]?.GetValue<string>() == "Approved" && s["id"] is not null && !known.Contains(s["id"]!.GetValue<string>()))
                    .Select(s => s!["id"]!.GetValue<string>()).ToList();
                var stuck = records.Where(r => r.State == RecordState.Sending).Select(r => r.SourceId).ToList();
                var ok = unknownApproved.Count == 0 && stuck.Count == 0;
                var states = string.Join(", ", records.GroupBy(r => r.State).Select(g => $"{g.Count()} {g.Key}"));
                Add("INV-02", ok ? InvariantOutcome.Pass : InvariantOutcome.Fail, ok
                    ? $"{records.Count} record(s) tracked ({states}); every approved source record ({listed}) has an outcome or a tracked pending state."
                    : $"Approved in the source but unknown to the integration: {string.Join(", ", unknownApproved.Take(15))}{(stuck.Count > 0 ? $"; stuck in Sending: {string.Join(", ", stuck)}" : "")}");
            }
        }

        if (wanted.Contains("INV-03"))
        {
            if (records is null) Add("INV-03", InvariantOutcome.Fail, "The integration database could not be read.");
            else
            {
                var byNumber = sap.GroupBy(d => Str(d, "PurchaseRequisition")).ToDictionary(g => g.Key, g => g.First());
                var bad = records.Where(r => r.State == RecordState.Synced)
                    .Where(r => r.SapNumber is null || !byNumber.TryGetValue(r.SapNumber, out var d) || Str(d, "ExternalReference") != r.SourceId)
                    .Select(r => $"{r.SourceId} ({r.SapNumber ?? "no SAP number"})").ToList();
                var synced = records.Count(r => r.State == RecordState.Synced);
                Add("INV-03", bad.Count == 0 ? InvariantOutcome.Pass : InvariantOutcome.Fail, bad.Count == 0
                    ? $"All {synced} synchronised record(s) point at a SAP document for the same reference."
                    : $"Counted as synchronised without a matching SAP document: {string.Join(", ", bad.Take(15))}");
            }
        }

        if (wanted.Contains("INV-05"))
        {
            if (records is null) Add("INV-05", InvariantOutcome.Fail, "The integration database could not be read.");
            else
            {
                await using var db = lab.Db();
                var history = await db.StatusHistory.AsNoTracking().OrderBy(h => h.Id).ToListAsync();
                var violations = new List<string>();
                foreach (var g in history.GroupBy(h => h.SourceId))
                {
                    ErpStatus? current = null;
                    foreach (var h in g)
                    {
                        if (current is { } c && !c.CanTransitionTo(h.ToStatus)) violations.Add($"{g.Key}: {c} to {h.ToStatus}");
                        current = h.ToStatus;
                    }
                }
                var mismatched = records.Where(r => r.ErpStatus is not null && !r.WritebackPending && r.State == RecordState.Synced).ToList();
                Add("INV-05", violations.Count == 0 ? InvariantOutcome.Pass : InvariantOutcome.Fail, violations.Count == 0
                    ? $"{history.Count} status change(s), all forward-only; {mismatched.Count} record(s) with a written-back ERP status."
                    : $"Backward transitions: {string.Join(", ", violations.Take(10))}");
            }
        }

        if (wanted.Contains("INV-06"))
        {
            if (records is null) Add("INV-06", InvariantOutcome.Fail, "The integration database could not be read.");
            else
            {
                var sending = records.Where(r => r.State == RecordState.Sending).Select(r => r.SourceId).ToList();
                var crashedUnrecovered = ctx.Result.Runs.LastOrDefault()?.Kind is "Crashed" or "Failed";
                var ok = sending.Count == 0 && !crashedUnrecovered;
                Add("INV-06", ok ? InvariantOutcome.Pass : InvariantOutcome.Fail, ok
                    ? "No record is left mid-send and the last run finished; state is durable and consistent."
                    : crashedUnrecovered ? "The last run crashed or failed and the scenario never recovered from it." : $"Left in Sending: {string.Join(", ", sending)}");
            }
        }

        if (wanted.Contains("INV-07"))
        {
            if (records is null) Add("INV-07", InvariantOutcome.Fail, "The integration database could not be read.");
            else
            {
                var byNumber = sap.GroupBy(d => Str(d, "PurchaseRequisition")).ToDictionary(g => g.Key, g => g.First());
                var mismatches = new List<string>();
                var compared = 0;
                foreach (var r in records.Where(r => r.SapNumber is not null && r.CanonicalJson is not null))
                {
                    var req = JsonSerializer.Deserialize<Requisition>(r.CanonicalJson!, Json);
                    if (req is null || req.Version != r.SyncedSourceVersion || !byNumber.TryGetValue(r.SapNumber!, out var doc)) continue;
                    compared++;
                    var items = doc["to_PurchaseReqnItem"]?.AsArray() ?? [];
                    var sapTotal = items.Sum(i => i!["RequestedQuantity"]!.GetValue<decimal>() * i["PurchaseRequisitionPrice"]!.GetValue<decimal>());
                    var srcTotal = req.Lines.Sum(l => l.Quantity * l.UnitPrice);
                    var currencies = items.Select(i => i!["PurReqnItemCurrency"]?.GetValue<string>()).Distinct().ToList();
                    if (items.Count != req.Lines.Count) mismatches.Add($"{r.SourceId}: {req.Lines.Count} line(s) in the source, {items.Count} in SAP");
                    else if (Math.Abs(sapTotal - srcTotal) > 0.01m) mismatches.Add($"{r.SourceId}: amount {srcTotal} in the source, {sapTotal} in SAP");
                    else if (currencies.Count != 1 || currencies[0] != req.Currency) mismatches.Add($"{r.SourceId}: currency {req.Currency} vs {string.Join("/", currencies)}");
                }
                Add("INV-07", mismatches.Count == 0 ? InvariantOutcome.Pass : InvariantOutcome.Fail, mismatches.Count == 0
                    ? $"{compared} synchronised requisition(s) compared: line count, amount and currency match SAP."
                    : string.Join("; ", mismatches.Take(10)));
            }
        }

        if (wanted.Contains("INV-11"))
        {
            var honoured = (await lab.SapFaultStateAsync())["settings"]?["honorIdempotencyKey"]?.GetValue<bool>() ?? true;
            var exchanges = lab.Capture.Exchanges.Where(e => e.Channel.EndsWith("→sap", StringComparison.Ordinal)).OrderBy(e => e.Seq).ToList();
            var unsafeRetries = new List<string>();
            var creates = exchanges.Where(e => e.Method == "POST" && e.Url.EndsWith("A_PurchaseRequisitionHeader", StringComparison.Ordinal))
                .Select(e => (Exchange: e, Reference: ReferenceOf(e.RequestBody))).Where(x => x.Reference is not null).ToList();
            foreach (var group in creates.GroupBy(c => c.Reference))
            {
                var list = group.ToList();
                for (var i = 1; i < list.Count; i++)
                {
                    var prev = list[i - 1].Exchange;
                    var next = list[i].Exchange;
                    var uncertain = prev.Status is null or >= 500 || (prev.Status is >= 200 and < 300);
                    if (!uncertain) continue;
                    var sameKey = prev.IdempotencyKey is not null && prev.IdempotencyKey == next.IdempotencyKey && honoured;
                    var lookedUp = exchanges.Any(e => e.Seq > prev.Seq && e.Seq < next.Seq && e.Method == "GET"
                        && (e.Url.Contains(group.Key!, StringComparison.Ordinal) || Uri.UnescapeDataString(e.Url).Contains(group.Key!, StringComparison.Ordinal)));
                    var refusedBeforeWork = prev.Status == 503 && sameKey;
                    if (!sameKey && !lookedUp && !refusedBeforeWork)
                        unsafeRetries.Add($"{group.Key}: create #{prev.Seq} ended {prev.Status?.ToString() ?? prev.Outcome}, create #{next.Seq} sent without a lookup or an honoured Idempotency-Key");
                }
            }
            Add("INV-11", unsafeRetries.Count == 0 ? InvariantOutcome.Pass : InvariantOutcome.Fail, unsafeRetries.Count == 0
                ? $"{creates.Count} create request(s); every repeat after an uncertain outcome was preceded by a SAP lookup or replayed under an honoured Idempotency-Key."
                : string.Join("; ", unsafeRetries.Take(10)));
        }

        if (wanted.Contains("INV-10"))
        {
            try
            {
                var report = await lab.ReconcileAsync();
                var dupRefs = sap.GroupBy(d => Str(d, "ExternalReference")).Count(g => g.Count() > 1);
                var linked = records?.Where(r => r.SapNumber is not null).Select(r => r.SapNumber!).ToHashSet() ?? [];
                var orphans = sap.Count(d => !linked.Contains(Str(d, "PurchaseRequisition")));
                var dupInv = report.Invariants.FirstOrDefault(i => i.Name.StartsWith("No duplicate", StringComparison.Ordinal));
                var orphanInv = report.Invariants.FirstOrDefault(i => i.Name.StartsWith("Every SAP requisition is linked", StringComparison.Ordinal));
                var problems = new List<string>();
                if (dupRefs > 0 && dupInv?.Outcome == Procurement.Sync.Reporting.InvariantOutcome.Pass) problems.Add($"SAP has {dupRefs} duplicated reference(s) but the report says none");
                if (orphans > 0 && orphanInv?.Outcome == Procurement.Sync.Reporting.InvariantOutcome.Pass) problems.Add($"SAP has {orphans} unlinked document(s) but the report says none");
                var reported = report.StateTotals.Values.Sum();
                if (records is not null && reported != records.Count) problems.Add($"The report accounts for {reported} requisition(s), the integration tracks {records.Count}");
                ctx.Evidence("reconciliation", report.Invariants.Select(i => new { i.Name, Outcome = i.Outcome.ToString(), i.Detail }));
                Add("INV-10", problems.Count == 0 ? InvariantOutcome.Pass : InvariantOutcome.Fail, problems.Count == 0
                    ? $"Report reflects SAP: {dupRefs} duplicated reference(s), {orphans} unlinked document(s), {reported} requisition(s) explained."
                    : string.Join("; ", problems));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Add("INV-10", InvariantOutcome.Fail, $"Reconciliation could not be built: {ex.GetBaseException().Message}");
            }
        }

        if (wanted.Contains("INV-04"))
        {
            // Replay everything once more with faults cleared: nothing that already exists in SAP may be created again.
            await lab.ClearFaultsAsync();
            if (ctx.Result.Runs.LastOrDefault()?.Kind is "Crashed") await lab.RestartAsync();
            var before = (await lab.SapDocumentsAsync()).GroupBy(d => Str(d, "ExternalReference")).ToDictionary(g => g.Key, g => g.Count());
            var rerun = await lab.RunAsync("invariant rerun");
            ctx.RecordRun("INV-04 replay (faults cleared)", rerun);
            var after = (await lab.SapDocumentsAsync()).GroupBy(d => Str(d, "ExternalReference")).ToDictionary(g => g.Key, g => g.Count());
            var duplicated = after.Where(a => before.TryGetValue(a.Key, out var n) && a.Value > n).Select(a => a.Key).ToList();
            Add("INV-04", duplicated.Count == 0 && rerun.Kind != RunOutcomeKind.Failed ? InvariantOutcome.Pass : InvariantOutcome.Fail, duplicated.Count == 0
                ? $"Replay run: {rerun.Kind}; no requisition already in SAP was created again ({rerun.Count(Procurement.Sync.Store.RunEventKind.Created)} new creation(s) of records still pending)."
                : $"Created again on replay: {string.Join(", ", duplicated)}");
        }

        if (wanted.Contains("INV-08") || wanted.Contains("INV-09"))
        {
            foreach (var id in SelfJudged.Where(wanted.Contains))
            {
                var mine = ctx.Result.Assertions.Where(a => a.Invariant == id).ToList();
                if (mine.Count == 0) Add(id, InvariantOutcome.NotApplicable, "Not exercised by this scenario.");
                else Add(id, mine.All(a => a.Passed) ? InvariantOutcome.Pass : InvariantOutcome.Fail,
                    mine.All(a => a.Passed) ? $"{mine.Count} server-side check(s) held." : string.Join("; ", mine.Where(a => !a.Passed).Select(a => a.Name)));
            }
        }

        if (wanted.Contains("INV-12"))
        {
            var foreign = lab.Capture.Exchanges.Where(e => !LabHosts.Contains(e.Host)).Select(e => $"{e.Host}{e.Url}").Distinct().ToList();
            Add("INV-12", foreign.Count == 0 ? InvariantOutcome.Pass : InvariantOutcome.Fail, foreign.Count == 0
                ? $"All {lab.Capture.Exchanges.Count} request(s) stayed inside the isolated lab (in-memory hosts, no network)."
                : $"Requests left the lab: {string.Join(", ", foreign.Take(5))}");
        }

        return results.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
    }

    private static async Task<List<RequisitionRecord>?> SafeRecordsAsync(LabEnvironment lab)
    {
        try
        {
            await using var db = lab.Db();
            return await db.Requisitions.AsNoTracking().ToListAsync();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Str(JsonObject d, string name) => d[name]?.GetValue<string>() ?? "";

    private static string? ReferenceOf(string? body)
    {
        if (body is null) return null;
        try
        {
            // SAP's JSON is PascalCase, but SAP (and the mock) match names case-insensitively, so must this check.
            return JsonNode.Parse(body) is JsonObject o
                ? o.FirstOrDefault(p => string.Equals(p.Key, "ExternalReference", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
