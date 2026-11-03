namespace Procurement.Sync.Validation;

/// <summary>
/// Master data the integration validates against. In production this would be cached from MDM/SAP;
/// here it is fixed and synthetic. Plant-to-company assignment is deliberately NOT checked: that is SAP's rule.
/// </summary>
public static class ReferenceData
{
    public static readonly IReadOnlySet<string> Currencies = new HashSet<string>(StringComparer.Ordinal) { "USD", "EUR", "GBP", "AED", "SAR", "QAR" };
    public static readonly IReadOnlySet<string> Units = new HashSet<string>(StringComparer.Ordinal) { "EA", "BOX", "KG", "L", "M", "H" };
    public static readonly IReadOnlySet<string> Plants = new HashSet<string>(StringComparer.Ordinal) { "1100", "1200", "2100", "2200", "3100" };
}
