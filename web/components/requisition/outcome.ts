import type { RequisitionDetail } from "@/lib/types";

// Same wording as the explorer, without loading every row.
export function deriveOutcome(detail: RequisitionDetail): string {
  const r = detail.record;
  if (!r) return "Not processed";
  const kinds = detail.timeline.map((e) => e.kind);
  switch (r.state) {
    case "Synced":
      if (kinds.some((k) => k === "AlreadyInSap" || k === "InDoubtResolved") && !kinds.includes("Created")) return "Linked";
      if (kinds.includes("Amended")) return "Updated";
      return "Created";
    case "Quarantined":
      return "Quarantined";
    case "Ignored":
      return "Skipped";
    case "Dismissed":
      return "Resolved by operator";
    case "RetryPending":
      return "Retrying";
    case "InDoubt":
      return "Unresolved (verifying in SAP)";
    case "Sending":
      return "Unresolved (mid-send)";
    default:
      return "Pending";
  }
}

export function decidingLayer(layer: string): 1 | 2 | 3 | null {
  const m = /layer\s*(\d)/i.exec(layer);
  if (!m) return null;
  const n = Number(m[1]);
  return n === 1 || n === 2 || n === 3 ? n : null;
}
