const FORMULA_START = /^[=+\-@\t\r]/;

// Spreadsheets run text starting with = + - @, tab or CR as a formula, so it gets a leading quote.
export function csvCell(value: unknown): string {
  let s: string;
  if (value === null || value === undefined) s = "";
  else if (typeof value === "string") s = value;
  else if (typeof value === "number" || typeof value === "boolean") s = String(value);
  else s = JSON.stringify(value);

  const isPlainNumber = /^[-+]?\d+(\.\d+)?([eE][-+]?\d+)?$/.test(s);
  if (s.length > 0 && FORMULA_START.test(s) && !isPlainNumber) s = `'${s}`;
  if (/[",\r\n]/.test(s)) s = `"${s.replace(/"/g, '""')}"`;
  return s;
}

export function toCsv<T extends object>(rows: T[], columns?: { key: keyof T & string; header?: string }[]): string {
  const cols: { key: keyof T & string; header?: string }[] = columns ?? (rows[0] ? Object.keys(rows[0]).map((key) => ({ key: key as keyof T & string })) : []);
  const header = cols.map((c) => csvCell(c.header ?? c.key)).join(",");
  const lines = rows.map((r) => cols.map((c) => csvCell((r as Record<string, unknown>)[c.key])).join(","));
  return [header, ...lines].join("\r\n") + "\r\n";
}
