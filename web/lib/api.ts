import type {
  AssistantCallRequest,
  AssistantCallResponse,
  AssistantUser,
  BatchAccepted,
  BatchesResponse,
  BatchResult,
  Catalogue,
  Dashboard,
  Dataset,
  FaultSettings,
  GeneratorApplyResult,
  GeneratorOptions,
  GeneratorPreview,
  Health,
  HttpExchange,
  ImportResult,
  JsonObject,
  ProblemDetails,
  QuarantineItem,
  ReconciliationView,
  RequisitionDetail,
  RequisitionRow,
  RunScenariosRequest,
  SapDocument,
  ScenarioResult,
  LatestResult,
  ScriptedFault,
  SourceRecord,
  ValidationResult,
  HandsOnChecks,
  NetworkState,
  WhatIfSituation,
  WhatIfStory,
  WorkspaceInfo,
  WorkspaceRun,
} from "./types";
import { normalizeFaultState } from "./faults";

const LAB_PROXY_PREFIX = "/api/lab";

type ApiErrorKind = "network" | "timeout" | "unauthorized" | "conflict" | "not-found" | "http" | "schema";

export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  readonly status: number;
  readonly problem?: ProblemDetails;
  readonly path: string;
  readonly method: string;
  readonly bodyText?: string;

  constructor(opts: {
    kind: ApiErrorKind;
    status: number;
    message: string;
    path: string;
    method: string;
    problem?: ProblemDetails;
    bodyText?: string;
  }) {
    super(opts.message);
    this.name = "ApiError";
    this.kind = opts.kind;
    this.status = opts.status;
    this.problem = opts.problem;
    this.path = opts.path;
    this.method = opts.method;
    this.bodyText = opts.bodyText;
  }

  // The request may have reached the lab, so the outcome is unknown rather than failed.
  get outcomeUnknown(): boolean {
    return this.kind === "network" || this.kind === "timeout" || this.status === 502 || this.status === 504;
  }
}

export function isApiError(e: unknown): e is ApiError {
  return e instanceof ApiError;
}

export function labUrl(path: string): string {
  const clean = path.startsWith("/") ? path : `/${path}`;
  return `${LAB_PROXY_PREFIX}${clean}`;
}

function kindForStatus(status: number): ApiErrorKind {
  if (status === 401 || status === 403) return "unauthorized";
  if (status === 404) return "not-found";
  if (status === 409) return "conflict";
  return "http";
}

function messageFor(status: number, problem: ProblemDetails | undefined, bodyText: string): string {
  if (status === 401) return problem?.title ?? "The Test Lab API rejected the lab key";
  if (problem?.title) return problem.detail ? `${problem.title}: ${problem.detail}` : problem.title;
  if (bodyText) return `HTTP ${status}: ${bodyText.slice(0, 300)}`;
  return `HTTP ${status}`;
}

interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "DELETE";
  body?: unknown;
  // Sent verbatim, so raw JSON with duplicate keys survives.
  rawBody?: string;
  formData?: FormData;
  timeoutMs?: number;
  signal?: AbortSignal;
  raw?: boolean;
}

const DEFAULT_TIMEOUT_MS = 30_000;

