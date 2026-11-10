namespace Procurement.Contracts;

public enum QuarantineReason
{
    SchemaInvalid,
    MissingRequiredField,
    MissingLines,
    InvalidQuantity,
    InvalidPrice,
    UnknownCurrency,
    UnknownUnitOfMeasure,
    UnknownPlant,
    NeededByInPast,
    AmountNotPositive,
    TotalMismatch,
    /// <summary>SAP answered 4xx: valid for the source, not for SAP.</summary>
    SapRejectedPermanent,
    RetryExhausted,
    /// <summary>A content amendment arrived after SAP had released (frozen) the requisition.</summary>
    VersionConflictUnresolved,
    /// <summary>Cancelled or un-approved in the source after it was transferred.</summary>
    SourceChangedAfterTransfer,
    /// <summary>SAP holds several requisitions for this reference; linking to one would be a guess.</summary>
    AmbiguousSapMatch,
    /// <summary>The local link points at a SAP requisition that belongs to another source requisition.</summary>
    MappingIntegrityViolation,
    /// <summary>Different content under a version number already processed.</summary>
    ConflictingSourceContent,
    InvalidApproval,
}
