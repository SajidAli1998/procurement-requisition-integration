using System.Globalization;
using System.Text.Json.Nodes;
using Procurement.SyntheticData;

namespace Mocks.SourceApi;

// Nothing is corrected on the way in: blank dates and zero totals reach the integration as they are.
public static class WorkbookSeed
{
    public static SeedResult Build(DataPack pack, EmployeeDirectory directory, DateTime nowUtc)
    {
        var records = new List<SeedRecord>(pack.Requisitions.Count);
        var manifest = new List<SeedManifestEntry>(pack.Requisitions.Count);
        var conflicts = pack.Failures.Where(f => f.Mode == "VERSION_CONFLICT" && f.RemoteCurrentVersion is not null)
            .ToDictionary(f => f.RequisitionId, f => f.RemoteCurrentVersion!.Value);

        // Workbook order becomes change order (one second apart), so page numbers match the workbook.
        var start = nowUtc.AddHours(-1).AddSeconds(-pack.Requisitions.Count);
        for (var i = 0; i < pack.Requisitions.Count; i++)
        {
            var r = pack.Requisitions[i];
            var updatedAt = start.AddSeconds(i);
            var status = r.ApprovalStatus == "Pending" ? "PendingApproval" : r.ApprovalStatus;
            var lines = new JsonArray();
            foreach (var l in r.Lines)
            {
                lines.Add(new JsonObject
                {
                    ["lineNo"] = l.LineNumber,
                    ["materialCode"] = l.ItemCode,
                    ["description"] = l.Description,
                    ["quantity"] = l.Quantity,
                    ["uom"] = l.Unit,
                    ["unitPrice"] = l.UnitPrice,
                    ["lineTotal"] = l.LineTotal,
                    ["plant"] = DataPack.Plant,
                    ["taxCode"] = l.TaxCode,
                });
            }

            var body = new JsonObject
            {
                ["id"] = r.Id,
                ["status"] = status,
                ["requester"] = directory.RequisitionRequesters.GetValueOrDefault(r.Id),
                ["department"] = r.Department,
                ["costCenter"] = r.Lines.Select(l => l.CostCentre).FirstOrDefault(),
                ["companyCode"] = DataPack.CompanyCode,
                ["currency"] = r.Currency,
                ["neededBy"] = r.RequiredBy?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["justification"] = r.Purpose,
                ["approvedAt"] = status == "Approved" ? updatedAt.AddHours(-2).ToString("O") : null,
                ["totalAmount"] = r.TotalAmount,
                ["lines"] = lines,
                ["erpSync"] = null,
            };

            if (conflicts.TryGetValue(r.Id, out var current) && current > r.Version)
            {
                records.Add(new SeedRecord(r.Id, current, updatedAt, body, ListedVersion: r.Version));
            }
            else
            {
                records.Add(new SeedRecord(r.Id, r.Version, updatedAt, body));
            }
            manifest.Add(new SeedManifestEntry(r.Id, status, r.RecordQuality is "VALID" or "NOT_APPROVED" ? null : r.RecordQuality));
        }
        return new SeedResult(records, manifest);
    }
}
