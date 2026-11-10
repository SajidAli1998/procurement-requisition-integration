using Procurement.Contracts;
using Procurement.Sync.Clients;

namespace Procurement.Sync.Mapping;

public static class SapMapper
{
    public const int TextLimit = 40;

    public static SapRequisitionInput ToSap(Requisition r) => new()
    {
        PurReqnDescription = Truncate(r.Justification ?? r.Id, TextLimit),
        ExternalReference = r.Id,
        ExternalVersion = r.Version,
        Items = r.Lines.Select(l => new SapItemInput
        {
            PurchaseRequisitionItem = l.LineNo.ToString("D5"),
            Material = l.MaterialCode,
            PurchaseRequisitionItemText = Truncate(l.Description, TextLimit),
            MaterialGroup = l.MaterialGroup,
            RequestedQuantity = l.Quantity,
            BaseUnit = l.Uom,
            PurchaseRequisitionPrice = l.UnitPrice,
            PurReqnItemCurrency = r.Currency,
            Plant = l.Plant,
            CompanyCode = r.CompanyCode,
            TaxCode = l.TaxCode,
            DeliveryDate = r.NeededBy,
        }).ToList(),
    };

    public static SapRequisitionInput ToPatch(Requisition r) => ToSap(r) with { ExternalReference = null };

    public static ErpStatus ToErpStatus(SapRequisitionDto dto) => (dto.ProcessingStatus, dto.ReleaseStatus) switch
    {
        ("L", _) => ErpStatus.Closed,
        ("B", _) => ErpStatus.PoCreated,
        (_, "08") => ErpStatus.Rejected,
        (_, "02") => ErpStatus.Released,
        (_, "01") => ErpStatus.InRelease,
        _ => ErpStatus.Created,
    };

    // Version numbers are not enough: our own status write-back bumps the source version without changing the content.
    public static bool SameContent(SapRequisitionDto sap, Requisition r)
    {
        var expected = ToSap(r);
        if (sap.PurReqnDescription != expected.PurReqnDescription || sap.Items.Count != expected.Items.Count) return false;
        return sap.Items.OrderBy(i => i.PurchaseRequisitionItem, StringComparer.Ordinal)
            .Zip(expected.Items.OrderBy(i => i.PurchaseRequisitionItem, StringComparer.Ordinal))
            .All(p => p.First.PurchaseRequisitionItem == p.Second.PurchaseRequisitionItem
                && p.First.Material == p.Second.Material
                && p.First.PurchaseRequisitionItemText == p.Second.PurchaseRequisitionItemText
                && p.First.MaterialGroup == p.Second.MaterialGroup
                && p.First.RequestedQuantity == p.Second.RequestedQuantity
                && p.First.BaseUnit == p.Second.BaseUnit
                && p.First.PurchaseRequisitionPrice == p.Second.PurchaseRequisitionPrice
                && p.First.PurReqnItemCurrency == p.Second.PurReqnItemCurrency
                && p.First.Plant == p.Second.Plant
                && p.First.CompanyCode == p.Second.CompanyCode
                && p.First.TaxCode == p.Second.TaxCode
                && p.First.DeliveryDate == p.Second.DeliveryDate);
    }

    // Unknown codes are reported rather than guessed.
    public static bool IsKnownStatus(SapRequisitionDto dto) =>
        dto.ProcessingStatus is "N" or "B" or "L" or "" && dto.ReleaseStatus is "" or "01" or "02" or "08";

    public static bool IsAmendable(ErpStatus status) => status is ErpStatus.Created or ErpStatus.InRelease;

    public static string? PurchaseOrderOf(SapRequisitionDto dto) => dto.Items.Select(i => i.PurchaseOrder).FirstOrDefault(p => p is not null);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
