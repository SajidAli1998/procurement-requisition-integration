using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Procurement.Contracts;
using Procurement.Scenarios.Engine;
using Procurement.Sync.Reporting;
using InvariantOutcome = Procurement.Scenarios.Engine.InvariantOutcome;
using Procurement.Sync.Store;
using Procurement.SyntheticData;

namespace Procurement.TestLab;

// Expected values from the client's workbook: shown beside actual results, never as them.
public static class Baseline
{
    public static object Expected() => new
    {
        source = "Sajid_Procurement_Integration_Synthetic_Data_Expanded.xlsx (Reconciliation_Expected, AI_API_Contract_Tests) and the existing test suite",
        metrics = new[]
        {
            new { metric = "Total", expected = "120" }, new { metric = "Created", expected = "58" }, new { metric = "Existing duplicates linked", expected = "7" },
            new { metric = "Quarantined", expected = "50" }, new { metric = "Skipped", expected = "5" }, new { metric = "Newly created duplicates", expected = "0" },
            new { metric = "Reconciliation matches", expected = "120/120" }, new { metric = "AI contract tests", expected = "60/60" },
            new { metric = "Existing automated tests", expected = "106" },
        },
    };
}

public static class Dashboard
{
    public static async Task<object> BuildAsync(Workspace ws)
    {
        var lab = ws.Lab;
        await using var db = lab.Db();
        await db.Database.EnsureCreatedAsync();
        var states = await db.Requisitions.AsNoTracking().GroupBy(r => r.State).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        int S(RecordState s) => states.FirstOrDefault(x => x.Key == s)?.Count ?? 0;
        var events = await db.RunEvents.AsNoTracking().GroupBy(e => e.Kind).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        int E(RunEventKind k) => events.FirstOrDefault(x => x.Key == k)?.Count ?? 0;
        var openQuarantine = await db.Quarantine.AsNoTracking().Where(q => q.ResolvedAt == null).ToListAsync();
        var source = await lab.SourceSnapshotAsync();
        var sap = await lab.SapDocumentsAsync();
        var duplicates = sap.GroupBy(d => d["ExternalReference"]?.GetValue<string>()).Count(g => g.Count() > 1);
        var linkedOnly = (await db.RunEvents.AsNoTracking().Where(e => e.Kind == RunEventKind.AlreadyInSap || e.Kind == RunEventKind.InDoubtResolved || e.Kind == RunEventKind.Created)
            .Select(e => new { e.SourceKey, e.Kind }).ToListAsync()).GroupBy(e => e.SourceKey).Count(g => g.All(e => e.Kind != RunEventKind.Created));
        var created = await db.RunEvents.AsNoTracking().Where(e => e.Kind == RunEventKind.Created).Select(e => e.SourceKey).Distinct().CountAsync();

        var sapCalls = lab.Capture.Exchanges.Where(e => e.Channel.EndsWith("→sap", StringComparison.Ordinal)).ToList();
        var ok = sapCalls.Count(e => e.Status is >= 200 and < 300);
        var latencies = sapCalls.Where(e => e.Status is not null).Select(e => e.DurationMs).Order().ToList();
        double P(double q) => latencies.Count == 0 ? 0 : Math.Round(latencies[(int)Math.Min(latencies.Count - 1, Math.Floor(q * latencies.Count))], 1);
        var runs = ws.Runs.Where(r => r.State == "Finished").ToList();
        var lastReport = runs.LastOrDefault(r => r.Report is not null)?.Report;
        var breakerOpened = runs.Sum(r => (int)(r.Transport?.GetValueOrDefault("sap.circuitOpened") ?? 0));
        var lastRun = runs.LastOrDefault();
        var breaker = lastRun?.Message?.Contains("circuit breaker", StringComparison.OrdinalIgnoreCase) == true ? "Open (last run stopped by the breaker)" : "Closed";

        return new
        {
            dataset = ws.Dataset,
            totals = new
            {
                total = source.Count,
                approved = source.Count(s => s?["status"]?.GetValue<string>() == "Approved"),
                created,
                linked = linkedOnly,
                skipped = S(RecordState.Ignored),
                quarantined = S(RecordState.Quarantined),
                pending = S(RecordState.Queued) + S(RecordState.RetryPending) + S(RecordState.InDoubt) + S(RecordState.Sending),
                retrying = S(RecordState.RetryPending),
                unresolved = S(RecordState.InDoubt) + S(RecordState.Sending),
                needsIntervention = openQuarantine.Count(q => q.Reason is QuarantineReason.RetryExhausted or QuarantineReason.SapRejectedPermanent
                    or QuarantineReason.VersionConflictUnresolved or QuarantineReason.SourceChangedAfterTransfer or QuarantineReason.AmbiguousSapMatch
                    or QuarantineReason.MappingIntegrityViolation or QuarantineReason.ConflictingSourceContent),
                synced = S(RecordState.Synced),
                duplicateReferencesInSap = duplicates,
                sapDocuments = sap.Count,
            },
            health = new
            {
                status = duplicates > 0 ? "Attention: duplicate in SAP" : breaker.StartsWith("Open") ? "Degraded: SAP unavailable" : openQuarantine.Count > 0 ? "Healthy with quarantined records" : "Healthy",
                breaker,
                breakerOpenedCount = breakerOpened,
                averageRunMs = runs.Count == 0 ? 0 : Math.Round(runs.Average(r => r.DurationMs ?? 0), 0),
                sapCalls = sapCalls.Count,
                sapSuccessRate = sapCalls.Count == 0 ? (double?)null : Math.Round(100.0 * ok / sapCalls.Count, 1),
                retries = E(RunEventKind.RetryScheduled) + runs.Sum(r => (int)(r.Transport?.Where(t => t.Key.EndsWith(".retries", StringComparison.Ordinal)).Sum(t => t.Value) ?? 0)),
                latencyMs = new { p50 = P(0.5), p95 = P(0.95), p99 = P(0.99) },
            },
            reconciliation = lastReport?.Invariants.Select(i => new { i.Name, outcome = i.Outcome.ToString(), i.Detail }),
            outcomes = new[]
            {
                new { name = "Created", value = created }, new { name = "Linked", value = linkedOnly }, new { name = "Quarantined", value = S(RecordState.Quarantined) },
                new { name = "Skipped", value = S(RecordState.Ignored) }, new { name = "Pending", value = S(RecordState.Queued) + S(RecordState.RetryPending) + S(RecordState.InDoubt) },
            },
            errorTypes = openQuarantine.GroupBy(q => q.Reason.ToString()).Select(g => new { name = g.Key, value = g.Count() }).OrderByDescending(x => x.value),
            sapResponses = sapCalls.GroupBy(e => e.Status?.ToString(CultureInfo.InvariantCulture) ?? "no response").Select(g => new { name = g.Key, value = g.Count() }).OrderBy(x => x.name),
            runs = runs.TakeLast(30).Select(r => new
            {
                r.Id, r.Kind, r.StartedAt, r.DurationMs, r.Outcome,
                created = r.Events?.GetValueOrDefault("Created") ?? 0, linked = r.Events?.GetValueOrDefault("AlreadyInSap") ?? 0,
                quarantined = r.Events?.GetValueOrDefault("Quarantined") ?? 0, retries = r.Events?.GetValueOrDefault("RetryScheduled") ?? 0,
                throughput = r.DurationMs is > 0 ? Math.Round(((r.Events?.GetValueOrDefault("Created") ?? 0) + (r.Events?.GetValueOrDefault("AlreadyInSap") ?? 0)) / (r.DurationMs.Value / 1000.0), 1) : 0,
            }),
            scenarioHistory = Cli.Store().History().Take(20).Reverse().Select(b => new { b.Id, b.Name, b.StartedAt, b.Tally }),
            baseline = Baseline.Expected(),
        };
    }
}

