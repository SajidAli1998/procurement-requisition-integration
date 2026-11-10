using System.Text.Json.Serialization;

namespace Mocks.SapApi;

// Field names follow API_PURCHASEREQ_PROCESS_SRV, but the shape is simplified.
public sealed class SapRequisition
{
    public required string PurchaseRequisition { get; init; }
    public string PurchaseRequisitionType { get; init; } = "NB";
    public string? PurReqnDescription { get; set; }

    // Not unique, as in SAP.
    public required string ExternalReference { get; init; }
    public long ExternalVersion { get; set; }

    // N not edited, B purchase order created, L closed.
    public string ProcessingStatus { get; set; } = "N";

    // "" not in release, 01 release pending, 02 released, 08 rejected.
    public string ReleaseStatus { get; set; } = "";

    public string CreatedByUser { get; init; } = "INTEGRATION";
    public DateTime CreationDateTime { get; init; }
    public DateTime LastChangeDateTime { get; set; }

    [JsonPropertyName("to_PurchaseReqnItem")]
    public List<SapRequisitionItem> Items { get; set; } = [];

    [JsonIgnore] public long Version { get; set; } = 1;
    [JsonIgnore] public SapLifecycle Lifecycle { get; set; } = SapLifecycle.Created;

    [JsonPropertyName("@odata.etag")]
    public string ODataETag => Mocks.Shared.ETags.For(Version);
}

public sealed class SapRequisitionItem
{
    public string PurchaseRequisitionItem { get; set; } = "";
    public string? Material { get; set; }
    public string? PurchaseRequisitionItemText { get; set; }
    public string? MaterialGroup { get; set; }
    public decimal RequestedQuantity { get; set; }
    public string BaseUnit { get; set; } = "";
    public decimal PurchaseRequisitionPrice { get; set; }
    public string PurReqnItemCurrency { get; set; } = "";
    public string Plant { get; set; } = "";
    public string CompanyCode { get; set; } = "";
    public string PurchasingGroup { get; set; } = "";
    public string? TaxCode { get; set; }
    public DateOnly DeliveryDate { get; set; }
    public string? PurchaseOrder { get; set; }
}

// Clients only see this through the ProcessingStatus and ReleaseStatus codes.
public enum SapLifecycle { Created, InRelease, Released, PoCreated, Closed, Rejected }

public sealed record PurchaseRequisitionInput
{
    public string? PurchaseRequisitionType { get; init; }
    public string? PurReqnDescription { get; init; }
    public string? ExternalReference { get; init; }
    public long? ExternalVersion { get; init; }

    [JsonPropertyName("to_PurchaseReqnItem")]
    public List<PurchaseRequisitionItemInput>? Items { get; init; }
}

public sealed record PurchaseRequisitionItemInput
{
    public string? PurchaseRequisitionItem { get; init; }
    public string? Material { get; init; }
    public string? PurchaseRequisitionItemText { get; init; }
    public string? MaterialGroup { get; init; }
    public decimal? RequestedQuantity { get; init; }
    public string? BaseUnit { get; init; }
    public decimal? PurchaseRequisitionPrice { get; init; }
    public string? PurReqnItemCurrency { get; init; }
    public string? Plant { get; init; }
    public string? CompanyCode { get; init; }
    public string? PurchasingGroup { get; init; }
    public string? TaxCode { get; init; }
    public DateOnly? DeliveryDate { get; init; }
}
