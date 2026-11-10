import type { FaultSettings, FaultState, ScriptedFault } from "./types";

export const DEFAULT_FAULT_SETTINGS: FaultSettings = {
  transientErrorRate: 0,
  throttleRate: 0,
  throttleRetryAfterSeconds: 1,
  lostResponseRate: 0,
  latencyMs: 0,
  pageOverlap: false,
  honorIdempotencyKey: true,
  referenceSearchLag: false,
  seed: 1234,
};

function camel(key: string): string {
  return key.length === 0 ? key : key.charAt(0).toLowerCase() + key.slice(1);
}

// The SAP mock answers in PascalCase, the source mock in camelCase.
export function camelKeys<T>(value: unknown): T {
  if (Array.isArray(value)) return value.map((v) => camelKeys(v)) as T;
  if (value && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value)) out[camel(k)] = camelKeys(v);
    return out as T;
  }
  return value as T;
}

const KINDS = ["None", "Error", "Throttle", "LostResponse", "Delay", "Abort", "Respond", "CommitThenRespond", "CommitThenTransform"];
const TRANSFORMS = ["Truncate", "SetField"];

export function normalizeFaultState(raw: unknown): FaultState | null {
  if (!raw || typeof raw !== "object") return null;
  const s = camelKeys<{ settings?: Partial<FaultSettings>; pendingScript?: unknown[]; injected?: Record<string, number> }>(raw);
  const script = (s.pendingScript ?? []).map((f) => {
    const fault = f as ScriptedFault & { kind: unknown; transform?: unknown };
    return {
      ...fault,
      kind: (typeof fault.kind === "number" ? KINDS[fault.kind] : fault.kind) as ScriptedFault["kind"],
      transform: (typeof fault.transform === "number" ? TRANSFORMS[fault.transform] : fault.transform) as ScriptedFault["transform"],
    };
  });
  return {
    settings: { ...DEFAULT_FAULT_SETTINGS, ...(s.settings ?? {}) },
    pendingScript: script,
    injected: s.injected ?? {},
  };
}

export function faultsActive(state: FaultState | null | undefined): boolean {
  if (!state) return false;
  const st = state.settings;
  return (
    state.pendingScript.length > 0 ||
    st.transientErrorRate > 0 ||
    st.throttleRate > 0 ||
    st.lostResponseRate > 0 ||
    st.latencyMs > 0 ||
    st.pageOverlap ||
    !st.honorIdempotencyKey ||
    st.referenceSearchLag
  );
}

const SAP_HEADER = "A_PurchaseRequisitionHeader";

export interface FaultPreset {
  id: string;
  name: string;
  description: string;
  target: "sap" | "source";
  script?: ScriptedFault[];
  settings?: Partial<FaultSettings>;
}

export const FAULT_PRESETS: FaultPreset[] = [
  {
    id: "commit-then-500",
    name: "SAP commits then 500",
    description: "SAP creates the requisition, then answers 500. The integration must find the document by reference, not create a second one.",
    target: "sap",
    script: [{ kind: "CommitThenRespond", method: "POST", pathContains: SAP_HEADER, statusCode: 500, body: '{"error":{"code":"INTERNAL","message":"Internal error (injected)"}}', count: 1 }],
  },
  {
    id: "phantom-201",
    name: "Phantom 201 without number",
    description: "SAP answers 201 Created with no document number and creates nothing. Must never be recorded as synced.",
    target: "sap",
    script: [{ kind: "Respond", method: "POST", pathContains: SAP_HEADER, statusCode: 201, body: "{}", count: 1 }],
  },
  {
    id: "lost-response",
    name: "Lost response",
    description: "SAP commits the create, then the connection drops. The outcome is unknown until the lookup confirms it.",
    target: "sap",
    script: [{ kind: "LostResponse", method: "POST", pathContains: SAP_HEADER, count: 1 }],
  },
  {
    id: "429-retry-after",
    name: "429 with Retry-After",
    description: "Two throttled creates with Retry-After: 2. The integration must wait and retry, not fail.",
    target: "sap",
    script: [{ kind: "Throttle", method: "POST", pathContains: SAP_HEADER, retryAfterSeconds: 2, count: 2 }],
  },
  {
    id: "sap-down",
    name: "SAP down",
    description: "Every SAP call answers 503. Records stay pending/retrying and the breaker opens; nothing is shown as synced.",
    target: "sap",
    settings: { transientErrorRate: 1 },
  },
  {
    id: "malformed-page",
    name: "Malformed source page",
    description: "The next source page is cut in half (invalid JSON). The run must fail safely without losing the checkpoint.",
    target: "source",
    script: [{ kind: "CommitThenTransform", method: "GET", pathContains: "/api/requisitions?", transform: "Truncate", count: 1 }],
  },
  {
    id: "duplicate-409",
    name: "Duplicate 409",
    description: "SAP refuses the create with 409 (already exists). The integration must look the document up and link it.",
    target: "sap",
    script: [
      {
        kind: "Respond",
        method: "POST",
        pathContains: SAP_HEADER,
        statusCode: 409,
        body: '{"error":{"code":"DUPLICATE","message":"A requisition with this ExternalReference already exists (injected)"}}',
        count: 1,
      },
    ],
  },
];
