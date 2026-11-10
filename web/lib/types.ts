// Scenario and batch endpoints omit nulls, so their fields are optional here.

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };
export type JsonObject = { [key: string]: JsonValue };

export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  suggestedAction?: string;
  traceId?: string;
  [key: string]: unknown;
}

export interface RunEnvironment {
  appVersion: string;
  gitCommit: string;
  runtime: string;
  os: string;
  machine: string;
}

export interface Health {
  status: string;
  isolated: boolean;
  environment: RunEnvironment;
}

export interface BaselineMetric {
  metric: string;
  expected: string;
}

export interface Baseline {
  source: string;
  metrics: BaselineMetric[];
}

export type Dataset = "Workbook" | "Generated" | "Empty";

export interface FaultSettings {
  transientErrorRate: number;
  throttleRate: number;
  throttleRetryAfterSeconds: number;
  lostResponseRate: number;
  latencyMs: number;
  pageOverlap: boolean;
  honorIdempotencyKey: boolean;
  referenceSearchLag: boolean;
  seed: number;
}

export const FAULT_KINDS = [
  "Error",
  "Throttle",
  "LostResponse",
  "Delay",
  "Abort",
  "Respond",
  "CommitThenRespond",
  "CommitThenTransform",
] as const;
export type FaultKind = (typeof FAULT_KINDS)[number];

export type BodyTransform = "Truncate" | "SetField";

export interface ScriptedFault {
  kind: FaultKind;
  method?: string | null;
  pathContains?: string | null;
  bodyContains?: string | null;
  count?: number;
  skip?: number;
  statusCode?: number;
  retryAfterSeconds?: number;
  retryAfterAsDate?: boolean;
  omitRetryAfter?: boolean;
  delayMs?: number;
  body?: string | null;
  transform?: BodyTransform;
  field?: string | null;
  value?: string | null;
}

export interface FaultState {
  settings: FaultSettings;
  pendingScript: ScriptedFault[];
  injected: Record<string, number>;
}

export interface WorkspaceRun {
  id: string;
  kind: string;
  startedAt: string;
  finishedAt: string | null;
  state: "Running" | "Finished" | string;
  outcome: string | null;
  message: string | null;
  durationMs: number | null;
  events: Record<string, number> | null;
  transport: Record<string, number> | null;
  pending: number | null;
  openQuarantine: number | null;
  retryWaitSeconds: number | null;
  steps: string[];
}

export interface WorkspaceInfo {
  dataset: Dataset | string;
  seed?: number | null;
  count?: number | null;
  createdAt: string;
  scheduled: boolean;
  scheduleSeconds: number;
  active: WorkspaceRun | null;
  sap: { total: number; duplicateReferences: number };
  faults: { sap: FaultState | null; source: FaultState | null };
  crashArmed: string | null;
  crashFiredAt?: string | null;
  dbFaultsArmed?: string[];
  dbFaultsFired?: string[];
}

export interface NameValue {
  name: string;
  value: number;
}

export interface InvariantRow {
  name: string;
  outcome: "Pass" | "Fail" | "NotApplicable" | string;
  detail: string;
}

export interface DashboardTotals {
  total: number;
  approved: number;
  created: number;
  linked: number;
  skipped: number;
  quarantined: number;
  pending: number;
  retrying: number;
  unresolved: number;
  needsIntervention: number;
  synced: number;
  duplicateReferencesInSap: number;
  sapDocuments: number;
}

export interface DashboardHealth {
  status: string;
  breaker: string;
  breakerOpenedCount: number;
  averageRunMs: number;
  sapCalls: number;
  sapSuccessRate: number | null;
  retries: number;
  latencyMs: { p50: number; p95: number; p99: number };
}

export interface DashboardRun {
  id: string;
  kind: string;
  startedAt: string;
  durationMs: number | null;
  outcome: string | null;
  created: number;
  linked: number;
  quarantined: number;
  retries: number;
  throughput: number;
}

export interface Tally {
  total: number;
  executed: number;
  passed: number;
  failed: number;
  blocked: number;
  notImplemented: number;
  notRun: number;
}

export interface ScenarioHistoryPoint {
  id: string;
  name: string;
  startedAt: string;
  tally: Tally;
}

export interface Dashboard {
  dataset: string;
  totals: DashboardTotals;
  health: DashboardHealth;
  reconciliation: InvariantRow[] | null;
  outcomes: NameValue[];
  errorTypes: NameValue[];
  sapResponses: NameValue[];
  runs: DashboardRun[];
  scenarioHistory: ScenarioHistoryPoint[];
  baseline: Baseline;
}

