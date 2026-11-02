using Procurement.Contracts;
using Procurement.Sync.Clients;

namespace Procurement.Sync.Mapping;

public static class SapMapper
{
    public static SapRequisitionInput ToSap(Requisition r) => new()
    {
        PurReqnDescription = Truncate(r.Justification ?? r.Id, 40),
        ExternalReference = r.Id,
        ExternalVersion = r.Version,
        Items = r.Lines.Select(l => new SapItemInput
        {
            PurchaseRequisitionItem = l.LineNo.ToString("D5"),
            Material = l.MaterialCode,
            PurchaseRequisitionItemText = Truncate(l.Description, 40),
            MaterialGroup = l.MaterialGroup,
            RequestedQuantity = l.Quantity,
            BaseUnit = l.Uom,
            PurchaseRequisitionPrice = l.UnitPrice,
            PurReqnItemCurrency = r.Currency,
            Plant = l.Plant,
            CompanyCode = r.CompanyCode,
            DeliveryDate = r.NeededBy,
        }).ToList(),
    };

    /// <summary>An amendment replaces the description, version and items; the external reference never changes.</summary>
    public static SapRequisitionInput ToPatch(Requisition r) => ToSap(r) with { ExternalReference = null };

    /// <summary>SAP processing/release status codes → canonical status.</summary>
    public static ErpStatus ToErpStatus(SapRequisitionDto dto) => (dto.ProcessingStatus, dto.ReleaseStatus) switch
    {
        ("L", _) => ErpStatus.Closed,
        ("B", _) => ErpStatus.PoCreated,
        (_, "08") => ErpStatus.Rejected,
        (_, "02") => ErpStatus.Released,
        (_, "01") => ErpStatus.InRelease,
        _ => ErpStatus.Created,
    };

    /// <summary>SAP accepts content changes only before release.</summary>
    public static bool IsAmendable(ErpStatus status) => status is ErpStatus.Created or ErpStatus.InRelease;

    public static string? PurchaseOrderOf(SapRequisitionDto dto) => dto.Items.Select(i => i.PurchaseOrder).FirstOrDefault(p => p is not null);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