public static class QuarantineView
{
    public static async Task<object> BuildAsync(Workspace ws)
    {
        await using var db = ws.Lab.Db();
        await db.Database.EnsureCreatedAsync();
        var entries = await db.Quarantine.AsNoTracking().OrderByDescending(q => q.LastSeenAt).ToListAsync();
        return entries.Select(q =>
        {
            JsonNode? raw = null;
            try { raw = q.RawPayload is null ? null : JsonNode.Parse(q.RawPayload); }
            catch (System.Text.Json.JsonException) { }
            var problems = new List<object>();
            try
            {
                if (q.Details is not null && JsonNode.Parse(q.Details) is JsonArray details)
                    foreach (var d in details)
                    {
                        var field = d?["field"]?.GetValue<string>() ?? "";
                        problems.Add(new { reason = d?["reason"]?.GetValue<string>(), field, message = d?["message"]?.GetValue<string>(), currentValue = Workspace.ValueAt(raw, field) });
                    }
            }
            catch (System.Text.Json.JsonException) { }
            return new
            {
                q.Id, q.SourceKey, q.SourceVersion, stage = q.Stage.ToString(), reason = q.Reason.ToString(), q.Summary, problems,
                severity = Severity(q.Reason), correctable = Correctable(q.Reason), correctableIn = CorrectableIn(q.Reason),
                attempts = q.Occurrences, q.FirstSeenAt, q.LastSeenAt, q.ResolvedAt, q.Resolution,
                suggestedAction = SuggestedActions.For(q.Reason, q.SourceKey), rawPayload = raw,
            };
        });
    }

