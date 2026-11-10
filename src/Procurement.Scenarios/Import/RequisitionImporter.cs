using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Procurement.SyntheticData;

namespace Procurement.Scenarios.Import;

public sealed record ImportIssue(string? Sheet, int? Row, string? Column, string Reason);

public sealed record ImportResult
{
    public required string FileName { get; init; }
    public string Format { get; set; } = "unknown";
    public List<string> Sheets { get; } = [];
    public List<JsonObject> Records { get; } = [];
    public int Lines { get; set; }
    public int RowsRead { get; set; }
    public int RowsSkipped { get; set; }
    public List<ImportIssue> Errors { get; } = [];
    public List<ImportIssue> Warnings { get; } = [];
    public bool Ok => Errors.Count == 0 && Records.Count > 0;
}

/// <summary>
/// Accepts the client's workbook layout, one flat sheet, or CSV. Formulas are never evaluated, macros never run, and the
/// file name alone is never trusted: a file that cannot be read safely is refused, not guessed at.
/// </summary>
public static class RequisitionImporter
{
    public const long MaxBytes = 10 * 1024 * 1024;

    private static readonly string[] HeaderColumns = ["Source_Requisition_ID", "Source_Version", "Approval_Status", "Currency", "Total_Amount", "Required_By"];
    private static readonly string[] LineColumns = ["Source_Requisition_ID", "Line_Number", "Item_Code", "Description", "Quantity", "Unit", "Cost_Centre"];
    private static readonly string[] FlatColumns = ["requisition_id", "status", "currency", "needed_by", "total_amount", "line_no", "description", "quantity", "uom", "unit_price", "plant", "company_code", "cost_center", "requester"];

    /// <summary>Strips any directory part and unsafe characters: an uploaded name is a label, never a path.</summary>
    public static string SafeFileName(string? name)
    {
        var bare = Path.GetFileName((name ?? "upload").Replace('\\', '/'));
        var clean = new string(bare.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ').ToArray()).Trim();
        return clean.Length == 0 || clean.Trim('.').Length == 0 ? "upload" : clean;
    }

    public static ImportResult Import(byte[] content, string? fileName, string? contentType = null)
    {
        var result = new ImportResult { FileName = SafeFileName(fileName) };
        if (fileName is not null && result.FileName != fileName)
            result.Warnings.Add(new(null, null, null, $"File name '{fileName}' was reduced to '{result.FileName}'"));
        if (content.Length == 0) { result.Errors.Add(new(null, null, null, "The file is empty")); return result; }
        if (content.Length > MaxBytes) { result.Errors.Add(new(null, null, null, $"The file is {content.Length / 1024 / 1024.0:0.0} MB; the limit is {MaxBytes / 1024 / 1024} MB")); return result; }

        var ext = Path.GetExtension(result.FileName).ToLowerInvariant();
        var isZip = content.Length > 4 && content[0] == 'P' && content[1] == 'K' && content[2] == 3 && content[3] == 4;
        var isOle = content.Length > 8 && content[0] == 0xD0 && content[1] == 0xCF && content[2] == 0x11 && content[3] == 0xE0;
        switch (ext)
        {
            case ".xlsm":
                result.Errors.Add(new(null, null, null, "Macro-enabled workbooks (.xlsm) are refused; save as .xlsx"));
                return result;
            case ".xls":
                result.Errors.Add(new(null, null, null, "Legacy .xls workbooks are not accepted; save as .xlsx"));
                return result;
            case ".xlsx":
                if (isOle) { result.Errors.Add(new(null, null, null, "The workbook is password-protected (encrypted) or not an .xlsx; remove the password and save as .xlsx")); return result; }
                if (!isZip) { result.Errors.Add(new(null, null, null, "The content is not an .xlsx workbook (wrong file type for the .xlsx extension)")); return result; }
                if (contentType is not null && !contentType.Contains("spreadsheet", StringComparison.OrdinalIgnoreCase) && !contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase) && !contentType.Contains("excel", StringComparison.OrdinalIgnoreCase))
                    result.Warnings.Add(new(null, null, null, $"Declared type '{contentType}' does not match .xlsx; the content was checked instead"));
                ReadXlsx(content, result);
                break;
            case ".csv":
                if (isZip || isOle || content.Take(4096).Contains((byte)0)) { result.Errors.Add(new(null, null, null, "The content is binary, not CSV text (wrong file type for the .csv extension)")); return result; }
                ReadCsv(content, result);
                break;
            default:
                result.Errors.Add(new(null, null, null, $"Unsupported file type '{ext}'; upload .xlsx or .csv"));
                return result;
        }
        return result;
    }