export async function request<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  const method = opts.method ?? (opts.body !== undefined || opts.rawBody !== undefined || opts.formData ? "POST" : "GET");
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(new DOMException("timeout", "TimeoutError")), opts.timeoutMs ?? DEFAULT_TIMEOUT_MS);
  const onAbort = () => controller.abort(opts.signal?.reason);
  opts.signal?.addEventListener("abort", onAbort);

  const headers: Record<string, string> = { Accept: "application/json" };
  let body: BodyInit | undefined;
  if (opts.formData) body = opts.formData;
  else if (opts.rawBody !== undefined) {
    body = opts.rawBody;
    headers["Content-Type"] = "application/json";
  } else if (opts.body !== undefined) {
    body = JSON.stringify(opts.body);
    headers["Content-Type"] = "application/json";
  }

  let response: Response;
  try {
    response = await fetch(labUrl(path), { method, headers, body, signal: controller.signal, cache: "no-store" });
  } catch (e) {
    const timedOut = controller.signal.reason instanceof DOMException && controller.signal.reason.name === "TimeoutError";
    if (opts.signal?.aborted) throw e;
    throw new ApiError({
      kind: timedOut ? "timeout" : "network",
      status: 0,
      path,
      method,
      message: timedOut
        ? `No answer from the lab within ${Math.round((opts.timeoutMs ?? DEFAULT_TIMEOUT_MS) / 1000)}s`
        : "The web server could not be reached (network error)",
    });
  } finally {
    clearTimeout(timeout);
    opts.signal?.removeEventListener("abort", onAbort);
  }

  if (opts.raw) {
    if (!response.ok) throw await toError(response, path, method);
    return response as unknown as T;
  }
  if (!response.ok) throw await toError(response, path, method);
  if (response.status === 204) return undefined as T;
  const text = await response.text();
  if (!text) return undefined as T;
  const type = response.headers.get("content-type") ?? "";
  if (type.includes("json")) {
    try {
      return JSON.parse(text) as T;
    } catch {
      throw new ApiError({ kind: "schema", status: response.status, path, method, message: "The lab sent invalid JSON", bodyText: text.slice(0, 2000) });
    }
  }
  return text as unknown as T;
}

async function toError(response: Response, path: string, method: string): Promise<ApiError> {
  const text = await response.text().catch(() => "");
  let problem: ProblemDetails | undefined;
  try {
    const parsed = text ? JSON.parse(text) : undefined;
    if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) problem = parsed as ProblemDetails;
  } catch {
    problem = undefined;
  }
  return new ApiError({
    kind: kindForStatus(response.status),
    status: response.status,
    path,
    method,
    problem,
    bodyText: text.slice(0, 4000),
    message: messageFor(response.status, problem, text),
  });
}

function expectShape<T>(value: unknown, path: string, check: (v: Record<string, unknown>) => boolean, what: string): T {
  if (value === null || typeof value !== "object" || !check(value as Record<string, unknown>)) {
    throw new ApiError({
      kind: "schema",
      status: 200,
      path,
      method: "GET",
      message: `Unexpected response from the lab: ${what}`,
      bodyText: JSON.stringify(value)?.slice(0, 2000),
    });
  }
  return value as T;
}

function expectArray<T>(value: unknown, path: string, what: string): T[] {
  if (!Array.isArray(value)) {
    throw new ApiError({
      kind: "schema",
      status: 200,
      path,
      method: "GET",
      message: `Unexpected response from the lab: ${what} should be a list`,
      bodyText: JSON.stringify(value)?.slice(0, 2000),
    });
  }
  return value as T[];
}

const isNum = (v: unknown) => typeof v === "number" && Number.isFinite(v);

