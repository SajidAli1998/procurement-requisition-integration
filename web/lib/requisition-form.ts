import { z } from "zod";
import type { JsonObject, JsonValue, SourceLine, SourceRecord } from "./types";

// Only structural rules (this schema) block saving. Business rules just warn: the lab exists to push bad
// records through the real pipeline and watch them get quarantined.

const NUMBER = /^-?\d+(\.\d+)?$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;

const numeric = (label: string, required: boolean) =>
  z
    .string()
    .trim()
    .refine((v) => (required ? v !== "" : true), { message: `${label} is required` })
    .refine((v) => v === "" || NUMBER.test(v), { message: `${label} must be a number (digits, optional minus sign, "." for decimals)` });

const isRealDate = (v: string) => {
  if (!DATE.test(v)) return false;
  const d = new Date(`${v}T00:00:00Z`);
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === v;
};

const lineSchema = z.object({
  lineNo: z
    .string()
    .trim()
    .min(1, "Line number is required")
    .refine((v) => /^\d+$/.test(v) && Number(v) > 0, { message: "Line number must be a whole number above 0" }),
  materialCode: z.string().max(200, "Material code is at most 200 characters"),
  description: z.string().max(2000, "Description is at most 2000 characters"),
  materialGroup: z.string().max(200),
  quantity: numeric("Quantity", true),
  uom: z.string().max(20, "Unit of measure is at most 20 characters"),
  unitPrice: numeric("Unit price", true),
  plant: z.string().max(20, "Plant is at most 20 characters"),
  taxCode: z.string().max(20),
});

export const requisitionFormSchema = z
  .object({
    id: z
      .string()
      .trim()
      .min(1, "Requisition ID is required")
      .max(64, "Requisition ID is at most 64 characters")
      .regex(/^[A-Za-z0-9._-]+$/, "Requisition ID may only contain letters, digits, '.', '-' and '_'"),
    status: z.string(),
    approvedAt: z
      .string()
      .trim()
      .refine((v) => v === "" || !Number.isNaN(new Date(v).getTime()), { message: "Approved at must be a date and time" }),
    requester: z.string().max(200),
    department: z.string().max(200),
    costCenter: z.string().max(50),
    companyCode: z.string().max(20),
    currency: z.string().max(10, "Currency is at most 10 characters"),
    neededBy: z
      .string()
      .trim()
      .refine((v) => v === "" || isRealDate(v), { message: "Needed by must be a real date (yyyy-mm-dd)" }),
    justification: z.string().max(5000, "Justification is at most 5000 characters"),
    overrideTotal: z.boolean(),
    totalAmount: numeric("Total amount", false),
    lines: z.array(lineSchema).min(1, "Add at least one line item"),
  })
  .superRefine((v, ctx) => {
    if (v.overrideTotal && v.totalAmount.trim() === "") {
      ctx.addIssue({ code: "custom", path: ["totalAmount"], message: "Enter the total to send, or turn off the override" });
    }
  });

export type RequisitionForm = z.infer<typeof requisitionFormSchema>;
type LineForm = z.infer<typeof lineSchema>;

export const STATUSES = ["Approved", "Pending", "Draft", "Rejected", "Submitted"] as const;

export function emptyLine(lineNo: number): LineForm {
  return { lineNo: String(lineNo), materialCode: "", description: "", materialGroup: "", quantity: "1", uom: "EA", unitPrice: "0", plant: "", taxCode: "" };
}

export function emptyForm(): RequisitionForm {
  return {
    id: "",
    status: "Approved",
    approvedAt: "",
    requester: "",
    department: "",
    costCenter: "",
    companyCode: "",
    currency: "",
    neededBy: "",
    justification: "",
    overrideTotal: false,
    totalAmount: "",
    lines: [emptyLine(10)],
  };
}

const toNum = (v: string): number | null => (v.trim() === "" || !NUMBER.test(v.trim()) ? null : Number(v.trim()));

export function linesTotal(lines: Pick<LineForm, "quantity" | "unitPrice">[]): number {
  const sum = lines.reduce((acc, l) => acc + (toNum(l.quantity) ?? 0) * (toNum(l.unitPrice) ?? 0), 0);
  return Math.round(sum * 100) / 100;
}