export type Outcome =
  | "Created"
  | "Linked"
  | "Updated"
  | "Quarantined"
  | "Skipped"
  | "Resolved by operator"
  | "Retrying"
  | "Unresolved (verifying in SAP)"
  | "Unresolved (mid-send)"
  | "Pending"
  | "Not processed";

export interface RequisitionRow {
  id: string;
  sourceSystem: string;
  requester: JsonValue;
  department: JsonValue;
  approvalStatus: JsonValue;
  sourceVersion: JsonValue;
  itemCount: number;
  materialCode: JsonValue;
  description: JsonValue;
  quantity: JsonValue;
  unitOfMeasure: JsonValue;
  unitPrice: JsonValue;
  currency: JsonValue;
  totalAmount: JsonValue;
  costCenter: JsonValue;
  companyCode: JsonValue;
  plant: JsonValue;
  purchasingGroup: JsonValue;
  requiredDelivery: JsonValue;
  sapNumber: string | null;
  sapStatus: string | null;
  state: string;
  outcome: Outcome | string;
  lastAttempt: string | null;
  retryCount: number;
  validation: "Valid" | "Invalid" | null;
  quarantineReason: string | null;
  quarantineSummary: string | null;
  reconciliation: string;
  lastError: string | null;
}

export interface SourceLine {
  lineNo?: JsonValue;
  materialCode?: JsonValue;
  description?: JsonValue;
  materialGroup?: JsonValue;
  quantity?: JsonValue;
  uom?: JsonValue;
  unitPrice?: JsonValue;
  lineTotal?: JsonValue;
  plant?: JsonValue;
  taxCode?: JsonValue;
  [key: string]: unknown;
}

export interface SourceRecord {
  id?: JsonValue;
  status?: JsonValue;
  requester?: JsonValue;
  department?: JsonValue;
  costCenter?: JsonValue;
  companyCode?: JsonValue;
  currency?: JsonValue;
  neededBy?: JsonValue;
  justification?: JsonValue;
  approvedAt?: JsonValue;
  totalAmount?: JsonValue;
  lines?: SourceLine[] | JsonValue;
  erpSync?: JsonValue;
  version?: JsonValue;
  updatedAt?: JsonValue;
  [key: string]: unknown;
}

export interface ValidationProblem {
  reason: string;
  field: string;
  message: string;
  value: JsonValue;
}

export interface ValidationResult {
  approved: boolean;
  status: string | null;
  valid: boolean;
  eligible: boolean;
  problems: ValidationProblem[];
  sapPayload: JsonObject | null;
}

export interface DuplicateDecision {
  layer: string;
  detail: string;
  created: number;
}

export interface TimelineEvent {
  at: string;
  run: string;
  kind: string;
  detail: string | null;
  sapResponses: string | null;
}

export interface QuarantineEntryRecord {
  id: number;
  sourceKey: string;
  sourceVersion: number;
  stage: string;
  reason: string;
  summary: string;
  details?: string | null;
  rawPayload?: string | null;
  occurrences: number;
  firstSeenAt: string;
  lastSeenAt: string;
  resolvedAt: string | null;
  resolution: string | null;
  [key: string]: unknown;
}

export interface StatusHistoryEntry {
  id: number;
  sourceId: string;
  sapNumber: string | null;
  fromStatus: string | null;
  toStatus: string | null;
  changedAt: string;
  recordedAt: string;
  runId: string | null;
}

export interface HttpExchange {
  seq: number;
  at: string;
  channel: string;
  host: string;
  method: string;
  url: string;
  status?: number | null;
  outcome?: string | null;
  durationMs: number;
  correlationId?: string | null;
  idempotencyKey?: string | null;
  requestBody?: string | null;
  responseBody?: string | null;
}

export interface IntegrationRecord {
  sourceId: string;
  sourceVersion: number;
  sourceStatus: string;
  contentHash: string;
  canonicalJson: string | null;
  state: string;
  sapNumber: string | null;
  sapETag: string | null;
  syncedSourceVersion: number | null;
  erpStatus: string | null;
  erpStatusAt: string | null;
  purchaseOrder: string | null;
  writebackPending: boolean;
  attempts: number;
  nextAttemptAt: string | null;
  lastError: string | null;
  firstSeenAt: string;
  updatedAt: string;
  revision: number;
}

export interface RequisitionDetail {
  id: string;
  source: SourceRecord | null;
  canonical: JsonObject | null;
  sapPayload: JsonObject | null;
  sapDocuments: JsonObject[];
  record: IntegrationRecord | null;
  validation: ValidationResult | null;
  duplicateDecision: DuplicateDecision;
  timeline: TimelineEvent[];
  quarantine: QuarantineEntryRecord[];
  statusHistory: StatusHistoryEntry[];
  http: HttpExchange[];
}

