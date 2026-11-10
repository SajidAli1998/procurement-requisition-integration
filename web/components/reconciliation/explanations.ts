import type { RecordOutcome, RequisitionRow } from "@/lib/types";
import { outcomeCategory } from "@/lib/status";

export interface Explanation {
  id: string;
  outcome: string;
  explanation: string;
  sapNumber: string | null;
  reconciliation: string;
}

// Prefer the run report's own words; derive one from state only when it has none.
export function explain(rows: RequisitionRow[], outcomes: RecordOutcome[]): Explanation[] {
  const byKey = new Map(outcomes.map((o) => [o.key, o]));
  return rows.map((r) => {
    const o = byKey.get(r.id);
    let explanation = o?.explanation ?? "";
    if (!explanation) {
      switch (outcomeCategory(r.outcome)) {
        case "quarantined":
          explanation = `Quarantined (${r.quarantineReason ?? "reason unknown"}): ${r.quarantineSummary ?? ""}`;
          break;
        case "skipped":
          explanation = `Not approved (status ${String(r.approvalStatus ?? "missing")}): skipped, not sent to SAP`;
          break;
        case "pending":
          explanation = "Pending: queued, not yet confirmed in SAP";
          break;
        case "retrying":
          explanation = `Retrying: ${r.retryCount} retr${r.retryCount === 1 ? "y" : "ies"} scheduled${r.lastError ? `; last error: ${r.lastError}` : ""}`;
          break;
        case "unresolved":
          explanation = "Unresolved: the last SAP call got no answer, so the next run looks the requisition up in SAP before creating anything";
          break;
        case "resolved":
          explanation = "Closed by an operator without synchronising";
          break;
        case "not-processed":
          explanation = "Not processed by any run yet";
          break;
        default:
          explanation = r.sapNumber ? `In SAP as ${r.sapNumber}` : r.outcome;
      }
    }
    return { id: r.id, outcome: r.outcome, explanation, sapNumber: r.sapNumber, reconciliation: r.reconciliation };
  });
}
