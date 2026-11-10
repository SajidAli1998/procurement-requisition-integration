using System.Text.Json.Nodes;
using Mocks.Shared.Faults;
using Procurement.Scenarios.Fixtures;

namespace Procurement.Scenarios.Generator;

/// <summary>Percentages are of the total count; the rest are valid and approved. The same seed always gives the same data.</summary>
public sealed record GeneratorOptions
{
    public int Count { get; init; } = 100;
    public int Seed { get; init; } = 2026;
    public double PendingPct { get; init; } = 5;
    public double InvalidPct { get; init; } = 10;
    public double DuplicatePct { get; init; } = 3;
    public double ExistingInSapPct { get; init; } = 5;
    public double MissingFieldsPct { get; init; } = 4;
    public double InvalidCodesPct { get; init; } = 3;
    public double ApprovalConflictPct { get; init; } = 2;
    public double PriceInconsistencyPct { get; init; } = 3;
    public double SapFailurePct { get; init; } = 5;
}

public sealed record GeneratedRecord(string Id, JsonObject Record, List<string> Traits, string Expected);

public sealed record GeneratedSet(List<GeneratedRecord> Records, List<(string Reference, long Version, decimal Quantity, decimal Price, string Plant, string Company, string Currency)> SapPreloads,
    List<ScriptedFault> SapFaults, Dictionary<string, int> Summary);

/// <summary>Each record states its expected outcome, so the generated data is checked by the pipeline rather than assumed to pass.</summary>
public static class SyntheticGenerator
{
    private sealed record Company(string Code, string Currency, string[] Plants, string[] CostCentres);
    private sealed record Material(string Code, string Description, string Group, string Uom, decimal Min, decimal Max);

    private static readonly Company[] Companies =
    [
        new("1000", "USD", ["1100", "1200"], ["CC-1000-10", "CC-1000-20", "CC-1000-30"]),
        new("2000", "EUR", ["2100", "2200"], ["CC-2000-10", "CC-2000-40"]),
    ];

    private static readonly Material[] Materials =
    [
        new("MAT-LAPTOP-14", "Laptop 14-inch", "IT", "EA", 900, 1800), new("MAT-MONITOR-27", "Monitor 27-inch", "IT", "EA", 180, 420),
        new("MAT-PAPER-A4", "Copy paper A4 (box)", "OFFICE", "BOX", 18, 35), new("MAT-DIESEL", "Diesel fuel", "FLEET", "L", 0.9m, 1.6m),
        new("SRV-CONSULT", "Consulting services", "SERVICES", "H", 90, 220), new("MAT-CABLE-CAT6", "Network cable Cat6", "IT", "M", 0.4m, 1.2m),
        new("MAT-TYRE-295", "Bus tyre 295/80R22.5", "FLEET", "EA", 320, 610), new("MAT-SAFETY-VEST", "High-visibility vest", "SAFETY", "EA", 6, 14),
    ];

    private static readonly string[] Requesters = ["amira.haddad", "omar.khalil", "lina.nasser", "yusuf.rahman", "sara.ibrahim", "karim.mansour", "noor.saleh", "tariq.aziz"];
    private static readonly string[] Purposes = ["Depot workshop restock", "New starter equipment", "Fleet maintenance", "Office supplies", "Network upgrade", "Safety compliance", "Contractor support"];

