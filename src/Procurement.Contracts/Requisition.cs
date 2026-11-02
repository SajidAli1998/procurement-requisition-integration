namespace Procurement.Contracts;

/// <summary>Canonical, validated requisition: the integration's internal model, independent of either system's wire format.</summary>
public sealed record Requisition(
    string Id,
    long Version,
    string Requester,
    string CostCenter,
    string CompanyCode,
    string Currency,
    DateOnly NeededBy,
    string? Justification,
    DateTimeOffset? ApprovedAt,
    decimal TotalAmount,
    IReadOnlyList<RequisitionLine> Lines);

public sealed record RequisitionLine(
    int LineNo,
    string? MaterialCode,
    string Description,
    string? MaterialGroup,
    decimal Quantity,
    string Uom,
    decimal UnitPrice,
    string Plant);
