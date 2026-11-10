namespace Procurement.Sync.Validation;

// Fixed, synthetic master data. Plant-to-company assignment is left to SAP on purpose: it is SAP's rule.
public static class ReferenceData
{
    public static readonly IReadOnlySet<string> Currencies = new HashSet<string>(StringComparer.Ordinal) { "USD", "EUR", "GBP", "AED", "SAR", "QAR" };
    public static readonly IReadOnlySet<string> Units = new HashSet<string>(StringComparer.Ordinal) { "EA", "BOX", "KG", "L", "M", "H" };
    // Half an "each" is a data error, not a quantity.
    public static readonly IReadOnlySet<string> WholeUnits = new HashSet<string>(StringComparer.Ordinal) { "EA", "BOX" };
    public static readonly IReadOnlySet<string> Plants = new HashSet<string>(StringComparer.Ordinal) { "1100", "1200", "2100", "2200", "3100" };
}
