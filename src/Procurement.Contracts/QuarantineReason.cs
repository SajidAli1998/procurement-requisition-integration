namespace Procurement.Contracts;

/// <summary>Closed set of reasons a record is quarantined, so reports can be grouped and acted on.</summary>
public enum QuarantineReason
{
    /// <summary>Payload could not be read: wrong types, unparseable values, missing id.</summary>
    SchemaInvalid,
    MissingRequiredField,
    MissingLines,
    InvalidQuantity,
    InvalidPrice,
    UnknownCurrency,
    UnknownUnitOfMeasure,
    UnknownPlant,
    NeededByInPast,
    TotalMismatch,
    /// <summary>SAP answered 4xx: the data is valid for the source but not for SAP.</summary>
    SapRejectedPermanent,
    /// <summary>Transient failures persisted beyond the retry budget.</summary>
    RetryExhausted,
    /// <summary>A content amendment arrived after SAP had frozen the requisition (released).</summary>
    VersionConflictUnresolved,
    /// <summary>The source requisition was cancelled or un-approved after it was transferred.</summary>
    SourceChangedAfterTransfer,
}
