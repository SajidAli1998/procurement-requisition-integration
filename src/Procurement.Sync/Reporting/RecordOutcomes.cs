using System.Globalization;
using Procurement.Sync.Store;

namespace Procurement.Sync.Reporting;

// Columns follow the client's Reconciliation_Expected sheet so the two can be compared row by row.
// Attempts counts SAP writes including retries; a record settled without a write (validated out, or found in SAP) counts 1.
public sealed record RecordOutcome(
    string Key, long? SourceVersion, string Result, string? SapNumber, int Attempts, string FailureCategory, string Explanation,
    string SapResponses);

public static class RecordOutcomes
{
    public static List<RecordOutcome> Build(IEnumerable<RunEvent> runEvents, IReadOnlyDictionary<string, RequisitionRecord> records) =>
        runEvents.GroupBy(e => e.SourceKey)
            .Select(g => Classify(g.Key, g.OrderBy(e => e.Id).ToList(), records.GetValueOrDefault(g.Key)))
            .OrderBy(o => o.Key, StringComparer.Ordinal)
            .ToList();

    private static RecordOutcome Classify(string key, List<RunEvent> events, RequisitionRecord? record)
    {
        bool Has(RunEventKind kind) => events.Any(e => e.Kind == kind);
        string? DetailOf(RunEventKind kind) => events.LastOrDefault(e => e.Kind == kind)?.Detail;

        var responses = events.Where(e => e.SapResponses is not null)
            .SelectMany(e => e.SapResponses!.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)))
            .ToList();
        var throttled = responses.Count(s => s == 429);
        var transient = responses.Count(s => s is 0 or >= 500);
        var refreshed = Has(RunEventKind.VersionRefreshed);
        var sap = record?.SapNumber;

        var result =
            Has(RunEventKind.Created) || Has(RunEventKind.InDoubtResolved) ? "Created"
            : Has(RunEventKind.AlreadyInSap) ? "Duplicate"
            : Has(RunEventKind.Amended) ? "Updated"
            : Has(RunEventKind.Quarantined) ? "Quarantined"
            : Has(RunEventKind.Ignored) ? "Skipped"
            : Has(RunEventKind.RetryScheduled) || Has(RunEventKind.InDoubt) ? "Pending"
            : Has(RunEventKind.StatusChanged) || Has(RunEventKind.WrittenBack) ? "StatusUpdated"
            : "Unchanged";

        var quarantine = DetailOf(RunEventKind.Quarantined);
        var quarantineReason = quarantine?.Split(':', 2)[0];
        var category = result switch
        {
            "Skipped" => "Not approved",
            "Quarantined" when quarantineReason is "SapRejectedPermanent" => "Rejected by SAP",
            "Quarantined" when quarantineReason is "RetryExhausted" => throttled > 0 ? "Rate limit" : "Transient service",
            "Quarantined" when quarantineReason is "VersionConflictUnresolved" || refreshed => "Version conflict",
            "Quarantined" => "Validation",
            _ when refreshed => "Version conflict",
            "Duplicate" => "Idempotency",
            _ when Has(RunEventKind.InDoubt) || Has(RunEventKind.InDoubtResolved) => "Lost response",
            _ when throttled > 0 => "Rate limit",
            _ when transient > 0 => "Transient service",
            _ => "None",
        };

        var attempts = responses.Count > 0 ? responses.Count : result is "Duplicate" or "Quarantined" ? 1 : 0;

        var explanation = result switch
        {
            "Created" => "Created" + Qualifiers(throttled, transient, refreshed, events) + (sap is null ? "" : $" as {sap}"),
            "Duplicate" => $"Already in SAP as {sap}: existing document linked and not created again",
            "Updated" => DetailOf(RunEventKind.Amended) ?? "Updated in SAP",
            "Quarantined" => quarantine?.Split(':', 2).ElementAtOrDefault(1)?.Trim() ?? "Quarantined",
            "Skipped" => $"Source approval status is {DetailOf(RunEventKind.Ignored)}",
            "Pending" => $"Waiting to retry: {DetailOf(RunEventKind.RetryScheduled) ?? DetailOf(RunEventKind.InDoubt)}",
            "StatusUpdated" => DetailOf(RunEventKind.StatusChanged) ?? "ERP status written back to the source",
            _ => "No change since the last run",
        };

        return new RecordOutcome(key, FirstExtractedVersion(events) ?? record?.SourceVersion, result, sap, attempts, category, explanation,
            string.Join(',', responses));
    }

    private static string Qualifiers(int throttled, int transient, bool refreshed, List<RunEvent> events)
    {
        var parts = new List<string>();
        if (refreshed)
        {
            var sent = FirstExtractedVersion(events);
            var staged = events.LastOrDefault(e => e.Kind == RunEventKind.Staged)?.Detail?.Split(' ')[1];
            parts.Add("after version refresh" + (sent is null || staged is null ? "" : $" (v{sent} → {staged})"));
        }
        if (throttled > 0) parts.Add($"after {Count(throttled)} rate-limit response{(throttled == 1 ? "" : "s")}");
        if (transient > 0) parts.Add($"after {Count(transient)} transient failure{(transient == 1 ? "" : "s")}");
        if (events.Any(e => e.Kind == RunEventKind.InDoubtResolved)) parts.Add("after a lost response was confirmed by lookup");
        return parts.Count == 0 ? " successfully" : " " + string.Join(" and ", parts);
    }

    private static string Count(int n) => n switch { 1 => "one", 2 => "two", 3 => "three", 4 => "four", _ => n.ToString(CultureInfo.InvariantCulture) };

    // Parses the Extracted detail, for example "v2 Approved".
    private static long? FirstExtractedVersion(List<RunEvent> events) =>
        events.FirstOrDefault(e => e.Kind == RunEventKind.Extracted)?.Detail is { } d && d.StartsWith('v')
        && long.TryParse(d[1..].Split(' ')[0], CultureInfo.InvariantCulture, out var v) ? v : null;
}
