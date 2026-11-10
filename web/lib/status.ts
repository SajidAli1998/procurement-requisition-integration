export type Tone = "success" | "info" | "accent" | "danger" | "warning" | "critical" | "muted" | "neutral";

export type OutcomeCategory =
  | "created"
  | "linked"
  | "updated"
  | "quarantined"
  | "skipped"
  | "pending"
  | "retrying"
  | "unresolved"
  | "resolved"
  | "not-processed"
  | "unknown";

interface OutcomeStyle {
  category: OutcomeCategory;
  label: string;
  tone: Tone;
  synchronised: boolean;
  description: string;
}

export function outcomeCategory(outcome: string | null | undefined): OutcomeCategory {
  const o = (outcome ?? "").trim().toLowerCase();
  if (o === "created") return "created";
  if (o === "linked" || o === "duplicate") return "linked";
  if (o === "updated" || o === "amended") return "updated";
  if (o === "quarantined") return "quarantined";
  if (o === "skipped" || o === "ignored" || o === "not approved") return "skipped";
  if (o === "retrying" || o === "retrypending") return "retrying";
  if (o.startsWith("unresolved") || o === "indoubt" || o === "sending") return "unresolved";
  if (o === "pending" || o === "queued") return "pending";
  if (o.startsWith("resolved") || o === "dismissed") return "resolved";
  if (o === "not processed" || o === "notseen") return "not-processed";
  return "unknown";
}

const STYLES: Record<OutcomeCategory, Omit<OutcomeStyle, "label" | "category">> = {
  created: { tone: "success", synchronised: true, description: "Created in SAP by the integration" },
  linked: { tone: "info", synchronised: true, description: "Linked to an existing SAP document (not created again)" },
  updated: { tone: "accent", synchronised: true, description: "Already linked; a change was sent as an update" },
  quarantined: { tone: "danger", synchronised: false, description: "Held back: not sent to SAP until corrected" },
  skipped: { tone: "muted", synchronised: false, description: "Not approved, so not sent to SAP" },
  pending: { tone: "warning", synchronised: false, description: "Queued: not yet confirmed in SAP" },
  retrying: { tone: "warning", synchronised: false, description: "A retry is scheduled. Not yet confirmed in SAP" },
  unresolved: { tone: "critical", synchronised: false, description: "Outcome unknown: SAP is re-checked on the next run" },
  resolved: { tone: "neutral", synchronised: false, description: "Closed by an operator. Not sent to SAP" },
  "not-processed": { tone: "neutral", synchronised: false, description: "Not processed by any run yet" },
  unknown: { tone: "neutral", synchronised: false, description: "Unrecognised state" },
};

export function outcomeStyle(outcome: string | null | undefined): OutcomeStyle {
  const category = outcomeCategory(outcome);
  return { category, label: outcome && outcome.trim() ? outcome : "Unknown", ...STYLES[category] };
}

export function stateCategory(state: string | null | undefined): OutcomeCategory {
  switch (state) {
    case "Synced":
      return "created";
    case "Quarantined":
      return "quarantined";
    case "Ignored":
      return "skipped";
    case "RetryPending":
      return "retrying";
    case "InDoubt":
    case "Sending":
      return "unresolved";
    case "Queued":
      return "pending";
    case "Dismissed":
      return "resolved";
    default:
      return "not-processed";
  }
}

export function isSynchronised(outcome: string | null | undefined): boolean {
  return outcomeStyle(outcome).synchronised;
}

type ScenarioStatusLabel = "Pass" | "Fail" | "Blocked" | "Not implemented" | "Not run";

export function scenarioStatus(status: string | null | undefined): { label: ScenarioStatusLabel; tone: Tone } {
  switch (status) {
    case "Pass":
      return { label: "Pass", tone: "success" };
    case "Fail":
      return { label: "Fail", tone: "danger" };
    case "Blocked":
      return { label: "Blocked", tone: "warning" };
    case "NotImplemented":
      return { label: "Not implemented", tone: "muted" };
    default:
      return { label: "Not run", tone: "neutral" };
  }
}

export function invariantTone(outcome: string | null | undefined): Tone {
  if (outcome === "Pass") return "success";
  if (outcome === "Fail") return "danger";
  return "muted";
}

export function httpStatusTone(status: number | null | undefined): Tone {
  if (status === null || status === undefined || status === 0) return "critical";
  if (status >= 200 && status < 300) return "success";
  if (status === 409 || status === 428 || status === 429) return "warning";
  if (status >= 400 && status < 500) return "danger";
  return "danger";
}

export function severityTone(severity: string): Tone {
  if (severity === "Critical") return "critical";
  if (severity === "High") return "danger";
  return "warning";
}
