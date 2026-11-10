import type { HttpExchange, LiveEvent, WorkspaceRun } from "./types";

export const STAGES = [
  { id: "source", label: "Source", description: "Reads requisitions from the source system (first page and single-record re-reads)." },
  { id: "pagination", label: "Pagination", description: "Follows the source cursor page by page; the checkpoint is saved after each page." },
  { id: "validation", label: "Validation", description: "Parses each record and applies the business rules. Failures are quarantined, never sent." },
  { id: "approval", label: "Approval check", description: "Only Approved requisitions continue; others are skipped (Ignored)." },
  { id: "duplicate", label: "Duplicate detection", description: "Local mapping, then an SAP lookup by ExternalReference before any create." },
  { id: "sap-request", label: "SAP request", description: "Create (POST) or update calls to SAP, each with a deterministic Idempotency-Key." },
  { id: "sap-response", label: "SAP response", description: "How SAP answered: 201/200, 409, 429 with Retry-After, 5xx, or no response at all." },
  { id: "writeback", label: "Status write-back", description: "Reads SAP statuses and writes the ERP status back to the source record." },
  { id: "reconciliation", label: "Reconciliation", description: "After the run: every approved record accounted for, no duplicates, amounts match." },
] as const;

export type StageId = (typeof STAGES)[number]["id"];

// An SAP create counts as both a request and a response.
export function stagesFor(e: Pick<HttpExchange, "channel" | "method" | "url">): StageId[] {
  const channel = e.channel ?? "";
  const url = e.url ?? "";
  const method = (e.method ?? "").toUpperCase();
  if (channel.startsWith("integration") && channel.endsWith("source")) {
    if (method === "PUT" && url.includes("/erp-status")) return ["writeback"];
    if (method === "GET" && /^\/api\/requisitions(\?|$)/.test(url)) return url.includes("cursor=") ? ["pagination"] : ["source"];
    if (method === "GET") return ["source"];
    return ["writeback"];
  }
  if (channel.startsWith("integration") && channel.endsWith("sap")) {
    const decoded = safeDecode(url);
    if (method === "GET" && decoded.includes("ExternalReference")) return ["duplicate"];
    if (method === "GET") return ["writeback"];
    return ["sap-request", "sap-response"];
  }
  return [];
}

function safeDecode(s: string): string {
  try {
    return decodeURIComponent(s);
  } catch {
    return s;
  }
}

export function eventsOfLatestRun(events: LiveEvent[]): { run: WorkspaceRun | null; events: LiveEvent[]; finished: WorkspaceRun | null } {
  const start = events.findLastIndex((e) => e.type === "run-started");
  const started = events[start];
  if (!started) return { run: null, events: [], finished: null };
  const run = started.data as WorkspaceRun;
  const rest = events.slice(start + 1);
  const fin = rest.find((e) => e.type === "run-finished" && (e.data as WorkspaceRun)?.id === run.id);
  return { run, events: rest, finished: fin ? (fin.data as WorkspaceRun) : null };
}

export function groupByStage(exchanges: HttpExchange[]): Record<StageId, HttpExchange[]> {
  const out = Object.fromEntries(STAGES.map((s) => [s.id, [] as HttpExchange[]])) as Record<StageId, HttpExchange[]>;
  for (const e of exchanges) for (const s of stagesFor(e)) out[s].push(e);
  return out;
}

// The SAP lookup filters on ExternalReference eq 'ID'; creates carry "ExternalReference":"ID" in the body.
const REF_PATTERNS = [/ExternalReference eq '([^']+)'/g, /"ExternalReference"\s*:\s*"([^"]+)"/g];

export function requisitionsTouched(exchanges: HttpExchange[]): Set<string> {
  const ids = new Set<string>();
  for (const e of exchanges) {
    if (!e.channel.startsWith("integration") || !e.channel.endsWith("sap")) continue;
    const text = `${safeDecode(e.url)} ${e.requestBody ?? ""}`;
    for (const re of REF_PATTERNS) for (const [, id] of text.matchAll(re)) if (id) ids.add(id);
  }
  return ids;
}

export function isErrorExchange(e: HttpExchange): boolean {
  return e.status === null || e.status === undefined || e.status === 0 || e.status >= 400;
}
