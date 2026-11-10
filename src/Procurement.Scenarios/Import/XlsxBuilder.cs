using System.Globalization;
using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Procurement.Scenarios.Import;

/// <summary>Builds small .xlsx files, including awkward ones (hidden rows, formulas, merges).</summary>
public sealed class XlsxBuilder
{
    public sealed record XCell(object? Value, string? Formula = null, string? Error = null, bool AsText = false);

    private sealed record XRow(List<XCell?> Cells, bool Hidden);
    private sealed record XSheet(string Name, List<XRow> Rows, List<string> Merges);

    private readonly List<XSheet> _sheets = [];
    private XSheet Current => _sheets[^1];

    public XlsxBuilder Sheet(string name)
    {
        _sheets.Add(new XSheet(name, [], []));
        return this;
    }

    public XlsxBuilder Row(params object?[] cells) => AddRow(false, cells);
    public XlsxBuilder HiddenRow(params object?[] cells) => AddRow(true, cells);
    public XlsxBuilder Merge(string range) { Current.Merges.Add(range); return this; }

    private XlsxBuilder AddRow(bool hidden, object?[] cells)
    {
        Current.Rows.Add(new XRow(cells.Select(c => c as XCell ?? (c is null ? null : new XCell(c))).ToList(), hidden));
        return this;
    }

    public static XCell Formula(string formula, object? cached = null) => new(cached, formula);
    public static XCell ErrorCell(string error) => new(null, Error: error);
    public static XCell Text(string value) => new(value, AsText: true);

    public byte[] Build(bool withMacroPart = false)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wb = doc.AddWorkbookPart();
            wb.Workbook = new Workbook();
            var sheets = wb.Workbook.AppendChild(new Sheets());
            uint id = 1;
            foreach (var s in _sheets)
            {
                var part = wb.AddNewPart<WorksheetPart>();
                var data = new SheetData();
                uint r = 1;
                foreach (var row in s.Rows)
                {
                    var xr = new Row { RowIndex = r };
                    if (row.Hidden) xr.Hidden = true;
                    for (var c = 0; c < row.Cells.Count; c++)
                    {
                        var cell = row.Cells[c];
                        if (cell is null) continue;
                        var xc = new Cell { CellReference = Col(c) + r };
                        if (cell.Error is not null) { xc.DataType = CellValues.Error; xc.CellValue = new CellValue(cell.Error); }
                        else
                        {
                            if (cell.Formula is not null) xc.CellFormula = new CellFormula(cell.Formula);
                            switch (cell.Value)
                            {
                                case null: break;
                                case string str:
                                    xc.DataType = CellValues.InlineString;
                                    xc.InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(str));
                                    break;
                                case bool b: xc.DataType = CellValues.Boolean; xc.CellValue = new CellValue(b ? "1" : "0"); break;
                                case DateOnly d: xc.CellValue = new CellValue(d.ToDateTime(TimeOnly.MinValue).ToOADate().ToString(CultureInfo.InvariantCulture)); break;
                                default: xc.CellValue = new CellValue(Convert.ToString(cell.Value, CultureInfo.InvariantCulture)!); break;
                            }
                        }
                        xr.Append(xc);
                    }
                    data.Append(xr);
                    r++;
                }
                part.Worksheet = new Worksheet(data);
                if (s.Merges.Count > 0) part.Worksheet.Append(new MergeCells(s.Merges.Select(m => new MergeCell { Reference = m })));
                sheets.Append(new Sheet { Id = wb.GetIdOfPart(part), SheetId = id++, Name = s.Name });
            }
        }
        var bytes = ms.ToArray();
        if (!withMacroPart) return bytes;
        // The content is irrelevant: the importer refuses a workbook with a vbaProject.bin part unread.
        using var copy = new MemoryStream();
        copy.Write(bytes);
        using (var zip = new ZipArchive(copy, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.CreateEntry("xl/vbaProject.bin");
            using var w = entry.Open();
            w.Write("Attribute VB_Name = \"Module1\"\nSub AutoOpen(): Shell \"calc\": End Sub"u8);
        }
        return copy.ToArray();
    }

    private static string Col(int i)
    {
        var s = "";
        for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
        return s;
    }
}
