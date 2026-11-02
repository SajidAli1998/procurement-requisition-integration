using System.Text.Json;
using System.Text.Json.Serialization;

namespace Procurement.Sync.Clients;

/// <summary>Wire format of the SAP purchase requisition service. Kept separate from the canonical model on purpose.</summary>
public sealed record SapRequisitionDto
{
    public string PurchaseRequisition { get; init; } = "";
    public string ExternalReference { get; init; } = "";
    public long ExternalVersion { get; init; }
    public string ProcessingStatus { get; init; } = "";
    public string ReleaseStatus { get; init; } = "";
    public DateTime LastChangeDateTime { get; init; }

    [JsonPropertyName("to_PurchaseReqnItem")]
    public List<SapItemDto> Items { get; init; } = [];

    [JsonPropertyName("@odata.etag")]
    public string? ETag { get; init; }
}

public sealed record SapItemDto
{
    public string PurchaseRequisitionItem { get; init; } = "";
    public string? PurchaseOrder { get; init; }
}

public sealed record SapRequisitionInput
{
    public string PurchaseRequisitionType { get; init; } = "NB";
    public string? PurReqnDescription { get; init; }
    public string? ExternalReference { get; init; }
    public long ExternalVersion { get; init; }

    [JsonPropertyName("to_PurchaseReqnItem")]
    public List<SapItemInput> Items { get; init; } = [];
}

public sealed record SapItemInput
{
    public required string PurchaseRequisitionItem { get; init; }
    public string? Material { get; init; }
    public string? PurchaseRequisitionItemText { get; init; }
    public string? MaterialGroup { get; init; }
    public decimal RequestedQuantity { get; init; }
    public required string BaseUnit { get; init; }
    public decimal PurchaseRequisitionPrice { get; init; }
    public required string PurReqnItemCurrency { get; init; }
    public required string Plant { get; init; }
    public required string CompanyCode { get; init; }
    public string PurchasingGroup { get; init; } = "001";
    public DateOnly DeliveryDate { get; init; }
}

internal static class SapJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
