import type { AssistantCallRequest, AssistantCallResponse } from "@/lib/types";

export const SCOPES = ["requisitions:read", "requisitions:draft", "requisitions:read:all"] as const;

// Mirrors AssistantOptions. The lab host raises RequestsPerMinute to 1000.
export const MAX_REQUEST_AMOUNT = 25_000;
const MAX_QUANTITY_PER_LINE = 1_000;

export interface Identity {
  user: string;
  scopes: string[] | null;
  anonymous: boolean;
}

export interface CallRecord {
  id: string;
  at: string;
  label: string;
  request: AssistantCallRequest;
  response?: AssistantCallResponse;
  error?: string;
}

export interface Material {
  code: string;
  description: string;
  materialGroup?: string;
  uom: string;
  unitPrice: number;
}
interface CostCenter {
  code: string;
  name: string;
  companyCode: string;
  currency: string;
}
export interface Plant {
  code: string;
  name: string;
  companyCode: string;
}
export interface Reference {
  materials: Material[];
  costCenters: CostCenter[];
  plants: Plant[];
}

interface DraftLine {
  materialCode: string;
  quantity: number;
}
export interface Draft {
  costCenter: string;
  plant: string;
  neededBy: string;
  justification: string;
  lines: DraftLine[];
}

interface InvalidField {
  field: string;
  code: string;
  message: string;
  suggestion?: string | null;
}

interface ParsedBody {
  json: unknown;
  isJson: boolean;
  code?: string;
  title?: string;
  detail?: string;
  suggestedAction?: string;
  retryable?: boolean;
  retryAfter?: number;
  invalidFields: InvalidField[];
  valid?: boolean;
  issues: InvalidField[];
  confirmationSummary?: string;
  confirmationToken?: string;
  estimatedTotal?: number;
  currency?: string;
}

const str = (v: unknown): string | undefined => (typeof v === "string" ? v : undefined);

function asIssues(v: unknown): InvalidField[] {
  if (!Array.isArray(v)) return [];
  return v
    .filter((x): x is Record<string, unknown> => !!x && typeof x === "object")
    .map((x) => ({ field: String(x.field ?? ""), code: String(x.code ?? ""), message: String(x.message ?? ""), suggestion: str(x.suggestion) ?? null }));
}

export function parseBody(body: string | null | undefined): ParsedBody {
  if (!body) return { json: null, isJson: false, invalidFields: [], issues: [] };
  let json: unknown;
  try {
    json = JSON.parse(body);
  } catch {
    return { json: body, isJson: false, invalidFields: [], issues: [] };
  }
  const o = json && typeof json === "object" && !Array.isArray(json) ? (json as Record<string, unknown>) : {};
  return {
    json,
    isJson: true,
    code: str(o.code),
    title: str(o.title),
    detail: str(o.detail),
    suggestedAction: str(o.suggestedAction),
    retryable: typeof o.retryable === "boolean" ? o.retryable : undefined,
    retryAfter: typeof o.retryAfter === "number" ? o.retryAfter : undefined,
    invalidFields: asIssues(o.invalidFields),
    valid: typeof o.valid === "boolean" ? o.valid : undefined,
    issues: asIssues(o.issues),
    confirmationSummary: str(o.confirmationSummary),
    confirmationToken: str(o.confirmationToken),
    estimatedTotal: typeof o.estimatedTotal === "number" ? o.estimatedTotal : undefined,
    currency: str(o.currency),
  };
}

export function newIdempotencyKey(): string {
  if (typeof crypto !== "undefined" && "randomUUID" in crypto) return crypto.randomUUID();
  return `key-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

// UTC, because that is how the assistant API compares dates.
function isoDateFromToday(days: number, now = new Date()): string {
  return new Date(now.getTime() + days * 86_400_000).toISOString().slice(0, 10);
}

export function validDraft(ref: Reference, now = new Date()): Draft | null {
  const cc = ref.costCenters.find((c) => ref.plants.some((p) => p.companyCode === c.companyCode));
  if (!cc) return null;
  const plant = ref.plants.find((p) => p.companyCode === cc.companyCode)!;
  const materials = [...ref.materials].filter((m) => m.unitPrice > 0 && m.unitPrice * 1 <= MAX_REQUEST_AMOUNT).sort((a, b) => a.unitPrice - b.unitPrice);
  const material = materials[0];
  if (!material) return null;
  return {
    costCenter: cc.code,
    plant: plant.code,
    neededBy: isoDateFromToday(30, now),
    justification: "Replacement parts for the depot workshop (assistant playground)",
    lines: [{ materialCode: material.code, quantity: 1 }],
  };
}

export function overCapLine(ref: Reference): DraftLine | null {
  const m = [...ref.materials].sort((a, b) => b.unitPrice - a.unitPrice)[0];
  if (!m || m.unitPrice <= 0) return null;
  const whole = m.uom === "EA" || m.uom === "BOX";
  let q = Math.floor(MAX_REQUEST_AMOUNT / m.unitPrice) + 1;
  if (!whole) q = Math.round((MAX_REQUEST_AMOUNT / m.unitPrice + 1) * 1000) / 1000;
  if (q > MAX_QUANTITY_PER_LINE) return null;
  return { materialCode: m.code, quantity: q };
}

export type CallFn = (label: string, req: Partial<AssistantCallRequest> & Pick<AssistantCallRequest, "method" | "path">) => Promise<CallRecord>;

export function statusOf(r: CallRecord): number | null {
  return r.response?.status ?? null;
}

export function codeOf(r: CallRecord): string | undefined {
  return parseBody(r.response?.body).code;
}