const FORM_KEYS = new Set([
  "id",
  "status",
  "approvedAt",
  "requester",
  "department",
  "costCenter",
  "companyCode",
  "currency",
  "neededBy",
  "justification",
  "totalAmount",
  "lines",
]);
const LINE_KEYS = new Set(["lineNo", "materialCode", "description", "materialGroup", "quantity", "uom", "unitPrice", "plant", "taxCode", "lineTotal"]);

export interface Extras {
  record: JsonObject;
  lines: JsonObject[];
}

const str = (v: JsonValue | undefined): string => (v === null || v === undefined ? "" : typeof v === "string" ? v : typeof v === "object" ? JSON.stringify(v) : String(v));

export function recordToForm(record: SourceRecord): { form: RequisitionForm; extras: Extras } {
  const lines = Array.isArray(record.lines) ? (record.lines as SourceLine[]) : [];
  const formLines: LineForm[] = lines.map((l, i) => ({
    lineNo: l && l.lineNo !== undefined && l.lineNo !== null ? str(l.lineNo) : String((i + 1) * 10),
    materialCode: str(l?.materialCode),
    description: str(l?.description),
    materialGroup: str(l?.materialGroup),
    quantity: str(l?.quantity),
    uom: str(l?.uom),
    unitPrice: str(l?.unitPrice),
    plant: str(l?.plant),
    taxCode: str(l?.taxCode),
  }));
  const total = str(record.totalAmount);
  const computed = linesTotal(formLines);
  const form: RequisitionForm = {
    id: str(record.id),
    status: str(record.status),
    approvedAt: str(record.approvedAt),
    requester: str(record.requester),
    department: str(record.department),
    costCenter: str(record.costCenter),
    companyCode: str(record.companyCode),
    currency: str(record.currency),
    neededBy: str(record.neededBy),
    justification: str(record.justification),
    overrideTotal: total !== "" && toNum(total) !== computed,
    totalAmount: total,
    lines: formLines.length > 0 ? formLines : [emptyLine(10)],
  };
  const extraRecord: JsonObject = {};
  for (const [k, v] of Object.entries(record)) if (!FORM_KEYS.has(k) && v !== undefined) extraRecord[k] = v as JsonValue;
  const extraLines = lines.map((l) => {
    const e: JsonObject = {};
    if (l && typeof l === "object") for (const [k, v] of Object.entries(l)) if (!LINE_KEYS.has(k) && v !== undefined) e[k] = v as JsonValue;
    return e;
  });
  return { form, extras: { record: extraRecord, lines: extraLines } };
}

const textOrNull = (v: string): string | null => (v.trim() === "" ? null : v);

// Empty text becomes null so "missing" stays missing.
export function formToRecord(form: RequisitionForm, extras?: Extras): SourceRecord {
  const lines: SourceLine[] = form.lines.map((l, i) => {
    const q = toNum(l.quantity);
    const p = toNum(l.unitPrice);
    return {
      ...(extras?.lines[i] ?? {}),
      lineNo: toNum(l.lineNo) ?? (i + 1) * 10,
      materialCode: textOrNull(l.materialCode),
      description: textOrNull(l.description),
      materialGroup: textOrNull(l.materialGroup),
      quantity: q,
      uom: textOrNull(l.uom),
      unitPrice: p,
      lineTotal: q !== null && p !== null ? Math.round(q * p * 100) / 100 : null,
      plant: textOrNull(l.plant),
      taxCode: textOrNull(l.taxCode),
    };
  });
  const total = form.overrideTotal ? toNum(form.totalAmount) : linesTotal(form.lines);
  return {
    ...(extras?.record ?? {}),
    id: form.id.trim(),
    status: textOrNull(form.status),
    requester: textOrNull(form.requester),
    department: textOrNull(form.department),
    costCenter: textOrNull(form.costCenter),
    companyCode: textOrNull(form.companyCode),
    currency: textOrNull(form.currency),
    neededBy: textOrNull(form.neededBy),
    justification: textOrNull(form.justification),
    approvedAt: textOrNull(form.approvedAt),
    totalAmount: total,
    lines,
  };
}

