using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Procurement.AssistantApi.Model;

[Description("A purchase requisition the assistant raises on behalf of the signed-in user. It is created awaiting human approval.")]
public sealed record CreateRequisitionRequest
{
    [Description("Cost center to charge, from GET /v1/reference/cost-centers.")]
    public required string CostCenter { get; init; }

    [Description("Receiving plant, from GET /v1/reference/plants. Must belong to the cost center's company.")]
    public required string Plant { get; init; }

    [Description("Date the goods or services are needed (yyyy-MM-dd), between tomorrow and one year ahead.")]
    public required DateOnly NeededBy { get; init; }

    [Description("Business reason in the user's words, 10-500 characters.")]
    [StringLength(500, MinimumLength = 10)]
    public required string Justification { get; init; }

    [Description("1-20 lines. Prices are taken from the catalogue; do not send prices.")]
    [MinLength(1), MaxLength(20)]
    public required IReadOnlyList<RequestLine> Lines { get; init; }

    [Description("Optional reference to the assistant conversation, stored for audit.")]
    [StringLength(200)]
    public string? AssistantSessionRef { get; init; }
}

public sealed record RequestLine
{
    [Description("Material or service code from GET /v1/reference/materials.")]
    public required string MaterialCode { get; init; }

    [Description("Quantity in the material's unit of measure, greater than 0 and at most 1000. Whole numbers for EA and BOX.")]
    [Range(0.001, 1000)]
    public required decimal Quantity { get; init; }
}

public sealed record ValidationIssue(
    [property: Description("JSON path of the field, e.g. lines[0].quantity")] string Field,
    [property: Description("Stable machine-readable code")] string Code,
    string Message,
    [property: Description("How to fix it")] string? Suggestion = null);

public sealed record ValidationResult(
    bool Valid,
    [property: Description("Total at catalogue prices, when all lines are valid")] decimal? EstimatedTotal,
    string? Currency,
    IReadOnlyList<ValidationIssue> Issues);

/// <summary>Closed set of statuses the assistant can report to the user.</summary>
public enum RequestStatus
{
    PendingApproval,
    Approved,
    TransferredToErp,
    ReleasedInErp,
    PurchaseOrderCreated,
    Closed,
    RejectedByApprover,
    RejectedInErp,
    Withdrawn,
    NeedsAttention,
}

public sealed record RequisitionView(
    string Id,
    RequestStatus Status,
    [property: Description("Plain-language explanation of the status, safe to show the user")] string StatusReason,
    [property: Description("What happens next and whether anyone needs to act")] string NextExpectedStep,
    [property: Description("SAP purchase requisition number once transferred")] string? ErpReference,
    string? PurchaseOrder,
    string? CostCenter,
    string? Currency,
    decimal? TotalAmount,
    DateOnly? NeededBy,
    string? Justification,
    IReadOnlyList<ViewLine> Lines,
    DateTime LastUpdated,
    IReadOnlyList<TimelineEntry> Timeline);

public sealed record ViewLine(int LineNo, string? MaterialCode, string? Description, decimal? Quantity, string? Uom, decimal? UnitPrice);

public sealed record TimelineEntry(DateTime At, string Event);

public sealed record RequisitionPage(IReadOnlyList<RequisitionView> Items, [property: Description("Pass as cursor to get the next page; null when done")] string? NextCursor);
