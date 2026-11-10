using System.Text;
using Procurement.Scenarios.Engine;
using Procurement.Scenarios.Fixtures;
using Procurement.Scenarios.Import;
using Procurement.Sync.Store;
using Procurement.SyntheticData;
using static Procurement.Scenarios.Engine.Priority;
using static Procurement.Scenarios.Engine.Mode;
using X = Procurement.Scenarios.Import.XlsxBuilder;

namespace Procurement.Scenarios.Catalogue;

internal static class ImportScenarios
{
    private static readonly string[] Header = ["requisition_id", "status", "requester", "cost_center", "company_code", "currency", "needed_by", "justification", "total_amount", "line_no", "material_code", "description", "quantity", "uom", "unit_price", "plant"];

    private static object?[] Row(string id, int line = 10, object? qty = null, object? price = null, object? total = null, string description = "Laptop", object? cost = null, string? plant = "1100") =>
        [id, "Approved", "user01@example.test", cost ?? "CC-1000-10", "1000", "USD", Req.Today(30), "Imported", total ?? 2400, line, "MAT-100", description, qty ?? 2, "EA", price ?? 1200, plant];

    private static X Flat(params object?[][] rows)
    {
        var x = new X().Sheet("Requisitions").Row([.. Header]);
        foreach (var r in rows) x.Row(r);
        return x;
    }

    private static string CsvLine(IEnumerable<object?> cells) => string.Join(',', cells.Select(c => c is null ? "" : Convert.ToString(c, System.Globalization.CultureInfo.InvariantCulture)!));

    private static byte[] Csv(params object?[][] rows) => Encoding.UTF8.GetBytes(CsvLine(Header) + "\n" + string.Join("\n", rows.Select(CsvLine)) + "\n");

    /// <summary>An import-only check: what the importer reports for this file, with nothing loaded anywhere.</summary>
    private static ScenarioBuilder Check(CatalogueBuilder c, string id, string title, Priority p, string expect, Func<byte[]> file, string name, Action<ScenarioContext, ImportResult> assert) =>
        c.Add(id, Categories.Import, title, p, Negative).Inv("INV-12").Fixture($"Generated file '{name}'").Expect(expect).Run(ctx =>
        {
            var result = RequisitionImporter.Import(file(), name);
            ctx.Evidence("import", new { result.FileName, result.Format, result.Sheets, requisitions = result.Records.Count, result.Lines, result.Errors, result.Warnings });
            foreach (var e in result.Errors.Take(10)) ctx.Info($"error: {e.Sheet}!{e.Row} [{e.Column}] {e.Reason}");
            foreach (var w in result.Warnings.Take(10)) ctx.Info($"warning: {w.Sheet}!{w.Row} [{w.Column}] {w.Reason}");
            assert(ctx, result);
            return Task.CompletedTask;
        });

    private static bool Has(IEnumerable<ImportIssue> issues, string text) => issues.Any(i => (i.Reason + " " + i.Column).Contains(text, StringComparison.OrdinalIgnoreCase));

