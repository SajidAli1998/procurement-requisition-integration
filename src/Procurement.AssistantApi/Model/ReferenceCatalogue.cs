using Procurement.SyntheticData;

namespace Procurement.AssistantApi.Model;

public sealed record MaterialInfo(string Code, string Description, string MaterialGroup, string Uom, decimal UnitPrice);

public sealed record CostCenterInfo(string Code, string Name, string CompanyCode, string Currency);

public sealed record PlantInfo(string Code, string Name, string CompanyCode);

// Prices come from here, never from the caller, so a model cannot invent a price or a code.
public sealed class ReferenceCatalogue
{
    // Whole quantities only.
    public static readonly IReadOnlySet<string> CountedUnits = new HashSet<string> { "EA", "BOX" };

    public required IReadOnlyList<MaterialInfo> Materials { get; init; }
    public required IReadOnlyList<CostCenterInfo> CostCenters { get; init; }
    public required IReadOnlyList<PlantInfo> Plants { get; init; }

    public MaterialInfo? Material(string code) => Materials.FirstOrDefault(m => m.Code == code);
    public CostCenterInfo? CostCenter(string code) => CostCenters.FirstOrDefault(c => c.Code == code);
    public PlantInfo? Plant(string code) => Plants.FirstOrDefault(p => p.Code == code);

    // The workbook has no plants, so the one assumed plant of the Qatar entity is offered.
    public static ReferenceCatalogue FromWorkbook(DataPack pack)
    {
        var lines = pack.Requisitions.SelectMany(r => r.Lines.Select(l => (Requisition: r, Line: l))).ToList();
        return new ReferenceCatalogue
        {
            Materials = lines.GroupBy(x => x.Line.ItemCode).Select(g => g.First())
                .Select(x => new MaterialInfo(x.Line.ItemCode, x.Line.Description, x.Requisition.Purpose, x.Line.Unit, x.Line.UnitPrice))
                .OrderBy(m => m.Code, StringComparer.Ordinal).ToList(),
            CostCenters = lines.GroupBy(x => x.Line.CostCentre).Select(g => g.First())
                .Select(x => new CostCenterInfo(x.Line.CostCentre, x.Requisition.Department, DataPack.CompanyCode, x.Requisition.Currency))
                .OrderBy(c => c.Code, StringComparer.Ordinal).ToList(),
            Plants = [new(DataPack.Plant, "Main depot (assumed; the workbook has no plants)", DataPack.CompanyCode)],
        };
    }

    public static ReferenceCatalogue Generated() => new()
    {
        Materials =
        [
            new("MAT-100001", "Laptop 14-inch, standard spec", "IT-HW", "EA", 1150m),
            new("MAT-100002", "Monitor 27-inch", "IT-HW", "EA", 250m),
            new("MAT-100003", "Docking station", "IT-HW", "EA", 170m),
            new("MAT-200001", "A4 copy paper, box of 5 reams", "OFF-SUP", "BOX", 24m),
            new("MAT-200002", "Whiteboard markers, pack of 12", "OFF-SUP", "BOX", 11.5m),
            new("MAT-300001", "Nitrile safety gloves, box of 100", "PPE", "BOX", 12.5m),
            new("MAT-300002", "High-visibility vest", "PPE", "EA", 6.5m),
            new("MAT-400001", "Hydraulic oil ISO 46", "FAC-MNT", "L", 4.5m),
            new("MAT-400002", "Copper cable 2.5mm", "FAC-MNT", "M", 2m),
            new("SRV-500001", "Consulting services, hourly", "SRV-CONS", "H", 115m),
            new("SRV-500002", "Equipment calibration service", "SRV-MNT", "EA", 425m),
        ],
        CostCenters =
        [
            new("CC-1000-IT", "IT operations", "1000", "USD"),
            new("CC-1000-FIN", "Finance", "1000", "USD"),
            new("CC-1000-OPS", "Operations", "1000", "USD"),
            new("CC-2000-OPS", "Operations (EU)", "2000", "EUR"),
            new("CC-2000-HSE", "Health, safety and environment (EU)", "2000", "EUR"),
        ],
        Plants =
        [
            new("1100", "Plant North", "1000"),
            new("1200", "Plant South", "1000"),
            new("2100", "Plant East", "2000"),
            new("2200", "Plant West", "2000"),
        ],
    };
}
