using Procurement.Scenarios.Engine;

namespace Procurement.Scenarios.Catalogue;

public static class ScenarioCatalogue
{
    private static readonly Lazy<IReadOnlyList<ScenarioDefinition>> Lazy = new(Build);

    public static IReadOnlyList<ScenarioDefinition> All => Lazy.Value;

    private static IReadOnlyList<ScenarioDefinition> Build()
    {
        var c = new CatalogueBuilder();
        BaselineAndValid.Register(c);
        Duplicates.Register(c);
        FieldsAndFormats.Register(c);
        Approval.Register(c);
        Failures.Register(c);
        StateAndConcurrency.Register(c);
        ImportScenarios.Register(c);
        SecurityAndAssistant.Register(c);
        PerformanceAndUx.Register(c);
        Advanced.Register(c);
        var ids = c.All.GroupBy(s => s.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (ids.Count > 0) throw new InvalidOperationException("Duplicate scenario ids: " + string.Join(", ", ids));
        return c.All;
    }

    public static ScenarioDefinition? Find(string id) => All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Ids accept a trailing * (e.g. DUP-*); all filters combine.</summary>
    public static IReadOnlyList<ScenarioDefinition> Select(IEnumerable<string>? ids = null, string? category = null, Mode? mode = null,
        Priority? priority = null, string? search = null)
    {
        var patterns = ids?.Where(i => !string.IsNullOrWhiteSpace(i)).ToList() ?? [];
        return All.Where(s =>
                (patterns.Count == 0 || patterns.Any(p => p.EndsWith('*')
                    ? s.Id.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(s.Id, p, StringComparison.OrdinalIgnoreCase)))
                && (category is null || s.Category.Contains(category, StringComparison.OrdinalIgnoreCase))
                && (mode is null || s.Modes.Contains(mode.Value))
                && (priority is null || s.Priority == priority)
                && (search is null || s.Title.Contains(search, StringComparison.OrdinalIgnoreCase) || s.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || s.Description.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