    private sealed record Sheet(string Name, List<Row> Rows, List<string>? Merges = null);
    private sealed record Row(int Number, bool Hidden, List<CellValue> Cells);
    private sealed record CellValue(string Column, object? Value, bool Formula, bool ErrorValue, bool Numeric);

    private static void ReadXlsx(byte[] content, ImportResult result)
    {
        try
        {
            using (var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read))
            {
                if (zip.Entries.Any(e => e.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                {
                    result.Errors.Add(new(null, null, null, "The workbook contains macros (vbaProject.bin); macros are never run or accepted"));
                    return;
                }
            }
            using var doc = SpreadsheetDocument.Open(new MemoryStream(content), false);
            var wb = doc.WorkbookPart;
            if (wb?.Workbook?.Sheets is null) { result.Errors.Add(new(null, null, null, "The workbook has no sheets")); return; }
            var strings = wb.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];
            var sheets = new List<Sheet>();
            foreach (var s in wb.Workbook.Sheets.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>())
            {
                var name = s.Name?.Value ?? "?";
                result.Sheets.Add(name);
                if (wb.GetPartById(s.Id!.Value!) is not WorksheetPart part) continue;
                // Merged title cells above the header are normal; a merge inside the data is reported when the sheet is read.
                var merged = part.Worksheet?.Descendants<MergeCell>().Select(m => m.Reference?.Value!).Where(r => r is not null).ToList() ?? [];
                var rows = new List<Row>();
                foreach (var r in part.Worksheet?.Descendants<DocumentFormat.OpenXml.Spreadsheet.Row>() ?? [])
                {
                    var cells = r.Elements<Cell>().Select(c => Read(c, strings)).ToList();
                    rows.Add(new Row((int)(r.RowIndex?.Value ?? (uint)(rows.Count + 1)), r.Hidden?.Value == true, cells));
                }
                sheets.Add(new Sheet(name, rows, merged));
            }
            if (sheets.All(s => s.Rows.All(r => r.Cells.All(c => c.Value is null))))
            {
                result.Errors.Add(new(null, null, null, "The workbook is empty"));
                return;
            }

            var dupNames = result.Sheets.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            foreach (var d in dupNames) result.Errors.Add(new(d, null, null, "Two sheets share this name"));

            var header = sheets.FirstOrDefault(s => s.Name is "Approved_Requisitions" or "Requisitions" && Table(s, result, HeaderColumns, quiet: true) is not null);
            var lines = sheets.FirstOrDefault(s => s.Name == "Requisition_Lines");
            if (header is not null || lines is not null)
            {
                result.Format = "xlsx (client workbook layout)";
                if (header is null) { result.Errors.Add(new(null, null, null, $"Sheet 'Approved_Requisitions' with columns {string.Join(", ", HeaderColumns)} was not found (sheets: {string.Join(", ", result.Sheets)})")); return; }
                if (lines is null) { result.Errors.Add(new(null, null, null, $"Sheet 'Requisition_Lines' was not found (sheets: {string.Join(", ", result.Sheets)})")); return; }
                if (!result.Sheets.Contains("Reconciliation_Expected"))
                    result.Warnings.Add(new(null, null, null, "No Reconciliation_Expected sheet: the import works, but expected-vs-actual comparison is unavailable"));
                var h = Table(header, result, HeaderColumns, quiet: false);
                var l = Table(lines, result, LineColumns, quiet: false);
                if (h is null || l is null) return;
                BuildFromWorkbookLayout(h, l, result);
            }
            else
            {
                result.Format = "xlsx (flat: one row per line)";
                var first = sheets.FirstOrDefault(s => s.Rows.Any(r => r.Cells.Count(c => c.Value is not null) >= 3));
                if (first is null) { result.Errors.Add(new(null, null, null, $"No sheet has a header row (sheets: {string.Join(", ", result.Sheets)}); expected 'Approved_Requisitions' + 'Requisition_Lines' or one flat sheet")); return; }
                var t = Table(first, result, FlatColumns, quiet: false);
                if (t is null) return;
                BuildFromFlat(t, result);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or OpenXmlPackageException or IOException or InvalidOperationException or FileFormatException)
        {
            result.Errors.Add(new(null, null, null, $"The workbook is corrupt or unreadable ({ex.GetType().Name}: {ex.Message})"));
        }
    }

    private static CellValue Read(Cell c, string[] strings)
    {
        var column = new string((c.CellReference?.Value ?? "").TakeWhile(char.IsLetter).ToArray());
        var raw = c.CellValue?.Text;
        var formula = c.CellFormula is not null;
        if (c.DataType?.Value == CellValues.Error) return new CellValue(column, raw, formula, true, false);
        if (c.DataType?.Value == CellValues.InlineString) return new CellValue(column, c.InlineString?.InnerText, formula, false, false);
        if (raw is null) return new CellValue(column, null, formula, false, false);
        if (c.DataType?.Value == CellValues.SharedString) return new CellValue(column, strings[int.Parse(raw, CultureInfo.InvariantCulture)], formula, false, false);
        if (c.DataType?.Value == CellValues.Boolean) return new CellValue(column, raw == "1", formula, false, false);
        if (c.DataType?.Value == CellValues.String) return new CellValue(column, raw, formula, false, false);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? new CellValue(column, d, formula, false, true)
            : new CellValue(column, raw, formula, false, false);
    }

    private sealed record TableRow(string Sheet, int Number, Dictionary<string, (object? Value, bool Numeric, string Column)> Values);

    /// <summary>Finds the header row (first with 3+ filled cells), checks the columns, and returns the data rows.</summary>
    private static List<TableRow>? Table(Sheet sheet, ImportResult result, string[] required, bool quiet)
    {
        var headerRow = sheet.Rows.FirstOrDefault(r => r.Cells.Count(c => c.Value is not null) >= 3);
        if (headerRow is null)
        {
            if (!quiet) result.Errors.Add(new(sheet.Name, null, null, "No header row found"));
            return null;
        }
        var headers = headerRow.Cells.Where(c => c.Value is not null).Select(c => (c.Column, Name: Convert.ToString(c.Value, CultureInfo.InvariantCulture)!.Trim())).ToList();
        var missing = required.Where(r => !headers.Any(h => string.Equals(h.Name, r, StringComparison.OrdinalIgnoreCase))).ToList();
        if (quiet) return missing.Count == 0 ? [] : null;
        foreach (var m in missing) result.Errors.Add(new(sheet.Name, headerRow.Number, m, $"Required column '{m}' is missing (renamed or deleted?)"));
        foreach (var d in headers.GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            result.Errors.Add(new(sheet.Name, headerRow.Number, d.Key, $"Column '{d.Key}' appears {d.Count()} times"));
        if (missing.Count > 0 || headers.GroupBy(h => h.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) return null;

        var mergedRows = new HashSet<int>();
        foreach (var m in sheet.Merges ?? [])
        {
            var parts = m.Split(':');
            var from = RowOf(parts[0]) ?? 0;
            var to = RowOf(parts.Length > 1 ? parts[1] : parts[0]) ?? from;
            if (to <= headerRow.Number) continue;
            result.Errors.Add(new(sheet.Name, Math.Max(from, headerRow.Number + 1), m, "Merged cells are ambiguous; unmerge and repeat the value in each row"));
            for (var r = from; r <= to; r++) mergedRows.Add(r);
        }

        var rows = new List<TableRow>();
        foreach (var r in sheet.Rows.Where(r => r.Number > headerRow.Number))
        {
            if (r.Cells.All(c => c.Value is null)) { result.RowsSkipped++; continue; }
            result.RowsRead++;
            if (mergedRows.Contains(r.Number)) { result.RowsSkipped++; continue; }
            if (r.Hidden)
            {
                result.Warnings.Add(new(sheet.Name, r.Number, null, "Hidden row ignored: unhide it if it should be imported"));
                result.RowsSkipped++;
                continue;
            }
            var bad = false;
            var values = new Dictionary<string, (object?, bool, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (column, name) in headers)
            {
                var cell = r.Cells.FirstOrDefault(c => c.Column == column);
                if (cell is null) { values[name] = (null, false, column); continue; }
                if (cell.Formula) { result.Errors.Add(new(sheet.Name, r.Number, $"{name} ({column}{r.Number})", "Contains a formula; formulas are not evaluated. Enter the value itself")); bad = true; continue; }
                if (cell.ErrorValue) { result.Errors.Add(new(sheet.Name, r.Number, $"{name} ({column}{r.Number})", $"Contains the spreadsheet error {cell.Value}")); bad = true; continue; }
                values[name] = (cell.Value, cell.Numeric, column);
            }
            if (bad) { result.RowsSkipped++; continue; }
            rows.Add(new TableRow(sheet.Name, r.Number, values));
        }
        if (rows.Count == 0 && !result.Errors.Any(e => e.Sheet == sheet.Name)) result.Errors.Add(new(sheet.Name, null, null, "The sheet has a header but no data rows"));
        return rows;
    }

    private static void BuildFromWorkbookLayout(List<TableRow> headers, List<TableRow> lines, ImportResult result)
    {
        var directory = EmployeeDirectory.Load();
        var linesByReq = new Dictionary<string, List<(TableRow Row, JsonObject Line)>>(StringComparer.Ordinal);
        foreach (var row in lines)
        {
            var id = Text(row, "Source_Requisition_ID", result, required: true);
            if (id is null) continue;
            var line = new JsonObject
            {
                ["lineNo"] = Number(row, "Line_Number", result, integer: true),
                ["materialCode"] = Code(row, "Item_Code", result),
                ["description"] = Text(row, "Description", result, required: true),
                ["quantity"] = Number(row, "Quantity", result),
                ["uom"] = Text(row, "Unit", result, required: true),
                ["unitPrice"] = Number(row, row.Values.ContainsKey("Unit_Price_QAR") ? "Unit_Price_QAR" : "Unit_Price", result),
                ["plant"] = DataPack.Plant,
                ["taxCode"] = Code(row, "Tax_Code", result),
                ["costCenter"] = Code(row, "Cost_Centre", result),
            };
            if (!linesByReq.TryGetValue(id, out var list)) linesByReq[id] = list = [];
            if (list.FirstOrDefault(x => x.Line["lineNo"]?.ToJsonString() == line["lineNo"]?.ToJsonString()) is { Line: not null } existing)
            {
                if (JsonNode.DeepEquals(existing.Line, line)) { result.Warnings.Add(new(row.Sheet, row.Number, null, $"Duplicate of row {existing.Row.Number} ({id} line {line["lineNo"]}); ignored")); continue; }
                result.Errors.Add(new(row.Sheet, row.Number, "Line_Number", $"{id} line {line["lineNo"]} also appears in row {existing.Row.Number} with different values"));
                continue;
            }
            list.Add((row, line));
        }

        var seen = new Dictionary<string, (TableRow Row, JsonObject Record)>(StringComparer.Ordinal);
        foreach (var row in headers)
        {
            var id = Text(row, "Source_Requisition_ID", result, required: true);
            if (id is null) continue;
            var status = Text(row, "Approval_Status", result, required: false);
            var record = new JsonObject
            {
                ["id"] = id,
                ["status"] = status == "Pending" ? "PendingApproval" : status,
                ["requester"] = directory.RequisitionRequesters.GetValueOrDefault(id) ?? "imported@lab.test",
                ["department"] = row.Values.ContainsKey("Requester_Dept") ? Text(row, "Requester_Dept", result, required: false) : null,
                ["companyCode"] = DataPack.CompanyCode,
                ["currency"] = Text(row, "Currency", result, required: false),
                ["neededBy"] = Date(row, "Required_By", result),
                ["justification"] = row.Values.ContainsKey("Business_Purpose") ? Text(row, "Business_Purpose", result, required: false) : null,
                ["approvedAt"] = status == "Approved" ? DateTime.UtcNow.AddHours(-2).ToString("O") : null,
                ["totalAmount"] = Number(row, "Total_Amount", result),
                ["erpSync"] = null,
            };
            var version = Number(row, "Source_Version", result, integer: true);
            record["importVersion"] = version;
            if (seen.TryGetValue(id, out var prior))
            {
                if (JsonNode.DeepEquals(prior.Record, record)) { result.Warnings.Add(new(row.Sheet, row.Number, null, $"Duplicate of row {prior.Row.Number} ({id}); ignored")); continue; }
                result.Errors.Add(new(row.Sheet, row.Number, "Source_Requisition_ID", $"{id} also appears in row {prior.Row.Number} with different values (conflicting records)"));
                continue;
            }
            seen[id] = (row, record);
        }

        foreach (var (id, (row, record)) in seen)
        {
            var reqLines = linesByReq.GetValueOrDefault(id) ?? [];
            if (reqLines.Count == 0) result.Warnings.Add(new(row.Sheet, row.Number, "Source_Requisition_ID", $"{id} has no lines in Requisition_Lines"));
            var array = new JsonArray();
            foreach (var (_, line) in reqLines.OrderBy(l => l.Line["lineNo"] is JsonValue v && v.TryGetValue<long>(out var no) ? no : 0))
            {
                record["costCenter"] ??= line["costCenter"]?.DeepClone();
                line.Remove("costCenter");
                array.Add(line);
            }
            record["lines"] = array;
            result.Lines += array.Count;
            result.Records.Add(record);
        }
        foreach (var orphan in linesByReq.Keys.Where(k => !seen.ContainsKey(k)))
            result.Errors.Add(new("Requisition_Lines", linesByReq[orphan][0].Row.Number, "Source_Requisition_ID", $"Lines for {orphan}, which is not in the requisition sheet"));
    }

    private static void BuildFromFlat(List<TableRow> rows, ImportResult result)
    {
        var records = new Dictionary<string, (TableRow Row, JsonObject Record)>(StringComparer.Ordinal);
        var approvedAt = DateTime.UtcNow.AddHours(-2).ToString("O");
        foreach (var row in rows)
        {
            var id = Text(row, "requisition_id", result, required: true);
            if (id is null) continue;
            var header = new JsonObject
            {
                ["id"] = id,
                ["status"] = Text(row, "status", result, required: false),
                ["requester"] = Text(row, "requester", result, required: false),
                ["costCenter"] = Code(row, "cost_center", result),
                ["companyCode"] = Code(row, "company_code", result),
                ["currency"] = Text(row, "currency", result, required: false),
                ["neededBy"] = Date(row, "needed_by", result),
                ["justification"] = row.Values.ContainsKey("justification") ? Text(row, "justification", result, required: false) : null,
                ["totalAmount"] = Number(row, "total_amount", result),
                ["erpSync"] = null,
            };
            if (header["status"]?.GetValue<string>() == "Approved") header["approvedAt"] = approvedAt;
            var line = new JsonObject
            {
                ["lineNo"] = Number(row, "line_no", result, integer: true),
                ["materialCode"] = row.Values.ContainsKey("material_code") ? Code(row, "material_code", result) : null,
                ["description"] = Text(row, "description", result, required: true),
                ["quantity"] = Number(row, "quantity", result),
                ["uom"] = Text(row, "uom", result, required: true),
                ["unitPrice"] = Number(row, "unit_price", result),
                ["plant"] = Code(row, "plant", result),
            };
            if (!records.TryGetValue(id, out var existing))
            {
                header["lines"] = new JsonArray(line);
                records[id] = (row, header);
                continue;
            }
            var compareA = existing.Record.DeepClone().AsObject();
            compareA.Remove("lines");
            if (!JsonNode.DeepEquals(compareA, header))
            {
                result.Errors.Add(new(row.Sheet, row.Number, "requisition_id", $"{id}: header values differ from row {existing.Row.Number} (conflicting records)"));
                continue;
            }
            var lines = (JsonArray)existing.Record["lines"]!;
            var same = lines.FirstOrDefault(l => l!["lineNo"]?.ToJsonString() == line["lineNo"]?.ToJsonString());
            if (same is not null)
            {
                if (JsonNode.DeepEquals(same, line)) result.Warnings.Add(new(row.Sheet, row.Number, null, $"Duplicate row ({id} line {line["lineNo"]}); ignored"));
                else result.Errors.Add(new(row.Sheet, row.Number, "line_no", $"{id} line {line["lineNo"]} appears twice with different values"));
                continue;
            }
            lines.Add(line);
        }
        foreach (var (_, (_, record)) in records)
        {
            result.Lines += ((JsonArray)record["lines"]!).Count;
            result.Records.Add(record);
        }
    }

    private static void ReadCsv(byte[] content, ImportResult result)
    {
        result.Format = "csv (flat: one row per line)";
        result.Sheets.Add("csv");
        var span = content.AsSpan();
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
            result.Warnings.Add(new("csv", null, null, "UTF-8 byte order mark found and ignored"));
        }
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(span);
        }
        catch (DecoderFallbackException ex)
        {
            result.Errors.Add(new("csv", null, null, $"The file is not valid UTF-8 (byte {ex.Index}); save it as UTF-8 CSV"));
            return;
        }

        var records = ParseCsv(text);
        if (records.Count == 0 || records.All(r => r.Cells.All(string.IsNullOrWhiteSpace))) { result.Errors.Add(new("csv", null, null, "The file is empty")); return; }
        var header = records[0];
        var sheet = new Sheet("csv", records.Select(r => new Row(r.Line, false,
            r.Cells.Select((v, i) => new CellValue(ColumnName(i), v.Length == 0 ? null : v, false, false, false)).ToList())).ToList());
        _ = header;
        var t = Table(sheet, result, FlatColumns, quiet: false);
        if (t is null) return;
        BuildFromFlat(t, result);
    }

    private sealed record CsvRecord(int Line, List<string> Cells);

    /// <summary>RFC 4180: quoted fields may contain commas, quotes ("") and line breaks.</summary>
    private static List<CsvRecord> ParseCsv(string text)
    {
        var records = new List<CsvRecord>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var line = 1;
        var start = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else { if (ch == '\n') line++; cell.Append(ch); }
                continue;
            }
            switch (ch)
            {
                case '"' when cell.Length == 0: quoted = true; break;
                case ',': cells.Add(cell.ToString()); cell.Clear(); break;
                case '\r': break;
                case '\n':
                    cells.Add(cell.ToString()); cell.Clear();
                    records.Add(new CsvRecord(start, cells));
                    cells = [];
                    line++;
                    start = line;
                    break;
                default: cell.Append(ch); break;
            }
        }
        if (cell.Length > 0 || cells.Count > 0) { cells.Add(cell.ToString()); records.Add(new CsvRecord(start, cells)); }
        return records;
    }

    private static string ColumnName(int i)
    {
        var s = "";
        for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
        return s;
    }

    private static string Where(TableRow row, string name) => row.Values.TryGetValue(name, out var v) ? $"{name} ({v.Column}{row.Number})" : name;

    private static string? Text(TableRow row, string name, ImportResult result, bool required)
    {
        if (!row.Values.TryGetValue(name, out var v) || v.Value is null)
        {
            if (required) result.Errors.Add(new(row.Sheet, row.Number, name, "Required value is empty"));
            return null;
        }
        var s = Convert.ToString(v.Value, CultureInfo.InvariantCulture)!;
        if (s.Length > 0 && "=+-@".Contains(s[0]) && !decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            result.Errors.Add(new(row.Sheet, row.Number, Where(row, name), "Starts with = + - or @ (possible formula injection); not imported"));
            return null;
        }
        return s;
    }

    private static string? Code(TableRow row, string name, ImportResult result)
    {
        if (!row.Values.TryGetValue(name, out var v) || v.Value is null) return null;
        if (v.Numeric && v.Value is double d)
        {
            var s = d.ToString("0.##########", CultureInfo.InvariantCulture);
            result.Warnings.Add(new(row.Sheet, row.Number, Where(row, name), $"Code stored as a number ({s}); leading zeros may have been lost. Format the column as text"));
            return s;
        }
        return Text(row, name, result, required: false);
    }

    private static JsonValue? Number(TableRow row, string name, ImportResult result, bool integer = false)
    {
        if (!row.Values.TryGetValue(name, out var v) || v.Value is null) return null;
        if (v.Value is double d)
        {
            if (integer && d != Math.Floor(d)) { result.Errors.Add(new(row.Sheet, row.Number, Where(row, name), $"{d} is not a whole number")); return null; }
            return integer ? JsonValue.Create((long)d) : JsonValue.Create((decimal)d);
        }
        var s = Convert.ToString(v.Value, CultureInfo.InvariantCulture)!.Trim();
        if (decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var n))
        {
            if (integer && n != decimal.Truncate(n)) { result.Errors.Add(new(row.Sheet, row.Number, Where(row, name), $"{s} is not a whole number")); return null; }
            return integer ? JsonValue.Create((long)n) : JsonValue.Create(n);
        }
        var why = s.IndexOfAny(['$', '€', '£', '¥']) >= 0 || s.Contains("QAR", StringComparison.OrdinalIgnoreCase) ? "contains a currency symbol"
            : s.Contains(',') && s.Contains('.') ? "contains a thousands separator"
            : s.Contains(',') ? "uses a decimal comma"
            : "is not a number";
        result.Errors.Add(new(row.Sheet, row.Number, Where(row, name), $"'{s}' {why}; enter a plain number like 1234.56"));
        return null;
    }

    private static string? Date(TableRow row, string name, ImportResult result)
    {
        if (!row.Values.TryGetValue(name, out var v) || v.Value is null) return null;
        if (v.Value is double serial)
        {
            if (serial < 1 || serial > 2958465) { result.Errors.Add(new(row.Sheet, row.Number, Where(row, name), $"{serial} is not a valid date serial number")); return null; }
            return DateOnly.FromDateTime(DateTime.FromOADate(serial)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        var s = Convert.ToString(v.Value, CultureInfo.InvariantCulture)!.Trim();
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        result.Errors.Add(new(row.Sheet, row.Number, Where(row, name), $"'{s}' is not a date in yyyy-MM-dd (or an Excel date); ambiguous formats like 03/04/2027 are refused"));
        return null;
    }

    private static int? RowOf(string reference) => int.TryParse(new string(reference.SkipWhile(char.IsLetter).ToArray()), out var r) ? r : null;
}