export interface QuarantineProblem {
  reason: string | null;
  field: string;
  message: string | null;
  currentValue: JsonValue;
}

export interface QuarantineItem {
  id: number;
  sourceKey: string;
  sourceVersion: number;
  stage: string;
  reason: string;
  summary: string;
  problems: QuarantineProblem[];
  severity: "Critical" | "High" | "Medium" | string;
  correctable: boolean;
  correctableIn: string;
  attempts: number;
  firstSeenAt: string;
  lastSeenAt: string;
  resolvedAt: string | null;
  resolution: string | null;
  suggestedAction: string;
  rawPayload: JsonValue;
}

export interface CurrencyTotal {
  currency: string;
  localCount: number;
  localAmount: number;
  sapCount: number;
  sapAmount: number;
  matches: boolean;
}

export interface OpenFailure {
  key: string;
  version: number;
  stage: string;
  reason: string;
  summary: string;
  suggestedAction: string;
  firstSeen: string;
  lastSeen: string;
  occurrences: number;
}

export interface RecordOutcome {
  key: string;
  sourceVersion: number;
  result: string;
  sapNumber: string | null;
  attempts: number;
  failureCategory: string | null;
  explanation: string | null;
  sapResponses: string | null;
}

export interface ComparisonSide {
  result: string | null;
  document: string | null;
  attempts: number | null;
  category: string | null;
  version: number | null;
  explanation: string | null;
  sapResponses?: string | null;
}

export interface ComparisonRow {
  id: string;
  expected: ComparisonSide;
  actual: ComparisonSide | null;
  matches: boolean;
  differences: string[];
}

export interface ReconciliationView {
  generatedAt: string;
  stateTotals: Record<string, number>;
  invariants: InvariantRow[];
  totals: CurrencyTotal[];
  openFailures: OpenFailure[];
  outcomes: RecordOutcome[];
  comparison: { matched: number; total: number; rows: ComparisonRow[] } | null;
}

export type SapDocument = JsonObject;

export interface AssistantUser {
  id: string;
  name: string;
  department: string;
  canRaise: boolean;
  procurementDelegate: boolean;
}

export interface AssistantCallRequest {
  user: string;
  scopes?: string[] | null;
  method: "GET" | "POST" | "PUT" | "DELETE" | "PATCH";
  path: string;
  headers: Record<string, string>;
  body?: string | null;
  anonymous: boolean;
}

export interface AssistantCallResponse {
  status: number;
  durationMs: number;
  correlationId: string | null;
  headers: Record<string, string>;
  body: string;
}

export interface LiveEvent<T = unknown> {
  type: string;
  at: string;
  data?: T;
}

export type Priority = "Critical" | "High" | "Medium" | "Low";
export type ScenarioStatus = "NotRun" | "Pass" | "Fail" | "Blocked" | "NotImplemented";
export const MODES = [
  "Baseline",
  "Functional",
  "Negative",
  "DuplicateStress",
  "FailureSimulation",
  "Recovery",
  "Security",
  "Performance",
] as const;
export type Mode = (typeof MODES)[number];

export interface Expectation {
  summary: string;
  http?: string;
  source?: string;
  sap?: string;
  database?: string;
  reconciliation?: string;
}

export interface ScenarioDefinition {
  id: string;
  category: string;
  title: string;
  description: string;
  priority: Priority;
  severity: string;
  modes: Mode[];
  preconditions: string;
  fixture: string;
  faults: string;
  steps: string[];
  expected: Expectation;
  invariants: string[];
  blockedReason?: string;
  coveredBy?: string;
  implemented: boolean;
  timeoutSeconds?: number;
}

export interface Catalogue {
  categories: string[];
  modes: Mode[];
  invariants: { id: string; name: string }[];
  scenarios: ScenarioDefinition[];
}

export interface Assertion {
  name: string;
  passed: boolean;
  expected?: string;
  actual?: string;
  invariant?: string;
}

export interface LogEntry {
  at: string;
  kind: "Step" | "Info" | "Run" | "Warn" | "Error";
  text: string;
  simulated?: boolean;
}

export interface InvariantResult {
  id: string;
  name: string;
  outcome: "Pass" | "Fail" | "NotApplicable";
  detail: string;
}

export interface RunSummary {
  label: string;
  worker: string;
  kind: string;
  status?: string;
  message?: string;
  durationMs: number;
  events?: Record<string, number>;
  transport?: Record<string, number>;
  openQuarantine?: number;
  pending?: number;
}