export const KNOWN_CURRENCIES = ["USD", "EUR", "GBP", "AED", "SAR", "QAR"];
export const KNOWN_UNITS = ["EA", "BOX", "KG", "L", "M", "H"];
export const KNOWN_PLANTS = ["1100", "1200", "2100", "2200", "3100"];
const WHOLE_UNITS = new Set(["EA", "BOX"]);

interface BusinessWarning {
  field: string;
  message: string;
}

// Mirrors the integration's rules for feedback while typing; the backend validation has the final say.
export function businessWarnings(form: RequisitionForm, today = new Date()): BusinessWarning[] {
  const w: BusinessWarning[] = [];
  const req = (field: keyof RequisitionForm, label: string) => {
    const v = form[field];
    if (typeof v === "string" && v.trim() === "") w.push({ field, message: `${label} is missing: the integration will quarantine this record (MissingRequiredField)` });
  };
  req("requester", "Requester");
  req("costCenter", "Cost center");
  req("companyCode", "Company code");
  req("currency", "Currency");
  req("neededBy", "Needed by");
  if (form.status.trim() !== "Approved") w.push({ field: "status", message: `Status "${form.status || "(empty)"}" is not Approved: the record will be skipped, not sent to SAP` });
  if (form.currency.trim() !== "" && !KNOWN_CURRENCIES.includes(form.currency.trim())) w.push({ field: "currency", message: `Currency '${form.currency}' is not supported (UnknownCurrency)` });
  if (DATE.test(form.neededBy)) {
    const todayIso = today.toISOString().slice(0, 10);
    if (form.neededBy < todayIso) w.push({ field: "neededBy", message: `Needed by ${form.neededBy} is in the past (NeededByInPast)` });
  }
  if (form.approvedAt.trim() !== "") {
    const a = new Date(form.approvedAt);
    if (!Number.isNaN(a.getTime()) && a.getTime() > today.getTime() + 5 * 60_000) w.push({ field: "approvedAt", message: "Approved at is in the future (InvalidApproval)" });
  }
  const computed = linesTotal(form.lines);
  const total = form.overrideTotal ? toNum(form.totalAmount) : computed;
  if (total === null || total <= 0) w.push({ field: "totalAmount", message: `Total amount must be greater than zero (got ${total ?? "nothing"}) (AmountNotPositive)` });
  else if (Math.abs(total - computed) > 0.01) w.push({ field: "totalAmount", message: `Total ${total} does not equal the sum of the lines (${computed}) (TotalMismatch)` });
  form.lines.forEach((l, i) => {
    const q = toNum(l.quantity);
    const p = toNum(l.unitPrice);
    const label = `Line ${l.lineNo || (i + 1) * 10}`;
    if (q !== null && q <= 0) w.push({ field: `lines.${i}.quantity`, message: `${label}: quantity ${q} must be greater than zero (InvalidQuantity)` });
    if (q !== null && q > 0 && WHOLE_UNITS.has(l.uom.trim()) && !Number.isInteger(q)) w.push({ field: `lines.${i}.quantity`, message: `${label}: quantity ${q} must be a whole number for unit ${l.uom} (InvalidQuantity)` });
    if (p !== null && p < 0) w.push({ field: `lines.${i}.unitPrice`, message: `${label}: unit price ${p} must not be negative (InvalidPrice)` });
    if (l.uom.trim() !== "" && !KNOWN_UNITS.includes(l.uom.trim())) w.push({ field: `lines.${i}.uom`, message: `${label}: unit '${l.uom}' is not recognised (UnknownUnitOfMeasure)` });
    if (l.plant.trim() === "" || !KNOWN_PLANTS.includes(l.plant.trim())) w.push({ field: `lines.${i}.plant`, message: `${label}: plant '${l.plant}' is not recognised (UnknownPlant)` });
    if (l.materialCode.trim() === "") w.push({ field: `lines.${i}.materialCode`, message: `${label}: material code is missing` });
  });
  return w;
}

export interface Template {
  id: string;
  name: string;
  description: string;
  // For templates an object cannot represent, such as duplicate keys.
  raw?: string;
  record?: SourceRecord;
}

function isoDate(daysFromNow: number, now = new Date()): string {
  const d = new Date(now.getTime() + daysFromNow * 86_400_000);
  return d.toISOString().slice(0, 10);
}

export function newLabId(prefix = "REQ-LAB"): string {
  const n = Math.floor(Math.random() * 900_000) + 100_000;
  return `${prefix}-${n}`;
}