export const api = {
  health: () => request<Health>("/health", { timeoutMs: 8000 }),

  workspace: async () => {
    const w = await request<WorkspaceInfo>("/workspace");
    const info = expectShape<WorkspaceInfo>(w, "/workspace", (v) => typeof v.dataset === "string" && typeof v.faults === "object", "workspace");
    return {
      ...info,
      faults: { sap: normalizeFaultState(info.faults?.sap), source: normalizeFaultState(info.faults?.source) },
    };
  },
  resetWorkspace: (body: { dataset: Dataset; seed?: number; count?: number }) =>
    request<void>("/workspace/reset", { method: "POST", body, timeoutMs: 60_000 }),
  dashboard: async () => {
    const d = await request<Dashboard>("/workspace/dashboard");
    return expectShape<Dashboard>(
      d,
      "/workspace/dashboard",
      (v) => {
        const t = v.totals as Record<string, unknown> | undefined;
        const h = v.health as Record<string, unknown> | undefined;
        return !!t && !!h && isNum(t.total) && isNum(t.created) && isNum(t.quarantined) && Array.isArray(v.outcomes) && typeof h.status === "string";
      },
      "dashboard totals/health missing",
    );
  },
  requisitions: async () =>
    expectArray<RequisitionRow>(await request<unknown>("/workspace/requisitions"), "/workspace/requisitions", "requisitions"),
  requisition: (id: string) => request<RequisitionDetail>(`/workspace/requisitions/${encodeURIComponent(id)}`),
  putRequisition: (id: string, record: SourceRecord | string, version?: number) =>
    request<JsonObject>(
      `/workspace/requisitions/${encodeURIComponent(id)}${version !== undefined ? `?version=${encodeURIComponent(String(version))}` : ""}`,
      typeof record === "string" ? { method: "PUT", rawBody: record } : { method: "PUT", body: record },
    ),
  editRequisition: (id: string, patch: JsonObject) =>
    request<SourceRecord>(`/workspace/requisitions/${encodeURIComponent(id)}/edit`, { method: "POST", body: patch }),
  validate: (record: SourceRecord | string) =>
    request<ValidationResult>("/workspace/validate", typeof record === "string" ? { method: "POST", rawBody: record } : { method: "POST", body: record }),

  runs: async () => expectArray<WorkspaceRun>(await request<unknown>("/workspace/runs"), "/workspace/runs", "runs"),
  sync: () => request<WorkspaceRun>("/workspace/sync", { method: "POST", body: {} }),
  replay: (ids: string[]) => request<WorkspaceRun>("/workspace/replay", { method: "POST", body: { ids } }),
  stop: () => request<{ stopped: boolean }>("/workspace/stop", { method: "POST", body: {} }),
  schedule: (enabled: boolean, seconds?: number) =>
    request<{ enabled: boolean; seconds: number }>("/workspace/schedule", { method: "POST", body: { enabled, seconds } }),
  restart: () => request<void>("/workspace/restart", { method: "POST", body: {}, timeoutMs: 60_000 }),
  crash: (point: "after-sap-create" | "mid-page") => request<{ armed: string }>("/workspace/crash", { method: "POST", body: { point } }),
  reconcile: () => request<JsonObject>("/workspace/reconcile", { method: "POST", body: {} }),

  putFaults: (target: "sap" | "source", settings: FaultSettings) =>
    request<unknown>(`/workspace/faults/${target}`, { method: "PUT", body: settings }),
  scriptFaults: (target: "sap" | "source", script: ScriptedFault[]) =>
    request<unknown>(`/workspace/faults/${target}/script`, { method: "POST", body: script }),
  clearFaults: () => request<void>("/workspace/faults", { method: "DELETE" }),
  dbFault: (body: { kind: "SaveFails" | "DatabaseLocked" | "Crash"; match: "mapping" | "checkpoint" | "sending"; times?: number }) =>
    request<{ armed: string }>("/workspace/faults/db", { method: "POST", body }),

  sapDocuments: async () => expectArray<SapDocument>(await request<unknown>("/workspace/sap/documents"), "/workspace/sap/documents", "SAP documents"),
  insertSapDocument: (body: { reference: string; version?: number; number?: string; lifecycle?: string }) =>
    request<{ number: string }>("/workspace/sap/documents", { method: "POST", body }),
  advanceSap: () => request<void>("/workspace/sap/advance", { method: "POST", body: {} }),
  evictSapKeys: () => request<void>("/workspace/sap/evict-keys", { method: "POST", body: {} }),

  quarantine: async () => expectArray<QuarantineItem>(await request<unknown>("/workspace/quarantine"), "/workspace/quarantine", "quarantine"),
  resolveQuarantine: (id: string, note: string) =>
    request<void>(`/workspace/quarantine/${encodeURIComponent(id)}/resolve`, { method: "POST", body: { note } }),

  reconciliation: () => request<ReconciliationView>("/workspace/reconciliation"),
  report: (format: "json" | "md" | "html" | "csv") => request<Response>(`/workspace/reports/${format}`, { raw: true }),

  http: (take = 300) => request<HttpExchange[]>(`/workspace/http?take=${take}`),

  assistantUsers: () => request<AssistantUser[]>("/workspace/assistant/users"),
  assistantCall: (call: AssistantCallRequest) => request<AssistantCallResponse>("/workspace/assistant/call", { method: "POST", body: call }),

  catalogue: async () =>
    expectShape<Catalogue>(await request<unknown>("/catalogue"), "/catalogue", (v) => Array.isArray(v.scenarios) && Array.isArray(v.categories), "catalogue"),
  batches: () => request<BatchesResponse>("/batches"),
  // A full batch can be tens of MB, so pages load summaries and fetch single results on demand.
  batchSummary: (id: string) => request<BatchResult>(`/batches/${encodeURIComponent(id)}?summary=true`),
  scenariosLatest: async () => {
    const v = await request<unknown>("/scenarios/latest");
    return expectShape<Record<string, LatestResult>>(v ?? {}, "/scenarios/latest", (x) => !Array.isArray(x), "latest results");
  },
  batchResult: (id: string, scenario: string) =>
    request<ScenarioResult>(`/batches/${encodeURIComponent(id)}/results/${encodeURIComponent(scenario)}`),
  runBatch: (body: RunScenariosRequest) => request<BatchAccepted>("/batches", { method: "POST", body }),
  cancelBatch: (id: string) => request<void>(`/batches/live/${encodeURIComponent(id)}`, { method: "DELETE" }),
  batchExport: (id: string, format: "json" | "csv" | "md" | "html") =>
    request<Response>(`/batches/${encodeURIComponent(id)}/export?format=${format}`, { raw: true }),

  generatorPreview: (o: GeneratorOptions) => request<GeneratorPreview>("/generator/preview", { method: "POST", body: o, timeoutMs: 120_000 }),
  generatorApply: (o: GeneratorOptions) => request<GeneratorApplyResult>("/generator/apply", { method: "POST", body: o, timeoutMs: 300_000 }),
  checks: () => request<HandsOnChecks>("/workspace/checks"),
  setNetwork: (state: NetworkState) => request<NetworkState>("/workspace/network", { method: "POST", body: state }),
  pause: () => request<{ paused: boolean }>("/workspace/pause", { method: "POST" }),
  resume: () => request<{ paused: boolean }>("/workspace/resume", { method: "POST" }),
  duplicate: (id: string, how: "resend" | "change" | "in-sap") => request<{ message: string }>("/workspace/duplicate", { method: "POST", body: { id, how } }),
  whatIfSituations: async () => expectArray<WhatIfSituation>(await request<unknown>("/whatif"), "/whatif", "situations"),
  runWhatIf: (id: string) => request<WhatIfStory>(`/whatif/${encodeURIComponent(id)}`, { method: "POST", timeoutMs: 180_000 }),
  importFile: (file: File, apply: boolean) => {
    const form = new FormData();
    form.append("file", file);
    return request<ImportResult>(`/workspace/import?apply=${apply}`, { method: "POST", formData: form, timeoutMs: 300_000 });
  },
};

