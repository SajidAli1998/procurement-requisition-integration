using Procurement.Contracts;
using Procurement.Sync.Clients;
using Procurement.Sync.Mapping;

namespace Procurement.Sync.UnitTests;

public sealed class MappingTests
{
    private static readonly Requisition Sample = new("REQ-1", 3, "user01@example.test", "CC-1000-IT", "1000", "USD",
        new DateOnly(2026, 12, 1), "Laptops for new starters in the finance team", null, 2400m,
        [new RequisitionLine(10, "MAT-100001", "Laptop", "IT-HW", 2, "EA", 1200m, "1100")]);

    [Theory]
    [InlineData("N", "", ErpStatus.Created)]
    [InlineData("N", "01", ErpStatus.InRelease)]
    [InlineData("N", "02", ErpStatus.Released)]
    [InlineData("N", "08", ErpStatus.Rejected)]
    [InlineData("B", "02", ErpStatus.PoCreated)]
    [InlineData("L", "02", ErpStatus.Closed)]
    public void Sap_status_codes_map_to_canonical_status(string processing, string release, ErpStatus expected) =>
        SapMapper.ToErpStatus(new SapRequisitionDto { ProcessingStatus = processing, ReleaseStatus = release }).ShouldBe(expected);

    [Fact]
    public void Canonical_requisition_maps_to_sap_items()
    {
        var sap = SapMapper.ToSap(Sample);

        sap.ExternalReference.ShouldBe("REQ-1");
        sap.ExternalVersion.ShouldBe(3);
        sap.PurReqnDescription!.Length.ShouldBeLessThanOrEqualTo(40);
        var item = sap.Items.ShouldHaveSingleItem();
        item.PurchaseRequisitionItem.ShouldBe("00010");
        item.CompanyCode.ShouldBe("1000");
        item.PurReqnItemCurrency.ShouldBe("USD");
        item.DeliveryDate.ShouldBe(new DateOnly(2026, 12, 1));
    }

    [Fact]
    public void Content_hash_ignores_the_version_but_not_the_content()
    {
        Fingerprints.ContentHash(Sample with { Version = 9 }).ShouldBe(Fingerprints.ContentHash(Sample));
        Fingerprints.ContentHash(Sample with { Justification = "Other" }).ShouldNotBe(Fingerprints.ContentHash(Sample));
    }

    [Fact]
    public void Idempotency_key_is_deterministic_per_version()
    {
        Fingerprints.CreateKey("REQ-1", 3).ShouldBe(Fingerprints.CreateKey("REQ-1", 3));
        Fingerprints.CreateKey("REQ-1", 4).ShouldNotBe(Fingerprints.CreateKey("REQ-1", 3));
    }
}
