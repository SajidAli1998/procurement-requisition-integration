using System.Globalization;
using System.Text.Json.Nodes;

namespace Mocks.SourceApi;

// ListedVersion: list pages still show this older version while GET by id returns the current one,
// like a read replica that has not caught up with a concurrent edit.
public sealed record SeedRecord(string Key, long Version, DateTime UpdatedAt, JsonObject Body, long? ListedVersion = null);

public sealed record SeedManifestEntry(string Key, string Status, string? Defect);

public sealed record SeedResult(IReadOnlyList<SeedRecord> Records, IReadOnlyList<SeedManifestEntry> Manifest);

// All names, codes and e-mail addresses are fictitious.
public static class SeedData
{
    public static readonly string[] Malformations =
    [
        "missing_lines", "negative_quantity", "unknown_currency", "unknown_uom", "past_needed_by",
        "quantity_not_number", "total_mismatch", "missing_requester", "invalid_date", "missing_id",
    ];

    // Valid in the source, but SAP rejects it: the plant is not assigned to the company code.
    public const string SapRejectDefect = "plant_company_mismatch";

    private sealed record Material(string Code, string Description, string Group, string Uom, decimal MinPrice, decimal MaxPrice);

    private static readonly Material[] Materials =
    [
        new("MAT-100001", "Laptop 14-inch, standard spec", "IT-HW", "EA", 900, 1400),
        new("MAT-100002", "Monitor 27-inch", "IT-HW", "EA", 180, 320),
        new("MAT-100003", "Docking station", "IT-HW", "EA", 120, 220),
        new("MAT-200001", "A4 copy paper, box of 5 reams", "OFF-SUP", "BOX", 18, 30),
        new("MAT-200002", "Whiteboard markers, pack of 12", "OFF-SUP", "BOX", 8, 15),
        new("MAT-300001", "Nitrile safety gloves, box of 100", "PPE", "BOX", 9, 16),
        new("MAT-300002", "High-visibility vest", "PPE", "EA", 4, 9),
        new("MAT-400001", "Hydraulic oil ISO 46", "FAC-MNT", "L", 3, 6),
        new("MAT-400002", "Copper cable 2.5mm", "FAC-MNT", "M", 1, 3),
        new("SRV-500001", "Consulting services, hourly", "SRV-CONS", "H", 80, 150),
        new("SRV-500002", "Equipment calibration service", "SRV-MNT", "EA", 250, 600),
    ];

    private static readonly (string Company, string Currency, string[] Plants, string[] CostCenters)[] Companies =
    [
        ("1000", "USD", ["1100", "1200"], ["CC-1000-IT", "CC-1000-FIN", "CC-1000-OPS"]),
        ("2000", "EUR", ["2100", "2200"], ["CC-2000-OPS", "CC-2000-HSE"]),
    ];

    private static readonly string[] Justifications =
    [
        "Replacement for end-of-life equipment",
        "New starter onboarding",
        "Quarterly consumables restock",
        "Planned maintenance shutdown",
        "Health and safety compliance",
        "Project delivery requirement",
    ];

    public static SeedResult Generate(int seed, int count, DateTime nowUtc)
    {
        var random = new Random(seed);
        var today = DateOnly.FromDateTime(nowUtc);
        var records = new List<SeedRecord>(count);
        var manifest = new List<SeedManifestEntry>(count);
        var approvedIndex = 0;

        for (var i = 1; i <= count; i++)
        {
            var id = $"REQ-2026-{i:D6}";
            var status = PickStatus(random);
            var company = Companies[random.Next(Companies.Length)];
            var lines = new JsonArray();
            var total = 0m;
            var lineCount = random.Next(1, 5);
            for (var n = 1; n <= lineCount; n++)
            {
                var m = Materials[random.Next(Materials.Length)];
                var qty = m.Uom is "H" or "M" or "L" ? random.Next(5, 200) : random.Next(1, 25);
                var price = Math.Round(m.MinPrice + (decimal)random.NextDouble() * (m.MaxPrice - m.MinPrice), 2);
                total += qty * price;
                lines.Add(new JsonObject
                {
                    ["lineNo"] = n * 10,
                    ["materialCode"] = m.Code,
                    ["description"] = m.Description,
                    ["materialGroup"] = m.Group,
                    ["quantity"] = qty,
                    ["uom"] = m.Uom,
                    ["unitPrice"] = price,
                    ["plant"] = company.Plants[random.Next(company.Plants.Length)],
                });
            }

            // The extra i milliseconds keep the ordering unambiguous.
            var updatedAt = nowUtc.AddDays(-30).AddMinutes(random.Next(0, 30 * 24 * 60)).AddMilliseconds(i);
            var body = new JsonObject
            {
                ["id"] = id,
                ["status"] = status,
                ["requester"] = $"user{random.Next(1, 21):D2}@example.test",
                ["costCenter"] = company.CostCenters[random.Next(company.CostCenters.Length)],
                ["companyCode"] = company.Company,
                ["currency"] = company.Currency,
                ["neededBy"] = today.AddDays(random.Next(7, 61)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["justification"] = Justifications[random.Next(Justifications.Length)],
                ["approvedAt"] = status == "Approved" ? updatedAt.AddMinutes(-random.Next(5, 600)).ToString("O") : null,
                ["totalAmount"] = Math.Round(total, 2),
                ["lines"] = lines,
                ["erpSync"] = null,
            };

            string? defect = null;
            if (status == "Approved")
            {
                approvedIndex++;
                if (approvedIndex % 12 == 0)
                {
                    defect = Malformations[(approvedIndex / 12 - 1) % Malformations.Length];
                    ApplyDefect(body, defect, today);
                }
                else if (approvedIndex % 40 == 7)
                {
                    defect = SapRejectDefect;
                    var other = Companies.First(c => c.Company != company.Company);
                    ((JsonObject)lines[0]!)["plant"] = other.Plants[0];
                }
            }

            var key = body["id"]?.GetValue<string>() ?? $"__noid-{i:D6}";
            records.Add(new SeedRecord(key, random.Next(1, 4), updatedAt, body));
            manifest.Add(new SeedManifestEntry(key, status, defect));
        }

        return new SeedResult(records, manifest);
    }

    private static string PickStatus(Random random) => random.Next(100) switch
    {
        < 80 => "Approved",
        < 88 => "PendingApproval",
        < 93 => "Rejected",
        < 97 => "Draft",
        _ => "Cancelled",
    };

    private static void ApplyDefect(JsonObject body, string defect, DateOnly today)
    {
        var firstLine = ((JsonArray)body["lines"]!)[0]!.AsObject();
        switch (defect)
        {
            case "missing_lines":
                body["lines"] = new JsonArray();
                break;
            case "negative_quantity":
                firstLine["quantity"] = -5;
                break;
            case "unknown_currency":
                body["currency"] = "XYZ";
                break;
            case "unknown_uom":
                firstLine["uom"] = "BUCKET";
                break;
            case "past_needed_by":
                body["neededBy"] = today.AddDays(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case "quantity_not_number":
                firstLine["quantity"] = "ten";
                break;
            case "total_mismatch":
                body["totalAmount"] = body["totalAmount"]!.GetValue<decimal>() + 100m;
                break;
            case "missing_requester":
                body.Remove("requester");
                break;
            case "invalid_date":
                body["neededBy"] = "31/02/2026";
                break;
            case "missing_id":
                body.Remove("id");
                break;
        }
    }
}