    private static string Severity(QuarantineReason r) => r switch
    {
        QuarantineReason.MappingIntegrityViolation or QuarantineReason.AmbiguousSapMatch or QuarantineReason.ConflictingSourceContent => "Critical",
        QuarantineReason.SourceChangedAfterTransfer or QuarantineReason.VersionConflictUnresolved or QuarantineReason.RetryExhausted or QuarantineReason.SapRejectedPermanent => "High",
        _ => "Medium",
    };

    private static bool Correctable(QuarantineReason r) => r is not (QuarantineReason.SourceChangedAfterTransfer or QuarantineReason.VersionConflictUnresolved);

    private static string CorrectableIn(QuarantineReason r) => r switch
    {
        QuarantineReason.RetryExhausted => "Replay once SAP is healthy",
        QuarantineReason.SapRejectedPermanent => "Source data or SAP configuration, then replay",
        QuarantineReason.SourceChangedAfterTransfer or QuarantineReason.VersionConflictUnresolved => "SAP (manual), then resolve",
        QuarantineReason.AmbiguousSapMatch => "SAP (cancel extra documents), then replay",
        QuarantineReason.MappingIntegrityViolation => "Investigate both SAP documents, then replay",
        _ => "Source system (the next run picks it up)",
    };
}

public static class Reconciliation
{
    public static async Task<object> BuildAsync(Workspace ws)
    {
        var report = await ws.Lab.ReconcileAsync();
        // Latest meaningful outcome per requisition across every run, so the comparison does not depend on which run was last.
        var outcomes = new Dictionary<string, RecordOutcome>(StringComparer.Ordinal);
        foreach (var run in ws.Runs.Where(r => r.Report is not null))
            foreach (var o in run.Report!.Records.Where(o => o.Result is not ("Unchanged" or "StatusUpdated")))
                outcomes[o.Key] = o;

        object? comparison = null;
        if (ws.Dataset == "Workbook")
        {
            var rows = DataPack.Load().ReconciliationExpected.Select(e =>
            {
                outcomes.TryGetValue(e.RequisitionId, out var a);
                var diffs = new List<string>();
                if (a is null) diffs.Add("no outcome yet");
                else
                {
                    if (e.ProcessingResult != a.Result) diffs.Add("result");
                    if (e.RemoteDocumentId != a.SapNumber) diffs.Add("document");
                    if (e.Attempts != a.Attempts) diffs.Add("attempts");
                    if (e.FailureCategory != a.FailureCategory) diffs.Add("category");
                    if (e.SourceVersion != a.SourceVersion) diffs.Add("version");
                }
                return new
                {
                    id = e.RequisitionId,
                    expected = new { result = e.ProcessingResult, document = e.RemoteDocumentId, e.Attempts, category = e.FailureCategory, version = e.SourceVersion, e.Explanation },
                    actual = a is null ? null : new { result = a.Result, document = a.SapNumber, a.Attempts, category = a.FailureCategory, version = a.SourceVersion, a.Explanation, a.SapResponses },
                    matches = diffs.Count == 0,
                    differences = diffs,
                };
            }).ToList();
            comparison = new { matched = rows.Count(r => r.matches), total = rows.Count, rows };
        }

        return new
        {
            report.GeneratedAt, report.StateTotals, invariants = report.Invariants.Select(i => new { i.Name, outcome = i.Outcome.ToString(), i.Detail }),
            report.Totals, report.OpenFailures, outcomes = outcomes.Values.OrderBy(o => o.Key, StringComparer.Ordinal), comparison,
        };
    }
}

public static class Exports
{
    public static string Csv(BatchResult b)
    {
        var sb = new StringBuilder("Scenario_ID,Category,Title,Priority,Status,Duration_ms,Assertions_passed,Assertions_failed,Invariants_failed,Expected,Actual\n");
        foreach (var r in b.Results)
            sb.AppendLine(string.Join(',', new object?[] { r.Id, r.Category, r.Title, r.Priority, r.Status, r.DurationMs, r.Assertions.Count(a => a.Passed), r.Assertions.Count(a => !a.Passed),
                r.Invariants.Count(i => i.Outcome == InvariantOutcome.Fail), r.Expected, r.Actual }.Select(Cell)));
        return sb.ToString();
    }