export interface StateSnapshot {
  source?: JsonValue;
  sap?: JsonValue;
  database?: JsonValue;
}

export interface ScenarioResult {
  id: string;
  title: string;
  category: string;
  priority: Priority;
  status: ScenarioStatus;
  startedAt: string;
  durationMs: number;
  expected: string;
  actual: string;
  error?: string;
  blockedReason?: string;
  log: LogEntry[];
  assertions: Assertion[];
  invariants: InvariantResult[];
  runs: RunSummary[];
  http: HttpExchange[];
  evidence: Record<string, JsonValue>;
  before?: StateSnapshot;
  after?: StateSnapshot;
  seed: number;
}

export interface BatchResult {
  id: string;
  name: string;
  startedAt: string;
  finishedAt?: string;
  tally: Tally;
  byCategory: Record<string, Tally>;
  byMode: Record<string, Tally>;
  environment: RunEnvironment;
  results: ScenarioResult[];
}

export interface BatchSummary {
  id: string;
  name: string;
  startedAt: string;
  finishedAt?: string;
  tally: Tally;
}

export interface BatchesResponse {
  active: { id: string; name: string; total: number; tally: Tally }[];
  history: BatchSummary[];
}

export interface RunScenariosRequest {
  ids?: string[];
  category?: string;
  mode?: Mode;
  priority?: Priority;
  search?: string;
  all?: boolean;
  name?: string;
  parallelism?: number;
  seed?: number;
}

export interface LatestResult {
  batchId: string;
  at: string;
  status: ScenarioStatus;
  actual?: string;
  durationMs?: number;
}

export interface BatchAccepted {
  id: string;
  name: string;
  total: number;
}

export interface RunnerEvent {
  batchId?: string;
  type: string;
  scenarioId?: string;
  text?: string;
  status?: ScenarioStatus;
  tally?: Tally;
  log?: LogEntry;
  state?: string;
}

export interface GeneratorOptions {
  count: number;
  seed: number;
  approvedPct?: number;
  pendingPct: number;
  invalidPct: number;
  duplicatePct: number;
  existingInSapPct: number;
  missingFieldsPct: number;
  invalidCodesPct: number;
  approvalConflictPct: number;
  priceInconsistencyPct: number;
  sapFailurePct: number;
}

export interface GeneratorManifestEntry {
  id: string;
  traits: string[];
  expected: string;
}

export interface GeneratorPreview {
  records: SourceRecord[];
  manifest: GeneratorManifestEntry[];
  sapPreloads?: number;
  sapFaults?: number;
  summary: Record<string, number>;
  csv?: string;
}

export interface GeneratorApplyResult {
  loaded: number;
  sapPreloaded: number;
  faultsScripted: number;
  summary?: Record<string, number>;
}

export interface ImportIssue {
  sheet: string | null;
  row: number | null;
  column: string | null;
  reason: string;
}

export interface ImportResult {
  fileName: string;
  format: string;
  sheets: string[];
  requisitions: number;
  lines: number;
  rowsRead?: number;
  rowsSkipped?: number;
  errors: ImportIssue[];
  warnings: ImportIssue[];
  ok: boolean;
  applied: boolean;
  records?: SourceRecord[];
}

export interface WhatIfSituation {
  id: string;
  title: string;
  description: string;
  group: string;
}

export interface WhatIfNumber {
  label: string;
  before: number | null;
  after: number;
  tone: "success" | "warning" | "danger" | "neutral";
}

export interface WhatIfStory {
  id: string;
  title: string;
  verdict: string;
  headline: string;
  story: string[];
  numbers: WhatIfNumber[];
  checks: { label: string; ok: boolean; detail: string }[];
  timeline: { at: string; text: string }[];
  next: { id: string; label: string } | null;
  links: { label: string; href: string }[];
  at: string;
}

export type Connection = "up" | "down" | "unreliable";

export interface NetworkState {
  sap: Connection;
  source: Connection;
  slowMotion: boolean;
}

export interface HandsOnChecks {
  verdict: { tone: "success" | "warning" | "danger" | "info" | "neutral"; title: string; text: string };
  running: boolean;
  paused: boolean;
  network: NetworkState;
  counts: {
    inSap: number;
    created: number;
    linked: number;
    waiting: number;
    checking: number;
    notRead: number;
    quarantined: number;
    skipped: number;
    duplicates: number;
    toGo: number;
    total: number;
  };
  checks: { label: string; ok: boolean; detail: string }[];
  requisitions: { id: string; state: string }[];
}
