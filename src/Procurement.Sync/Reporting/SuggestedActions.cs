using Procurement.Contracts;

namespace Procurement.Sync.Reporting;

public static class SuggestedActions
{
    public static string For(QuarantineReason reason, string key) => reason switch
    {
        QuarantineReason.SchemaInvalid => "Correct the field named in the summary in the source system; the next run picks up the corrected version automatically.",
        QuarantineReason.MissingRequiredField => key.StartsWith("NOID-")
            ? "The source sent a requisition without an id. Raise with the source system owner; it cannot be matched or transferred."
            : "Fill in the missing field in the source system; the next run picks it up automatically.",
        QuarantineReason.MissingLines => "Add at least one line to the requisition in the source system.",
        QuarantineReason.InvalidQuantity => "Correct the line quantity (must be greater than zero) in the source system.",
        QuarantineReason.InvalidPrice => "Correct the line price (must not be negative) in the source system.",
        QuarantineReason.UnknownCurrency => "Use a supported ISO 4217 currency, or add the currency to the integration's reference data if it is genuinely new.",
        QuarantineReason.UnknownUnitOfMeasure => "Use a supported unit of measure, or add it to the reference data if it is genuinely new.",
        QuarantineReason.UnknownPlant => "Use a valid plant code, or add the plant to the reference data.",
        QuarantineReason.NeededByInPast => "Move the needed-by date to today or later in the source system.",
        QuarantineReason.AmountNotPositive => "Enter the approved total amount (greater than zero) in the source system; the next run picks it up.",
        QuarantineReason.TotalMismatch => "Recalculate the header total so it equals the sum of the lines.",
        QuarantineReason.SapRejectedPermanent => $"Correct the data SAP rejected (see summary) in the source, or fix the SAP configuration, then run: procsync quarantine replay {key}",
        QuarantineReason.RetryExhausted => $"SAP was unavailable for too long. Check SAP health, then run: procsync quarantine replay {key}",
        QuarantineReason.VersionConflictUnresolved => $"SAP holds a different version and would not accept this one (see summary). Apply the change in SAP or raise a new requisition, then run: procsync quarantine resolve {key} --note \"...\"",
        QuarantineReason.SourceChangedAfterTransfer => $"The requisition was cancelled or un-approved after it reached SAP. Cancel or close the SAP requisition if it is no longer needed, then run: procsync quarantine resolve {key} --note \"...\"",
        QuarantineReason.AmbiguousSapMatch => $"SAP holds several requisitions for this reference (see summary). Cancel the extra ones in SAP so one remains, then run: procsync quarantine replay {key}",
        QuarantineReason.MappingIntegrityViolation => $"The integration's link points at a SAP requisition for another reference (see summary). Check both SAP documents, then run: procsync quarantine replay {key}",
        QuarantineReason.InvalidApproval => "The approval timestamp is not credible (see summary). Check the approval in the source system; the next run picks up the corrected version.",
        QuarantineReason.ConflictingSourceContent => $"The source changed this requisition's content without changing its version. Raise with the source system owner; once it has a new version, run: procsync quarantine replay {key}",
        _ => "Investigate the details and the raw payload.",
    };
}
