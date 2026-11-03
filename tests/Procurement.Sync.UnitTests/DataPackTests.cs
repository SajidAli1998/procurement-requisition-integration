using Procurement.SyntheticData;

namespace Procurement.Sync.UnitTests;

/// <summary>The client's workbook is read as-is, including its deliberate defects.</summary>
public sealed class DataPackTests
{
    private static readonly DataPack Pack = DataPack.Load();

    [Fact]
    public void Every_sheet_is_read_with_its_documented_record_count()
    {
        Pack.Requisitions.Count.ShouldBe(120);
        Pack.Requisitions.Sum(r => r.Lines.Count).ShouldBe(200);
        Pack.Failures.Count.ShouldBe(78);
        Pack.RemoteStatus.Count.ShouldBe(65);
        Pack.QuarantineExpected.Count.ShouldBe(50);
        Pack.ReconciliationExpected.Count.ShouldBe(120);
        Pack.ApiTests.Count.ShouldBe(60);
        Pack.Suppliers.Count.ShouldBe(50);
    }

    [Fact]
    public void Values_keep_their_types_and_blanks_stay_blank()
    {
        var first = Pack.Requisitions[0];
        first.Id.ShouldBe("REQ-7001");
        first.Currency.ShouldBe("QAR");
        first.RequiredBy.ShouldBe(new DateOnly(2026, 12, 1));
        first.Lines.Select(l => l.LineNumber).ShouldBe([10, 20]);

        Pack.Requisitions.Single(r => r.Id == "REQ-7071").TotalAmount.ShouldBe(0m);
        Pack.Requisitions.Single(r => r.Id == "REQ-7072").RequiredBy.ShouldBeNull();
        Pack.Failures.Single(f => f.RequisitionId == "REQ-7003").RetryAfterSeconds.ShouldBe(20);
        Pack.ApiTests[0].ConfirmationRequired.ShouldBeFalse();
    }

    [Fact]
    public void Line_totals_match_quantity_times_price_as_the_workbook_promises()
    {
        Pack.Requisitions.SelectMany(r => r.Lines).ShouldAllBe(l => l.LineTotal == l.Quantity * l.UnitPrice);
    }

    [Fact]
    public void The_employee_directory_names_a_requester_for_every_workbook_requisition()
    {
        var directory = EmployeeDirectory.Load();
        Pack.Requisitions.ShouldAllBe(r => directory.RequisitionRequesters.ContainsKey(r.Id));
        directory.Find("E1008")!.CanRaise.ShouldBeFalse();
        directory.Find("E1900")!.ProcurementDelegate.ShouldBeTrue();
    }
}
