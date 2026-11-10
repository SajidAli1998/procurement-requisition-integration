using Procurement.Scenarios.Engine;
using Procurement.Scenarios.Lab;

namespace Procurement.Scenarios.Catalogue;

public static class Categories
{
    public const string Baseline = "Baseline (client workbook)";
    public const string Valid = "1. Valid requisitions";
    public const string Duplicates = "2. Duplicates & idempotency";
    public const string Fields = "3. Missing & null fields";
    public const string Formats = "4. Data types & formats";
    public const string Approval = "5. Approval & business rules";
    public const string Source = "6. Source system failures";
    public const string Sap = "7. SAP API failures";
    public const string Breaker = "8. Circuit breaker & retry";
    public const string Concurrency = "9. Concurrency & races";
    public const string Database = "10. Database & persistence";
    public const string Status = "11. Status synchronisation";
    public const string Import = "12. Excel & CSV import";
    public const string Security = "13. Security & authorization";
    public const string Assistant = "14. AI assistant contract";
    public const string Performance = "15. Performance, load & recovery";
    public const string Frontend = "16. Frontend & user experience";
    public const string Advanced = "17. Advanced enterprise scenarios";

    public static readonly string[] Ordered = [Baseline, Valid, Duplicates, Fields, Formats, Approval, Source, Sap, Breaker, Concurrency, Database, Status, Import, Security, Assistant, Performance, Frontend, Advanced];
}

public sealed class ScenarioBuilder(ScenarioDefinition d)
{
    public ScenarioDefinition Definition => d;

    public ScenarioBuilder Describe(string text) { d.Description = text; return this; }
    public ScenarioBuilder P(Priority p, string severity = "Major") { d.Priority = p; d.Severity = severity; return this; }
    public ScenarioBuilder Modes(params Mode[] modes) { d.Modes.AddRange(modes); return this; }
    public ScenarioBuilder Pre(string text) { d.Preconditions = text; return this; }
    public ScenarioBuilder Fixture(string text) { d.Fixture = text; return this; }
    public ScenarioBuilder Faults(string text) { d.Faults = text; return this; }
    public ScenarioBuilder Steps(params string[] steps) { d.Steps.AddRange(steps); return this; }
    public ScenarioBuilder Timeout(int seconds) { d.TimeoutSeconds = seconds; return this; }
    public ScenarioBuilder Lab(LabOptions options) { d.Lab = options; return this; }
    public ScenarioBuilder Sync(params (string Key, string? Value)[] settings)
    {
        var dict = new Dictionary<string, string?>(d.Lab.Sync);
        foreach (var (k, v) in settings) dict[k] = v;
        d.Lab = d.Lab with { Sync = dict };
        return this;
    }
    public ScenarioBuilder Covered(string where) { d.CoveredBy = where; return this; }
    public ScenarioBuilder Blocked(string reason) { d.BlockedReason = reason; return this; }

    public ScenarioBuilder Expect(string summary, string? http = null, string? source = null, string? sap = null, string? db = null, string? recon = null)
    {
        d.Expected = new Expectation { Summary = summary, Http = http, Source = source, Sap = sap, Database = db, Reconciliation = recon };
        return this;
    }

    /// <summary>Replaces the default invariant set.</summary>
    public ScenarioBuilder Inv(params string[] ids)
    {
        d.Invariants.Clear();
        foreach (var id in ids) d.Invariants.Add(id);
        return this;
    }

    public ScenarioBuilder Without(params string[] ids)
    {
        foreach (var id in ids) d.Invariants.Remove(id);
        return this;
    }

    public ScenarioBuilder Run(Func<ScenarioContext, Task> body) { d.Body = body; return this; }
}

public sealed class CatalogueBuilder
{
    private readonly List<ScenarioDefinition> _all = [];

    public IReadOnlyList<ScenarioDefinition> All => _all;

    /// <summary>The pipeline invariants apply by default.</summary>
    public ScenarioBuilder Add(string id, string category, string title, Priority priority = Priority.Medium, params Mode[] modes)
    {
        var d = new ScenarioDefinition { Id = id, Category = category, Title = title, Priority = priority };
        d.Modes.AddRange(modes);
        foreach (var inv in Invariants.Pipeline) d.Invariants.Add(inv);
        _all.Add(d);
        return new ScenarioBuilder(d);
    }

    /// <summary>Authorization, confirmation and isolation invariants apply instead of the pipeline ones.</summary>
    public ScenarioBuilder AddApi(string id, string category, string title, Priority priority = Priority.Medium, params Mode[] modes)
    {
        var b = Add(id, category, title, priority, modes);
        b.Inv(Invariants.Assistant);
        return b;
    }
}
