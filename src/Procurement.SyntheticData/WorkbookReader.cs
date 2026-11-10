using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Procurement.SyntheticData;

// Returns the rows below each sheet's header keyed by column name. Values are string, double, bool or null, as Excel stores them.
public static class WorkbookReader
{
    public static Dictionary<string, List<Dictionary<string, object?>>> ReadAll(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = SpreadsheetDocument.Open(stream, isEditable: false);
        var workbook = document.WorkbookPart ?? throw new InvalidDataException($"{path} has no workbook part");
        var strings = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(s => s.InnerText).ToArray() ?? [];

        var result = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var sheet in workbook.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            var part = (WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
            var rows = (part.Worksheet?.Descendants<Row>() ?? []).Select(r => ReadRow(r, strings)).ToList();
            // The header is the first row with three or more filled cells (title and note rows have one).
            var headerIndex = rows.FindIndex(r => r.Count(v => v is not null) >= 3);
            if (headerIndex < 0) continue;
            var header = rows[headerIndex];
            result[sheet.Name!.Value!] = rows.Skip(headerIndex + 1)
                .Where(r => r.Any(v => v is not null))
                .Select(r => header.Select((h, i) => (Name: h as string, Value: i < r.Count ? r[i] : null))
                    .Where(c => c.Name is not null)
                    .ToDictionary(c => c.Name!, c => c.Value, StringComparer.Ordinal))
                .ToList();
        }
        return result;
    }

    private static List<object?> ReadRow(Row row, string[] strings)
    {
        var values = new List<object?>();
        foreach (var cell in row.Elements<Cell>())
        {
            var column = ColumnIndex(cell.CellReference?.Value) ?? values.Count;
            while (values.Count < column) values.Add(null);
            values.Add(ReadCell(cell, strings));
        }
        return values;
    }

    private static object? ReadCell(Cell cell, string[] strings)
    {
        var raw = cell.CellValue?.Text;
        var type = cell.DataType?.Value;
        if (type == CellValues.InlineString) return cell.InlineString?.InnerText;
        if (raw is null) return null;
        if (type == CellValues.SharedString) return strings[int.Parse(raw, System.Globalization.CultureInfo.InvariantCulture)];
        if (type == CellValues.Boolean) return raw == "1";
        if (type == CellValues.String) return raw;
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : raw;
    }

    // "C12" is column 2.
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var index = 0;
        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch)) break;
            index = index * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return index - 1;
    }
}