export const qk = {
  health: ["health"] as const,
  workspace: ["workspace"] as const,
  dashboard: ["workspace", "dashboard"] as const,
  requisitions: ["workspace", "requisitions"] as const,
  requisition: (id: string) => ["workspace", "requisition", id] as const,
  runs: ["workspace", "runs"] as const,
  quarantine: ["workspace", "quarantine"] as const,
  reconciliation: ["workspace", "reconciliation"] as const,
  sapDocuments: ["workspace", "sap-documents"] as const,
  http: (take: number) => ["workspace", "http", take] as const,
  assistantUsers: ["assistant-users"] as const,
  catalogue: ["catalogue"] as const,
  batches: ["batches"] as const,
  batchSummary: (id: string) => ["batch-summary", id] as const,
  batchResult: (id: string, scenario: string) => ["batch-result", id, scenario] as const,
  scenariosLatest: ["scenarios-latest"] as const,
  whatIf: ["whatif"] as const,
  checks: ["workspace", "checks"] as const,
};

export const WORKSPACE_KEY = ["workspace"] as const;

export async function saveResponse(response: Response, fallbackName: string): Promise<void> {
  const blob = await response.blob();
  const disposition = response.headers.get("content-disposition") ?? "";
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
  const name = match?.[1] ? decodeURIComponent(match[1]) : fallbackName;
  const { downloadBlob } = await import("./download");
  downloadBlob(blob, name);
}