function validRecord(id: string, now = new Date()): SourceRecord {
  return {
    id,
    status: "Approved",
    requester: "E1002",
    department: "Fleet Maintenance",
    costCenter: "CC-02",
    companyCode: "3000",
    currency: "QAR",
    neededBy: isoDate(30, now),
    justification: "Replacement route control displays for depot buses",
    approvedAt: new Date(now.getTime() - 3_600_000).toISOString(),
    totalAmount: 1570,
    lines: [
      { lineNo: 10, materialCode: "TEST-002-01", description: "Route control displays item 1", materialGroup: null, quantity: 2, uom: "EA", unitPrice: 387, lineTotal: 774, plant: "3100", taxCode: "Z0" },
      { lineNo: 20, materialCode: "TEST-001-01", description: "Workshop diagnostic tools item 1", materialGroup: null, quantity: 1, uom: "EA", unitPrice: 796, lineTotal: 796, plant: "3100", taxCode: "Z0" },
    ],
  };
}

export function buildTemplates(id = newLabId(), now = new Date()): Template[] {
  const base = () => validRecord(id, now);
  const withLine = (patch: Partial<SourceLine>, total?: number): SourceRecord => {
    const r = base();
    const lines = r.lines as SourceLine[];
    lines[0] = { ...lines[0], ...patch };
    const q = Number(lines[0].quantity);
    const p = Number(lines[0].unitPrice);
    lines[0].lineTotal = Math.round(q * p * 100) / 100;
    r.totalAmount = total ?? Math.round(lines.reduce((s, l) => s + Number(l.quantity) * Number(l.unitPrice), 0) * 100) / 100;
    return r;
  };
  return [
    { id: "valid", name: "Valid approved requisition", description: "Passes every rule; should be created in SAP once.", record: base() },
    { id: "missing-requester", name: "Missing requester", description: "requester is null. Expected: MissingRequiredField.", record: { ...base(), requester: null } },
    { id: "zero-total", name: "Zero total", description: "totalAmount 0. Expected: AmountNotPositive.", record: { ...base(), totalAmount: 0 } },
    { id: "past-date", name: "Past delivery date", description: "neededBy in the past. Expected: NeededByInPast.", record: { ...base(), neededBy: isoDate(-10, now) } },
    { id: "unknown-plant", name: "Unknown plant", description: "Plant 9999 is not in the reference data. Expected: UnknownPlant.", record: withLine({ plant: "9999" }) },
    { id: "negative-qty", name: "Negative quantity", description: "Quantity -2. Expected: InvalidQuantity.", record: withLine({ quantity: -2 }) },
    { id: "decimal-ea", name: "Decimal EA quantity", description: "2.5 EA is not a whole number. Expected: InvalidQuantity.", record: withLine({ quantity: 2.5 }) },
    {
      id: "duplicate-json",
      name: "Duplicate JSON keys",
      description: "Raw JSON with totalAmount twice (1570 and -1), sent verbatim. The lab must refuse it (400 Duplicate JSON property) and store nothing.",
      raw: JSON.stringify(base(), null, 2).replace('"totalAmount": 1570,', '"totalAmount": 1570,\n  "totalAmount": -1,'),
    },
    {
      id: "xss",
      name: "XSS text",
      description: "Script and markup in free text. Must be displayed literally everywhere.",
      record: {
        ...base(),
        justification: `<script>alert("xss")</script><img src=x onerror=alert(1)>`,
        lines: (base().lines as SourceLine[]).map((l, i) => (i === 0 ? { ...l, description: `"><svg onload=alert(document.domain)> =HYPERLINK("http://evil.test")` } : l)),
      },
    },
    {
      id: "unicode",
      name: "Unicode text",
      description: "Arabic, CJK, emoji and combining marks; must round-trip unchanged.",
      record: {
        ...base(),
        justification: "قطع غيار للحافلات / 公交车零件 / Ersatzteile für Busse \u{1F68C} / Ñandú café",
        lines: (base().lines as SourceLine[]).map((l, i) => (i === 0 ? { ...l, description: "شاشات التحكم بالمسار / 路线控制显示器 \u{1F68F}" } : l)),
      },
    },
  ];
}
