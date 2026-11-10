using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Procurement.Sync.Reporting;

public sealed class ReportWriter(IOptions<SyncOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // Returns the path of the HTML report.
    public async Task<string> WriteAsync(ReconciliationReport report, CancellationToken ct)
    {
        var directory = Path.GetFullPath(options.Value.ReportDirectory);
        Directory.CreateDirectory(directory);
        var files = new Dictionary<string, string>
        {
            ["json"] = JsonSerializer.Serialize(report, Json),
            ["md"] = Markdown(report),
            ["html"] = Html(report),
            ["csv"] = Csv(report),
        };
        foreach (var (extension, content) in files)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, $"{report.RunId}.{extension}"), content, ct);
            await File.WriteAllTextAsync(Path.Combine(directory, $"latest.{extension}"), content, ct);
        }
        return Path.Combine(directory, $"{report.RunId}.html");
    }

    private static string Cell(string? s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ");

    public static string Markdown(ReconciliationReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Reconciliation report: {r.RunId}");
        sb.AppendLine();
        sb.AppendLine($"**Status:** {r.Status}{(r.Message is null ? "" : $" ({r.Message})")}  ");
        sb.AppendLine($"**Trigger:** {r.Trigger} · **Started:** {r.StartedAt:u} · **Generated:** {r.GeneratedAt:u}");
        sb.AppendLine();

        sb.AppendLine("## Invariants");
        sb.AppendLine();
        sb.AppendLine("| Result | Check | Detail |");
        sb.AppendLine("|---|---|---|");
        foreach (var i in r.Invariants) sb.AppendLine($"| {i.Outcome} | {i.Name} | {Cell(i.Detail)} |");
        sb.AppendLine();

        sb.AppendLine("## This run");
        sb.AppendLine();
        sb.AppendLine("| Event | Count |");
        sb.AppendLine("|---|---:|");
        foreach (var (k, v) in r.RunCounts) sb.AppendLine($"| {k} | {v} |");
        foreach (var (k, v) in r.Transport) sb.AppendLine($"| {k} | {v} |");
        sb.AppendLine($"| retry wait (s) | {r.RetryWaitSeconds} |");
        sb.AppendLine();

        sb.AppendLine("## Where everything stands");
        sb.AppendLine();
        sb.AppendLine("| State | Requisitions |");
        sb.AppendLine("|---|---:|");
        foreach (var (k, v) in r.StateTotals) sb.AppendLine($"| {k} | {v} |");
        sb.AppendLine();

        if (r.Totals.Count > 0)
        {
            sb.AppendLine("## Amounts (source vs SAP)");
            sb.AppendLine();
            sb.AppendLine("| Currency | Source count | Source amount | SAP count | SAP amount | Match |");
            sb.AppendLine("|---|---:|---:|---:|---:|---|");
            foreach (var t in r.Totals)
                sb.AppendLine($"| {t.Currency} | {t.LocalCount} | {t.LocalAmount:N2} | {t.SapCount} | {t.SapAmount:N2} | {(t.Matches ? "Yes" : "No")} |");
            if (r.ExcludedFromTotals > 0) sb.AppendLine($"\n_{r.ExcludedFromTotals} requisition(s) excluded: SAP holds an earlier version._");
            sb.AppendLine();
        }

        sb.AppendLine($"## Open failures ({r.OpenFailures.Count})");
        sb.AppendLine();
        if (r.OpenFailures.Count == 0)
        {
            sb.AppendLine("None.");
        }
        else
        {
            sb.AppendLine("| Requisition | Ver | Stage | Reason | What is wrong | What to do | Seen |");
            sb.AppendLine("|---|---:|---|---|---|---|---:|");
            foreach (var f in r.OpenFailures)
                sb.AppendLine($"| {f.Key} | {f.Version} | {f.Stage} | {f.Reason} | {Cell(f.Summary)} | {Cell(f.SuggestedAction)} | {f.Occurrences} |");
        }
        sb.AppendLine();

        sb.AppendLine($"## Notable events this run ({r.NotableChanges.Count})");
        sb.AppendLine();
        if (r.NotableChanges.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| Requisition | Event | Detail |");
            sb.AppendLine("|---|---|---|");
            foreach (var c in r.NotableChanges) sb.AppendLine($"| {c.Key} | {c.Kind} | {Cell(c.Detail)} |");
        }
        sb.AppendLine();

        sb.AppendLine($"## Per requisition ({r.Records.Count})");
        sb.AppendLine();
        if (r.Records.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine(string.Join(" · ", r.Outcomes.Select(o => $"**{o.Value}** {o.Key}")));
            sb.AppendLine();
            sb.AppendLine("| Requisition | Ver | Result | SAP document | Attempts | Failure category | Explanation |");
            sb.AppendLine("|---|---:|---|---|---:|---|---|");
            foreach (var o in r.Records)
                sb.AppendLine($"| {o.Key} | {o.SourceVersion} | {o.Result} | {o.SapNumber} | {o.Attempts} | {o.FailureCategory} | {Cell(o.Explanation)} |");
        }
        sb.AppendLine();

        sb.AppendLine($"## Waiting for retry ({r.Pending.Count})");
        sb.AppendLine();
        if (r.Pending.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| Requisition | State | Attempts | Next attempt | Last error |");
            sb.AppendLine("|---|---|---:|---|---|");
            foreach (var p in r.Pending) sb.AppendLine($"| {p.Key} | {p.State} | {p.Attempts} | {p.NextAttemptAt:u} | {Cell(p.LastError)} |");
        }
        return sb.ToString();
    }

    // Same columns as the client's Reconciliation_Expected sheet.
    public static string Csv(ReconciliationReport r)
    {
        static string Q(object? v)
        {
            var s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            return s.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        }
        var sb = new StringBuilder();
        sb.AppendLine("Source_Requisition_ID,Source_Version,Processing_Result,Remote_Document_ID,Attempts,Failure_Category,Explanation,SAP_Responses");
        foreach (var o in r.Records)
            sb.AppendLine(string.Join(',', Q(o.Key), Q(o.SourceVersion), Q(o.Result), Q(o.SapNumber), Q(o.Attempts), Q(o.FailureCategory), Q(o.Explanation), Q(o.SapResponses)));
        return sb.ToString();
    }

    private const string Css = """
        :root{--bg:#f7f7f5;--card:#fff;--text:#1d1d1b;--muted:#6b6b66;--line:#e4e4df;--ok:#1f7a4d;--warn:#a15c00;--bad:#b42318;--accent:#2f5bea}
        @media (prefers-color-scheme:dark){:root{--bg:#141413;--card:#1e1e1c;--text:#ececea;--muted:#a3a39d;--line:#33332f;--ok:#4cc38a;--warn:#f0a43a;--bad:#f97066;--accent:#7c9bff}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 system-ui,-apple-system,Segoe UI,sans-serif}
        main{max-width:1200px;margin:0 auto;padding:24px 16px 64px}h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:32px 0 10px}
        .meta{color:var(--muted)}.status{display:inline-block;padding:2px 10px;border-radius:999px;font-weight:600;border:1px solid var(--line)}
        .cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(140px,1fr));gap:12px;margin-top:20px}
        .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}.card b{display:block;font-size:24px}
        .card span{color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.04em}
        .wrap{overflow-x:auto;background:var(--card);border:1px solid var(--line);border-radius:10px}
        table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:8px 10px;border-bottom:1px solid var(--line);vertical-align:top}
        th{font-size:12px;color:var(--muted);font-weight:600}tr:last-child td{border-bottom:0}td.num,th.num{text-align:right;font-variant-numeric:tabular-nums}
        .Pass{color:var(--ok)}.Warn{color:var(--warn)}.Fail{color:var(--bad)}.Skipped{color:var(--muted)}code{font-size:12px}
        .empty{color:var(--muted);padding:12px}
        """;

    private static string E(object? s) => WebUtility.HtmlEncode(s?.ToString() ?? "");

    public static string Html(ReconciliationReport r)
    {
        var sb = new StringBuilder();
        int Count(string kind) => r.RunCounts.GetValueOrDefault(kind);
        var failed = r.Invariants.Count(i => i.Outcome == InvariantOutcome.Fail);

        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Reconciliation {{E(r.RunId)}}</title>
            <style>{{Css}}</style></head><body><main>
            <h1>Reconciliation report</h1>
            <div class="meta">{{E(r.RunId)}} · {{E(r.Trigger)}} · started {{r.StartedAt:u}} · generated {{r.GeneratedAt:u}}</div>
            <p><span class="status">{{E(r.Status)}}</span> {{E(r.Message)}}</p>
            <div class="cards">
              <div class="card"><span>Read</span><b>{{Count("Extracted")}}</b></div>
              <div class="card"><span>Created in SAP</span><b>{{Count("Created")}}</b></div>
              <div class="card"><span>Amended</span><b>{{Count("Amended")}}</b></div>
              <div class="card"><span>Quarantined</span><b>{{Count("Quarantined")}}</b></div>
              <div class="card"><span>Released from quarantine</span><b>{{Count("QuarantineResolved")}}</b></div>
              <div class="card"><span>Status updates</span><b>{{Count("StatusChanged")}}</b></div>
              <div class="card"><span>Open failures</span><b>{{r.OpenFailures.Count}}</b></div>
              <div class="card"><span>Invariants failed</span><b class="{{(failed == 0 ? "Pass" : "Fail")}}">{{failed}}</b></div>
            </div>
            """);

        sb.Append("<h2>Invariants</h2><div class=\"wrap\"><table><tr><th>Result</th><th>Check</th><th>Detail</th></tr>");
        foreach (var i in r.Invariants)
            sb.Append($"<tr><td class=\"{i.Outcome}\">{i.Outcome}</td><td>{E(i.Name)}</td><td>{E(i.Detail)}</td></tr>");
        sb.Append("</table></div>");

        if (r.Totals.Count > 0)
        {
            sb.Append("<h2>Amounts: source vs SAP</h2><div class=\"wrap\"><table><tr><th>Currency</th><th class=\"num\">Source count</th><th class=\"num\">Source amount</th><th class=\"num\">SAP count</th><th class=\"num\">SAP amount</th><th>Match</th></tr>");
            foreach (var t in r.Totals)
                sb.Append($"<tr><td>{E(t.Currency)}</td><td class=\"num\">{t.LocalCount}</td><td class=\"num\">{t.LocalAmount:N2}</td><td class=\"num\">{t.SapCount}</td><td class=\"num\">{t.SapAmount:N2}</td><td class=\"{(t.Matches ? "Pass" : "Fail")}\">{(t.Matches ? "Yes" : "No")}</td></tr>");
            sb.Append("</table></div>");
        }

        sb.Append($"<h2>Open failures ({r.OpenFailures.Count})</h2><div class=\"wrap\">");
        if (r.OpenFailures.Count == 0) sb.Append("<div class=\"empty\">None.</div>");
        else
        {
            sb.Append("<table><tr><th>Requisition</th><th class=\"num\">Ver</th><th>Stage</th><th>Reason</th><th>What is wrong</th><th>What to do</th><th class=\"num\">Seen</th></tr>");
            foreach (var f in r.OpenFailures)
                sb.Append($"<tr><td><code>{E(f.Key)}</code></td><td class=\"num\">{f.Version}</td><td>{E(f.Stage)}</td><td>{E(f.Reason)}</td><td>{E(f.Summary)}</td><td>{E(f.SuggestedAction)}</td><td class=\"num\">{f.Occurrences}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("</div>");

        sb.Append("<h2>This run</h2><div class=\"wrap\"><table><tr><th>Event</th><th class=\"num\">Count</th></tr>");
        foreach (var (k, v) in r.RunCounts) sb.Append($"<tr><td>{E(k)}</td><td class=\"num\">{v}</td></tr>");
        foreach (var (k, v) in r.Transport) sb.Append($"<tr><td>{E(k)}</td><td class=\"num\">{v}</td></tr>");
        sb.Append($"<tr><td>retry wait (s)</td><td class=\"num\">{r.RetryWaitSeconds}</td></tr></table></div>");

        sb.Append("<h2>Where everything stands</h2><div class=\"wrap\"><table><tr><th>State</th><th class=\"num\">Requisitions</th></tr>");
        foreach (var (k, v) in r.StateTotals) sb.Append($"<tr><td>{E(k)}</td><td class=\"num\">{v}</td></tr>");
        sb.Append("</table></div>");

        sb.Append($"<h2>Notable events ({r.NotableChanges.Count})</h2><div class=\"wrap\">");
        if (r.NotableChanges.Count == 0) sb.Append("<div class=\"empty\">None.</div>");
        else
        {
            sb.Append("<table><tr><th>Requisition</th><th>Event</th><th>Detail</th></tr>");
            foreach (var c in r.NotableChanges) sb.Append($"<tr><td><code>{E(c.Key)}</code></td><td>{E(c.Kind)}</td><td>{E(c.Detail)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("</div>");

        sb.Append($"<h2>Per requisition ({r.Records.Count})</h2>");
        if (r.Records.Count > 0) sb.Append($"<p class=\"meta\">{E(string.Join(" · ", r.Outcomes.Select(o => $"{o.Value} {o.Key}")))} · also in <code>{E(r.RunId)}.csv</code></p>");
        sb.Append("<div class=\"wrap\">");
        if (r.Records.Count == 0) sb.Append("<div class=\"empty\">None.</div>");
        else
        {
            sb.Append("<table><tr><th>Requisition</th><th class=\"num\">Ver</th><th>Result</th><th>SAP document</th><th class=\"num\">Attempts</th><th>Failure category</th><th>Explanation</th></tr>");
            foreach (var o in r.Records)
                sb.Append($"<tr><td><code>{E(o.Key)}</code></td><td class=\"num\">{o.SourceVersion}</td><td>{E(o.Result)}</td><td>{E(o.SapNumber)}</td><td class=\"num\">{o.Attempts}</td><td>{E(o.FailureCategory)}</td><td>{E(o.Explanation)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("</div>");

        sb.Append($"<h2>Waiting for retry ({r.Pending.Count})</h2><div class=\"wrap\">");
        if (r.Pending.Count == 0) sb.Append("<div class=\"empty\">None.</div>");
        else
        {
            sb.Append("<table><tr><th>Requisition</th><th>State</th><th class=\"num\">Attempts</th><th>Next attempt</th><th>Last error</th></tr>");
            foreach (var p in r.Pending) sb.Append($"<tr><td><code>{E(p.Key)}</code></td><td>{E(p.State)}</td><td class=\"num\">{p.Attempts}</td><td>{p.NextAttemptAt:u}</td><td>{E(p.LastError)}</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("</div></main></body></html>");
        return sb.ToString();
    }
}