    public static void Register(CatalogueBuilder c)
    {
        const string I = Categories.Import;

        c.Add("IMP-001", I, "Original 120-row workbook: imported and synchronised", Critical, Baseline, Functional)
            .Fixture("The client's workbook, uploaded as a file into an empty lab")
            .Expect("120 requisitions, 200 lines, 0 errors; after a sync every one is accounted for (65 created, 50 quarantined, 5 skipped) with 0 duplicates")
            .Run(async ctx =>
            {
                var result = RequisitionImporter.Import(await File.ReadAllBytesAsync(DataFiles.Resolve(null, DataPack.FileName)), DataPack.FileName);
                ctx.Equal("import errors", 0, result.Errors.Count);
                ctx.Equal("requisitions", 120, result.Records.Count);
                ctx.Equal("lines", 200, result.Lines);
                foreach (var r in result.Records)
                {
                    var version = r["importVersion"]?.GetValue<long>();
                    var copy = r.Clone();
                    copy.Remove("importVersion");
                    await ctx.Lab.PutSourceAsync(copy["id"]!.GetValue<string>(), copy, version);
                }
                ctx.Step("Load the imported records into the lab source; SAP starts empty (no pre-existing documents)");
                await ctx.RunUntilSettledAsync();
                await using var db = ctx.Lab.Db();
                ctx.Equal("created", 65, db.Requisitions.Count(r => r.State == RecordState.Synced));
                ctx.Equal("quarantined", 50, db.Requisitions.Count(r => r.State == RecordState.Quarantined));
                ctx.Equal("skipped", 5, db.Requisitions.Count(r => r.State == RecordState.Ignored));
            });

        Check(c, "IMP-002", "Empty workbook", Medium, "Refused: the workbook is empty", () => new X().Sheet("Sheet1").Build(), "empty.xlsx",
            (ctx, r) => ctx.Check("refused as empty", Has(r.Errors, "empty") && r.Records.Count == 0, "empty", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-003", "Headers only", Medium, "Refused: header but no data rows", () => Flat().Build(), "headers.xlsx",
            (ctx, r) => ctx.Check("no data rows reported", Has(r.Errors, "no data rows"), "no data rows", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-004", "Missing columns", High, "Each missing required column named", () => new X().Sheet("R").Row("requisition_id", "status", "currency").Row("REQ-1", "Approved", "USD").Build(), "missing.xlsx",
            (ctx, r) =>
            {
                ctx.Check("'quantity' reported missing", r.Errors.Any(e => e.Column == "quantity"), "quantity", string.Join(",", r.Errors.Select(e => e.Column)));
                ctx.Check("'unit_price' reported missing", r.Errors.Any(e => e.Column == "unit_price"), "unit_price", "");
            });
        Check(c, "IMP-005", "Renamed columns", High, "A renamed column is reported missing (never guessed)",
            () => new X().Sheet("R").Row([.. Header.Select(h => h == "quantity" ? "Qty" : h)]).Row(Row("REQ-1")).Build(), "renamed.xlsx",
            (ctx, r) => ctx.Check("'quantity' reported missing (renamed to Qty)", r.Errors.Any(e => e.Column == "quantity" && e.Reason.Contains("renamed")), "quantity missing", string.Join("; ", r.Errors.Select(e => e.Reason))));
        Check(c, "IMP-006", "Reordered columns", Medium, "Read by header name: imported correctly",
            () => new X().Sheet("R").Row([.. Header.Reverse()]).Row([.. Row("REQ-IMP6").Reverse()]).Build(), "reordered.xlsx",
            (ctx, r) =>
            {
                ctx.Equal("errors", 0, r.Errors.Count);
                ctx.Equal("quantity read from the right column", "2", r.Records.FirstOrDefault()?["lines"]?[0]?["quantity"]?.ToJsonString());
            });
        Check(c, "IMP-007", "Duplicate column names", High, "Refused: the duplicated header is named",
            () => new X().Sheet("R").Row([.. Header, "quantity"]).Row([.. Row("REQ-1"), 5]).Build(), "dupcol.xlsx",
            (ctx, r) => ctx.Check("duplicate column reported", r.Errors.Any(e => e.Reason.Contains("appears 2 times")), "appears 2 times", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-008", "Duplicate rows", Medium, "Exact duplicate ignored with a warning; one record",
            () => Flat(Row("REQ-IMP8"), Row("REQ-IMP8")).Build(), "duprows.xlsx",
            (ctx, r) =>
            {
                ctx.Equal("records", 1, r.Records.Count);
                ctx.Check("duplicate warned", Has(r.Warnings, "Duplicate"), "warning", r.Warnings.FirstOrDefault()?.Reason);
            });
        Check(c, "IMP-009", "Blank rows", Low, "Skipped; the rest imported",
            () => Flat(Row("REQ-IMP9A"), [null, null], Row("REQ-IMP9B")).Build(), "blank.xlsx",
            (ctx, r) => { ctx.Equal("records", 2, r.Records.Count); ctx.Equal("errors", 0, r.Errors.Count); });
        Check(c, "IMP-010", "Hidden rows", Medium, "Hidden row not imported, with a warning naming the row",
            () => new X().Sheet("Requisitions").Row([.. Header]).Row(Row("REQ-IMP10A")).HiddenRow(Row("REQ-IMP10B")).Build(), "hidden.xlsx",
            (ctx, r) =>
            {
                ctx.Equal("records (hidden one left out)", 1, r.Records.Count);
                ctx.Check("hidden row warned with its row number", r.Warnings.Any(w => w.Row == 3 && w.Reason.Contains("Hidden")), "row 3", r.Warnings.FirstOrDefault()?.Reason);
            });
        Check(c, "IMP-011", "Merged cells in the data", High, "Refused for those rows with the merged range named",
            () => Flat(Row("REQ-IMP11"), Row("REQ-IMP11", line: 20)).Merge("A2:A3").Build(), "merged.xlsx",
            (ctx, r) => ctx.Check("merge reported with its range", r.Errors.Any(e => e.Column == "A2:A3"), "A2:A3", string.Join(",", r.Errors.Select(e => e.Column))));
        Check(c, "IMP-012", "Formula cells (never evaluated)", Critical, "Refused: formulas are not evaluated; the cell is named",
            () => Flat([.. Row("REQ-IMP12").Take(12), X.Formula("2*1", 2), "EA", 1200, "1100"]).Build(), "formula.xlsx",
            (ctx, r) => ctx.Check("formula cell reported", r.Errors.Any(e => e.Reason.Contains("formula") && e.Column!.Contains("M2")), "quantity (M2)", string.Join("; ", r.Errors.Select(e => $"{e.Column}: {e.Reason}"))));
        Check(c, "IMP-013", "Formula errors (#DIV/0!)", High, "Refused with the error value and cell",
            () => Flat([.. Row("REQ-IMP13").Take(14), X.ErrorCell("#DIV/0!"), "1100"]).Build(), "diverr.xlsx",
            (ctx, r) => ctx.Check("error value reported", r.Errors.Any(e => e.Reason.Contains("#DIV/0!")), "#DIV/0!", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-014", "Date serial numbers converted", Medium, "An Excel date (serial number) becomes yyyy-MM-dd",
            () => Flat([.. Row("REQ-IMP14").Take(6), DateOnly.Parse(Req.Today(45)), .. Row("REQ-IMP14").Skip(7)]).Build(), "serial.xlsx",
            (ctx, r) => ctx.Equal("needed-by date", Req.Today(45), r.Records.FirstOrDefault()?["neededBy"]?.GetValue<string>()));
        Check(c, "IMP-015", "Leading zeros in codes", High, "Text '0042' kept; a numeric 42 warned (zeros may be lost)",
            () => Flat(Row("REQ-IMP15A", cost: X.Text("0042")), Row("REQ-IMP15B", cost: 42)).Build(), "zeros.xlsx",
            (ctx, r) =>
            {
                ctx.Equal("text code kept", "0042", r.Records.First(x => x["id"]!.GetValue<string>() == "REQ-IMP15A")["costCenter"]?.GetValue<string>());
                ctx.Check("numeric code warned", Has(r.Warnings, "leading zeros"), "leading zeros", r.Warnings.FirstOrDefault()?.Reason);
            });
        Check(c, "IMP-016", "Currency symbols in numbers", High, "'$1200.00' refused, not stripped",
            () => Flat(Row("REQ-IMP16", price: "$1200.00")).Build(), "symbol.xlsx",
            (ctx, r) => ctx.Check("currency symbol reported", Has(r.Errors, "currency symbol"), "currency symbol", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-017", "Thousand separators", High, "'1,200.50' refused as ambiguous",
            () => Flat(Row("REQ-IMP17", price: "1,200.50")).Build(), "thousands.xlsx",
            (ctx, r) => ctx.Check("thousands separator reported", Has(r.Errors, "thousands separator"), "thousands separator", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-018", "Decimal comma", High, "'1200,50' refused (locale-dependent)",
            () => Flat(Row("REQ-IMP18", price: "1200,50")).Build(), "comma.xlsx",
            (ctx, r) => ctx.Check("decimal comma reported", Has(r.Errors, "decimal comma"), "decimal comma", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-019", "Unexpected sheet names", Medium, "Refused; the sheets found are listed",
            () => new X().Sheet("Requisition_Lines").Row("Source_Requisition_ID", "Line_Number", "Item_Code", "Description", "Quantity", "Unit", "Cost_Centre").Row("REQ-1", 1, "X", "D", 1, "EA", "CC").Sheet("Reqs").Row("a", "b", "c").Build(), "sheets.xlsx",
            (ctx, r) => ctx.Check("names the sheets found", r.Errors.Any(e => e.Reason.Contains("Reqs")), "lists sheets", r.Errors.FirstOrDefault()?.Reason));

        c.Add("IMP-020", I, "Workbook without Reconciliation_Expected", Medium, Negative).Inv("INV-12")
            .Expect("Imports fine with a warning that expected-vs-actual comparison is unavailable").Run(ctx =>
            {
                var x = new X().Sheet("Approved_Requisitions").Row("Source_Requisition_ID", "Source_Version", "Approval_Status", "Currency", "Total_Amount", "Required_By")
                    .Row("REQ-I20", 1, "Approved", "QAR", 100, DateOnly.Parse(Req.Today(20)))
                    .Sheet("Requisition_Lines").Row("Source_Requisition_ID", "Line_Number", "Item_Code", "Description", "Quantity", "Unit", "Unit_Price_QAR", "Cost_Centre")
                    .Row("REQ-I20", 1, "TEST-1", "Item", 1, "EA", 100, "CC-02");
                var r = RequisitionImporter.Import(x.Build(), "nosheet.xlsx");
                ctx.Equal("errors", 0, r.Errors.Count);
                ctx.Equal("records", 1, r.Records.Count);
                ctx.Check("warning about the missing sheet", Has(r.Warnings, "Reconciliation_Expected"), "warning", r.Warnings.FirstOrDefault()?.Reason);
                return Task.CompletedTask;
            });

        Check(c, "IMP-021", "Duplicate sheet names", Low, "Refused: two sheets share a name",
            () => new X().Sheet("Requisitions").Row([.. Header]).Row(Row("A")).Sheet("Requisitions").Row([.. Header]).Row(Row("B")).Build(), "dupsheet.xlsx",
            (ctx, r) => ctx.Check("duplicate sheet reported", Has(r.Errors, "share this name"), "share this name", string.Join("; ", r.Errors.Select(e => e.Reason))));
        Check(c, "IMP-022", "Invalid file extension", Medium, "Refused: only .xlsx and .csv", () => "hello"u8.ToArray(), "data.txt",
            (ctx, r) => ctx.Check("unsupported type", Has(r.Errors, "Unsupported file type"), "Unsupported", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-023", "Wrong content for the extension (CSV text named .xlsx)", High, "Refused: content checked, not the name", () => Csv(Row("REQ-1")), "fake.xlsx",
            (ctx, r) => ctx.Check("type mismatch reported", Has(r.Errors, "not an .xlsx"), "not an .xlsx", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-024", "Corrupt workbook", High, "Refused as corrupt, no crash", () => Flat(Row("REQ-1")).Build()[..400], "corrupt.xlsx",
            (ctx, r) => ctx.Check("corrupt reported", Has(r.Errors, "corrupt"), "corrupt", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-025", "Password-protected workbook", Medium, "Refused with advice to remove the password",
            () => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, .. new byte[1024]], "locked.xlsx",
            (ctx, r) => ctx.Check("password reported", Has(r.Errors, "password"), "password", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-026", "Oversized upload", Medium, "Refused over 10 MB before parsing", () => new byte[11 * 1024 * 1024], "big.csv",
            (ctx, r) => ctx.Check("size limit reported", Has(r.Errors, "limit"), "limit", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-027", "CSV with invalid encoding (Latin-1)", High, "Refused: not UTF-8 (no mojibake imported)",
            () => [.. Encoding.UTF8.GetBytes(CsvLine(Header) + "\n"), .. Encoding.Latin1.GetBytes(CsvLine(Row("REQ-1", description: "Café")) + "\n")], "latin1.csv",
            (ctx, r) => ctx.Check("encoding reported", Has(r.Errors, "UTF-8"), "UTF-8", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-028", "UTF-8 with BOM", Low, "BOM recognised and ignored; imported", () => [0xEF, 0xBB, 0xBF, .. Csv(Row("REQ-IMP28"))], "bom.csv",
            (ctx, r) => { ctx.Equal("records", 1, r.Records.Count); ctx.Equal("first header read correctly (no BOM in the name)", "REQ-IMP28", r.Records.FirstOrDefault()?["id"]?.GetValue<string>()); });
        Check(c, "IMP-029", "Embedded commas and newlines in quoted fields", Medium, "Parsed as one field each",
            () => Encoding.UTF8.GetBytes(CsvLine(Header) + "\n" + CsvLine([.. Row("REQ-IMP29").Take(11), "\"Desk, oak\nwith drawers\"", .. Row("REQ-IMP29").Skip(12)]) + "\n"), "quoted.csv",
            (ctx, r) => ctx.Equal("description", "Desk, oak\nwith drawers", r.Records.FirstOrDefault()?["lines"]?[0]?["description"]?.GetValue<string>()));
        Check(c, "IMP-030", "CSV formula injection", Critical, "A cell starting with = is refused, never stored",
            () => Csv(Row("REQ-IMP30", description: "=HYPERLINK(\"http://x.test\",\"click\")")), "inject.csv",
            (ctx, r) => ctx.Check("formula injection reported", Has(r.Errors, "formula injection"), "formula injection", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-031", "Spreadsheet with macros", Critical, "Refused; the macro is never opened or run", () => Flat(Row("REQ-1")).Build(withMacroPart: true), "macro.xlsx",
            (ctx, r) => ctx.Check("macros reported", Has(r.Errors, "macros"), "macros", r.Errors.FirstOrDefault()?.Reason));
        Check(c, "IMP-032", "Conflicting records for the same requisition", High, "Refused for that requisition: both rows named",
            () => Flat(Row("REQ-IMP32"), [.. Row("REQ-IMP32", line: 20).Take(8), 9999, .. Row("REQ-IMP32", line: 20).Skip(9)]).Build(), "conflict.xlsx",
            (ctx, r) => ctx.Check("conflict reported", Has(r.Errors, "conflicting"), "conflicting", string.Join("; ", r.Errors.Select(e => e.Reason))));
    }
}