    // Prefixing ' stops a spreadsheet from running a cell as a formula (CSV injection).
    public static string Cell(object? value)
    {
        var s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    public static string Markdown(BatchResult b)
    {
        var t = b.Tally;
        var sb = new StringBuilder();
        sb.AppendLine($"# Scenario run: {b.Name}");
        sb.AppendLine();
        sb.AppendLine($"Batch `{b.Id}`, {b.StartedAt:yyyy-MM-dd HH:mm} UTC, {(b.FinishedAt - b.StartedAt)?.TotalSeconds:0.0}s. Build {b.Environment.GitCommit}, {b.Environment.Runtime}, {b.Environment.Os}.");
        sb.AppendLine();
        sb.AppendLine($"**{t.Total} scenarios: {t.Executed} executed, {t.Passed} passed, {t.Failed} failed, {t.Blocked} blocked, {t.NotImplemented} not implemented.**");
        sb.AppendLine();
        sb.AppendLine("| Category | Executed | Passed | Failed | Blocked |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var (cat, c) in b.ByCategory.OrderBy(kv => Array.IndexOf(Procurement.Scenarios.Catalogue.Categories.Ordered, kv.Key)))
            sb.AppendLine($"| {cat} | {c.Executed} | {c.Passed} | {c.Failed} | {c.Blocked} |");
        sb.AppendLine();
        sb.AppendLine("| ID | Scenario | Priority | Result | Actual |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var r in b.Results) sb.AppendLine($"| {r.Id} | {r.Title.Replace("|", "\\|")} | {r.Priority} | {r.Status} | {r.Actual.Replace("|", "\\|").Replace("\n", " ")} |");
        var failed = b.Results.Where(r => r.Status == ScenarioStatus.Fail).ToList();
        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Failures");
            foreach (var r in failed)
            {
                sb.AppendLine();
                sb.AppendLine($"### {r.Id} {r.Title}");
                foreach (var a in r.Assertions.Where(a => !a.Passed)) sb.AppendLine($"- ✗ {a.Name}: expected {a.Expected}, got {a.Actual}");
                foreach (var i in r.Invariants.Where(i => i.Outcome == InvariantOutcome.Fail)) sb.AppendLine($"- ✗ {i.Id} {i.Name}: {i.Detail}");
                if (r.Error is not null) sb.AppendLine($"- Error: {r.Error}");
            }
        }
        var blocked = b.Results.Where(r => r.Status == ScenarioStatus.Blocked).ToList();
        if (blocked.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Blocked (not executed, never counted as passed)");
            sb.AppendLine();
            foreach (var r in blocked) sb.AppendLine($"- **{r.Id}** {r.Title}: {r.BlockedReason}");
        }
        return sb.ToString();
    }

    public static string Html(BatchResult b)
    {
        var t = b.Tally;
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Scenario run</title><style>
body{font:14px/1.5 system-ui,sans-serif;margin:24px;color:#14213D;background:#F7F6F2}table{border-collapse:collapse;width:100%;background:#fff}td,th{border:1px solid #ddd;padding:6px 8px;text-align:left;vertical-align:top}
th{background:#ECEFF4}.Pass{color:#1d7a46;font-weight:600}.Fail{color:#b42318;font-weight:600}.Blocked{color:#a15c07;font-weight:600}.NotImplemented{color:#666}
.k{display:inline-block;margin-right:18px}.k b{font-size:22px;display:block}details{margin:4px 0}code{font-size:12px}
@media (prefers-color-scheme: dark){body{background:#0f172a;color:#e2e8f0}table{background:#111827}th{background:#1f2937}td,th{border-color:#334155}}</style></head><body>
""");
        sb.Append($"<h1>Scenario run: {E(b.Name)}</h1><p>Batch <code>{E(b.Id)}</code> · {b.StartedAt:yyyy-MM-dd HH:mm} UTC · build {E(b.Environment.GitCommit)} · {E(b.Environment.Runtime)}</p>");
        sb.Append($"<p><span class=k><b>{t.Total}</b>scenarios</span><span class=k><b>{t.Executed}</b>executed</span><span class='k Pass'><b>{t.Passed}</b>passed</span><span class='k Fail'><b>{t.Failed}</b>failed</span><span class='k Blocked'><b>{t.Blocked}</b>blocked</span><span class=k><b>{t.NotImplemented}</b>not implemented</span></p>");
        sb.Append("<table><tr><th>ID</th><th>Scenario</th><th>Priority</th><th>Result</th><th>Expected</th><th>Actual</th></tr>");
        foreach (var r in b.Results)
        {
            sb.Append($"<tr><td>{E(r.Id)}</td><td>{E(r.Title)}<details><summary>evidence</summary>");
            foreach (var a in r.Assertions) sb.Append($"<div>{(a.Passed ? "✓" : "✗")} {E(a.Name)}{(a.Passed ? "" : $" (expected {E(a.Expected)}, got {E(a.Actual)})")}</div>");
            foreach (var i in r.Invariants) sb.Append($"<div>{(i.Outcome == InvariantOutcome.Pass ? "✓" : i.Outcome == InvariantOutcome.Fail ? "✗" : "-")} {E(i.Id)} {E(i.Name)}: {E(i.Detail)}</div>");
            sb.Append($"</details></td><td>{r.Priority}</td><td class='{r.Status}'>{r.Status}</td><td>{E(r.Expected)}</td><td>{E(r.Actual)}</td></tr>");
        }
        sb.Append("</table></body></html>");
        return sb.ToString();
    }
}