    public static GeneratedSet Generate(GeneratorOptions o)
    {
        var random = new Random(o.Seed);
        var records = new List<GeneratedRecord>();
        var preloads = new List<(string, long, decimal, decimal, string, string, string)>();
        var faults = new List<ScriptedFault>();
        int N(double pct) => (int)Math.Round(o.Count * Math.Clamp(pct, 0, 100) / 100.0);

        // Traits are assigned to distinct records, in a fixed order, so the counts are exact.
        var plan = new List<string>();
        void Plan(string trait, int n) { for (var i = 0; i < n; i++) plan.Add(trait); }
        Plan("pending", N(o.PendingPct));
        Plan("invalid", N(o.InvalidPct));
        Plan("missing-field", N(o.MissingFieldsPct));
        Plan("invalid-code", N(o.InvalidCodesPct));
        Plan("approval-conflict", N(o.ApprovalConflictPct));
        Plan("price-inconsistent", N(o.PriceInconsistencyPct));
        Plan("existing-in-sap", N(o.ExistingInSapPct));
        Plan("sap-failure", N(o.SapFailurePct));
        while (plan.Count < o.Count) plan.Add("valid");
        plan = plan.Take(o.Count).OrderBy(_ => random.Next()).ToList();

        for (var i = 0; i < o.Count; i++)
        {
            var id = $"GEN-{o.Seed}-{i + 1:D5}";
            var company = Companies[random.Next(Companies.Length)];
            var lines = new JsonArray();
            var lineCount = random.Next(1, 5);
            for (var n = 1; n <= lineCount; n++)
            {
                var m = Materials[random.Next(Materials.Length)];
                var qty = m.Uom is "EA" or "BOX" ? random.Next(1, 20) : Math.Round((decimal)(random.NextDouble() * 400 + 5), 1);
                var price = Math.Round(m.Min + (decimal)random.NextDouble() * (m.Max - m.Min), 2);
                lines.Add(Req.Line(n * 10, m.Code, m.Description, qty, m.Uom, price, company.Plants[random.Next(company.Plants.Length)], m.Group));
            }
            var record = new JsonObject
            {
                ["id"] = id,
                ["status"] = "Approved",
                ["requester"] = Requesters[random.Next(Requesters.Length)] + "@example.test",
                ["costCenter"] = company.CostCentres[random.Next(company.CostCentres.Length)],
                ["companyCode"] = company.Code,
                ["currency"] = company.Currency,
                ["neededBy"] = Req.Today(random.Next(7, 90)),
                ["justification"] = Purposes[random.Next(Purposes.Length)],
                ["approvedAt"] = DateTime.UtcNow.AddHours(-random.Next(1, 72)).ToString("O"),
                ["lines"] = lines,
                ["erpSync"] = null,
            }.Recalculate();

            var trait = plan[i];
            var traits = new List<string> { trait };
            string expected;
            switch (trait)
            {
                case "pending":
                    record["status"] = random.Next(3) switch { 0 => "PendingApproval", 1 => "Draft", _ => "Rejected" };
                    record["approvedAt"] = null;
                    expected = $"Skipped ({record["status"]})";
                    break;
                case "invalid":
                    switch (random.Next(4))
                    {
                        case 0: record.Set("lines[0].quantity", 0).Recalculate(); expected = "Quarantined: InvalidQuantity"; break;
                        case 1: record["neededBy"] = Req.Today(-10); expected = "Quarantined: NeededByInPast"; break;
                        case 2: record["totalAmount"] = 0; expected = "Quarantined: AmountNotPositive"; break;
                        default: record.Set("lines[0].unitPrice", -5).Recalculate(); expected = "Quarantined: InvalidPrice"; break;
                    }
                    break;
                case "missing-field":
                    var field = new[] { "requester", "costCenter", "currency", "neededBy" }[random.Next(4)];
                    record.Remove(field);
                    expected = "Quarantined: MissingRequiredField";
                    traits.Add("missing " + field);
                    break;
                case "invalid-code":
                    if (random.Next(2) == 0) { record.Set("lines[0].plant", "9999"); expected = "Quarantined: UnknownPlant"; }
                    else { record.Set("lines[0].uom", "PCS"); expected = "Quarantined: UnknownUnitOfMeasure"; }
                    break;
                case "approval-conflict":
                    record["approvedAt"] = DateTime.UtcNow.AddDays(3).ToString("O");
                    expected = "Quarantined: InvalidApproval";
                    break;
                case "price-inconsistent":
                    record["totalAmount"] = Math.Round(Req.Number(record["totalAmount"])!.Value * 1.1m + 1, 2);
                    expected = "Quarantined: TotalMismatch";
                    break;
                case "existing-in-sap":
                    var first = (JsonObject)lines[0]!;
                    if (lines.Count > 1) { while (lines.Count > 1) lines.RemoveAt(1); record.Recalculate(); }
                    preloads.Add((id, 1, Req.Number(first["quantity"])!.Value, Req.Number(first["unitPrice"])!.Value, first["plant"]!.GetValue<string>(), company.Code, company.Currency));
                    expected = "Linked (already in SAP)";
                    break;
                case "sap-failure":
                    var kind = random.Next(3);
                    faults.Add(kind switch
                    {
                        0 => new ScriptedFault { Kind = FaultKind.Throttle, Method = "POST", PathContains = "A_PurchaseRequisitionHeader", BodyContains = $"\"{id}\"", Count = 2, RetryAfterSeconds = 1 },
                        1 => new ScriptedFault { Kind = FaultKind.Error, Method = "POST", PathContains = "A_PurchaseRequisitionHeader", BodyContains = $"\"{id}\"", Count = 2, StatusCode = 503 },
                        _ => new ScriptedFault { Kind = FaultKind.LostResponse, Method = "POST", PathContains = "A_PurchaseRequisitionHeader", BodyContains = $"\"{id}\"", Count = 1 },
                    });
                    traits.Add(kind switch { 0 => "429 ×2", 1 => "503 ×2", _ => "lost response" });
                    expected = "Created (after recovering from the SAP failure)";
                    break;
                default:
                    expected = "Created";
                    break;
            }
            records.Add(new GeneratedRecord(id, record, traits, expected));
        }

        // Duplicates are served again with identical content and must not be created twice.
        var duplicates = N(o.DuplicatePct);
        foreach (var original in records.Where(r => r.Traits[0] == "valid").Take(duplicates).ToList())
        {
            original.Traits.Add("duplicated in the feed");
        }

        var summary = records.GroupBy(r => r.Expected.Split(' ')[0].TrimEnd(':')).ToDictionary(g => g.Key, g => g.Count());
        summary["DuplicatedInFeed"] = records.Count(r => r.Traits.Contains("duplicated in the feed"));
        return new GeneratedSet(records, preloads, faults, summary);
    }

    public static string Csv(GeneratedSet set)
    {
        var sb = new System.Text.StringBuilder("requisition_id,status,requester,cost_center,company_code,currency,needed_by,justification,total_amount,line_no,material_code,description,quantity,uom,unit_price,plant,expected\n");
        foreach (var g in set.Records)
        {
            var r = g.Record;
            foreach (var line in (r["lines"] as JsonArray ?? []).OfType<JsonObject>())
            {
                string?[] cells = [r["id"]?.ToString(), r["status"]?.ToString(), r["requester"]?.ToString(), r["costCenter"]?.ToString(), r["companyCode"]?.ToString(),
                    r["currency"]?.ToString(), r["neededBy"]?.ToString(), r["justification"]?.ToString(), r["totalAmount"]?.ToJsonString(), line["lineNo"]?.ToJsonString(),
                    line["materialCode"]?.ToString(), line["description"]?.ToString(), line["quantity"]?.ToJsonString(), line["uom"]?.ToString(), line["unitPrice"]?.ToJsonString(),
                    line["plant"]?.ToString(), g.Expected];
                sb.AppendLine(string.Join(',', cells.Select(c => c is null ? "" : c.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c)));
            }
        }
        return sb.ToString();
    }
}
