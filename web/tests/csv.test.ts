import { describe, expect, it } from "vitest";
import { csvCell, toCsv } from "@/lib/csv";

describe("csvCell formula neutralisation", () => {
  it.each([
    ["=1+1", "'=1+1"],
    ["+SUM(A1)", "'+SUM(A1)"],
    ["-2+3", "'-2+3"],
    ["@cmd", "'@cmd"],
    ['=HYPERLINK("http://evil")', `"'=HYPERLINK(""http://evil"")"`],
    ["\tTAB", "'\tTAB"],
  ])("prefixes %j", (input, expected) => {
    expect(csvCell(input)).toBe(expected);
  });

  it("leaves plain numbers (including negatives) alone", () => {
    expect(csvCell(-5)).toBe("-5");
    expect(csvCell("-5")).toBe("-5");
    expect(csvCell(12.5)).toBe("12.5");
  });

  it("quotes commas, quotes and line breaks", () => {
    expect(csvCell('a,"b"')).toBe('"a,""b"""');
    expect(csvCell("line1\nline2")).toBe('"line1\nline2"');
  });

  it("renders null/undefined as empty and objects as JSON", () => {
    expect(csvCell(null)).toBe("");
    expect(csvCell(undefined)).toBe("");
    expect(csvCell({ a: 1 })).toBe('"{""a"":1}"');
  });

  it("builds a CSV with headers and only the chosen columns", () => {
    const csv = toCsv([{ id: "REQ-1", note: "=cmd|' /C calc'!A0", n: 2 }], [{ key: "id" }, { key: "note", header: "Note" }]);
    expect(csv).toBe("id,Note\r\nREQ-1,'=cmd|' /C calc'!A0\r\n");
  });
});
