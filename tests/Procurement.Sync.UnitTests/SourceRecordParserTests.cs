using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Procurement.Contracts;
using Procurement.Sync.Validation;

namespace Procurement.Sync.UnitTests;

public sealed class SourceRecordParserTests
{
    private const string Valid = """
        {
          "id": "REQ-1", "version": 3, "status": "Approved", "requester": "user01@example.test",
          "costCenter": "CC-1000-IT", "companyCode": "1000", "currency": "USD", "neededBy": "2026-12-01",
          "justification": "Laptops", "approvedAt": "2026-11-01T10:00:00Z", "totalAmount": 2400.00,
          "lines": [ { "lineNo": 10, "materialCode": "MAT-100001", "description": "Laptop", "materialGroup": "IT-HW",
                       "quantity": 2, "uom": "EA", "unitPrice": 1200.00, "plant": "1100" } ],
          "somethingNew": "ignored by the tolerant reader"
        }
        """;

    private static readonly RequisitionValidator Validator =
        new(new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)));

    private static (Requisition?, List<ValidationProblem>) Parse(string json, Action<System.Text.Json.Nodes.JsonObject>? mutate = null)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        mutate?.Invoke(node);
        var element = JsonDocument.Parse(node.ToJsonString()).RootElement;
        var (req, problems) = SourceRecordParser.Parse(element, SourceRecordParser.ReadEnvelope(element));
        if (req is not null) problems = Validator.Check(req);
        return (req, problems);
    }

    private static System.Text.Json.Nodes.JsonObject Line(System.Text.Json.Nodes.JsonObject o) => o["lines"]![0]!.AsObject();

    [Fact]
    public void A_valid_record_parses_and_unknown_fields_are_ignored()
    {
        var (req, problems) = Parse(Valid);

        problems.ShouldBeEmpty();
        req.ShouldNotBeNull();
        req.Id.ShouldBe("REQ-1");
        req.Version.ShouldBe(3);
        req.Lines.Single().Quantity.ShouldBe(2);
    }

    [Fact]
    public void A_string_quantity_is_a_schema_error_with_the_field_path()
    {
        var (_, problems) = Parse(Valid, o => Line(o)["quantity"] = "ten");

        var problem = problems.ShouldHaveSingleItem();
        problem.Reason.ShouldBe(QuarantineReason.SchemaInvalid);
        problem.Field.ShouldBe("lines[0].quantity");
        problem.Message.ShouldContain("\"ten\"");
    }

    [Fact]
    public void A_record_without_id_gets_a_stable_key_and_is_reported()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Valid)!.AsObject();
        node.Remove("id");
        var element = JsonDocument.Parse(node.ToJsonString()).RootElement;

        var envelope = SourceRecordParser.ReadEnvelope(element);
        var again = SourceRecordParser.ReadEnvelope(JsonDocument.Parse(node.ToJsonString()).RootElement);
        var (_, problems) = SourceRecordParser.Parse(element, envelope);

        envelope.HasId.ShouldBeFalse();
        envelope.Key.ShouldStartWith("NOID-");
        again.Key.ShouldBe(envelope.Key);
        problems.ShouldContain(p => p.Reason == QuarantineReason.MissingRequiredField && p.Field == "id");
    }

    [Theory]
    [InlineData("neededBy", "31/02/2026", QuarantineReason.SchemaInvalid)]
    [InlineData("neededBy", "2026-10-01", QuarantineReason.NeededByInPast)]
    [InlineData("currency", "XYZ", QuarantineReason.UnknownCurrency)]
    public void Header_rules(string field, string value, QuarantineReason expected)
    {
        var (_, problems) = Parse(Valid, o => o[field] = value);
        problems[0].Reason.ShouldBe(expected);
    }

    [Fact]
    public void Missing_requester_is_a_missing_required_field()
    {
        var (_, problems) = Parse(Valid, o => o.Remove("requester"));
        problems.ShouldHaveSingleItem().Reason.ShouldBe(QuarantineReason.MissingRequiredField);
    }

    [Fact]
    public void Empty_lines_are_reported_as_missing_lines_not_as_a_total_mismatch()
    {
        var (_, problems) = Parse(Valid, o => o["lines"] = new System.Text.Json.Nodes.JsonArray());
        problems.ShouldHaveSingleItem().Reason.ShouldBe(QuarantineReason.MissingLines);
    }

    [Fact]
    public void A_negative_quantity_is_reported_before_the_resulting_total_mismatch()
    {
        var (_, problems) = Parse(Valid, o => Line(o)["quantity"] = -5);

        problems[0].Reason.ShouldBe(QuarantineReason.InvalidQuantity);
        problems.ShouldContain(p => p.Reason == QuarantineReason.TotalMismatch);
    }

    [Theory]
    [InlineData("uom", "BUCKET", QuarantineReason.UnknownUnitOfMeasure)]
    [InlineData("plant", "9999", QuarantineReason.UnknownPlant)]
    public void Line_rules(string field, string value, QuarantineReason expected)
    {
        var (_, problems) = Parse(Valid, o => Line(o)[field] = value);
        problems.ShouldHaveSingleItem().Reason.ShouldBe(expected);
    }

    [Fact]
    public void Total_must_match_the_lines_within_a_cent()
    {
        Parse(Valid, o => o["totalAmount"] = 2400.01m).Item2.ShouldBeEmpty();
        Parse(Valid, o => o["totalAmount"] = 2500m).Item2.ShouldHaveSingleItem().Reason.ShouldBe(QuarantineReason.TotalMismatch);
    }

    [Fact]
    public void A_zero_total_is_reported_as_not_positive_rather_than_as_a_mismatch()
    {
        var (_, problems) = Parse(Valid, o => o["totalAmount"] = 0);

        problems.Select(p => p.Reason).ShouldBe([QuarantineReason.AmountNotPositive]);
        problems[0].Message.ShouldStartWith("Total amount must be greater than zero");
    }

    [Fact]
    public void A_blank_needed_by_date_is_a_missing_required_field()
    {
        var (_, problems) = Parse(Valid, o => o["neededBy"] = null);

        var problem = problems.ShouldHaveSingleItem();
        problem.Reason.ShouldBe(QuarantineReason.MissingRequiredField);
        problem.Field.ShouldBe("neededBy");
    }

    [Fact]
    public void Qatari_riyal_and_the_tax_code_are_accepted()
    {
        var (req, problems) = Parse(Valid, o => { o["currency"] = "QAR"; Line(o)["taxCode"] = "Z0"; });

        problems.ShouldBeEmpty();
        req!.Lines.Single().TaxCode.ShouldBe("Z0");
    }
}
