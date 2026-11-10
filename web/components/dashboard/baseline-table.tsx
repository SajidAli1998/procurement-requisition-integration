"use client";

import { Check, Minus, X } from "lucide-react";
import type { Baseline, DashboardTotals, ReconciliationView } from "@/lib/types";
import { cn } from "@/lib/utils";

interface BaselineRow {
  metric: string;
  expected: string;
  actual: string | null;
  note?: string;
}

// A metric the lab cannot measure says so; the expected value is never copied across.
export function baselineRows(baseline: Baseline, totals: DashboardTotals | null, reconciliation: ReconciliationView | null | undefined, dataset: string): BaselineRow[] {
  return baseline.metrics.map((m) => {
    const t = totals;
    switch (m.metric) {
      case "Total":
        return { ...m, actual: t ? String(t.total) : null };
      case "Created":
        return { ...m, actual: t ? String(t.created) : null };
      case "Existing duplicates linked":
        return { ...m, actual: t ? String(t.linked) : null };
      case "Quarantined":
        return { ...m, actual: t ? String(t.quarantined) : null };
      case "Skipped":
        return { ...m, actual: t ? String(t.skipped) : null };
      case "Newly created duplicates":
        return { ...m, actual: t ? String(t.duplicateReferencesInSap) : null, note: "references with more than one SAP document" };
      case "Reconciliation matches":
        if (dataset !== "Workbook") return { ...m, actual: null, note: "only measured on the Workbook dataset" };
        return reconciliation?.comparison
          ? { ...m, actual: `${reconciliation.comparison.matched}/${reconciliation.comparison.total}` }
          : { ...m, actual: null, note: "loading reconciliation..." };
      case "AI contract tests":
        return { ...m, actual: null, note: "run category 14 in the scenario runner" };
      case "Existing automated tests":
        return { ...m, actual: null, note: "measured by dotnet test, not by the lab" };
      default:
        return { ...m, actual: null };
    }
  });
}

export function BaselineTable({ rows, source }: { rows: BaselineRow[]; source: string }) {
  return (
    <div>
      <div className="overflow-x-auto rounded-md border">
        <table className="w-full text-sm" data-testid="baseline-table">
          <caption className="sr-only">Expected values from the client workbook beside actual lab values</caption>
          <thead className="bg-muted/40 text-xs text-muted-foreground">
            <tr>
              <th scope="col" className="px-2 py-2 text-left font-semibold">Metric</th>
              <th scope="col" className="px-2 py-2 text-right font-semibold">Expected (client workbook)</th>
              <th scope="col" className="px-2 py-2 text-right font-semibold">Actual (lab)</th>
              <th scope="col" className="px-2 py-2 text-center font-semibold">Match</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => {
              const match = r.actual === null ? null : r.actual === r.expected;
              return (
                <tr key={r.metric} className={cn("border-t", match === false && "bg-red-50/60 dark:bg-red-950/20")} data-metric={r.metric}>
                  <th scope="row" className="px-2 py-1.5 text-left font-medium">{r.metric}</th>
                  <td className="px-2 py-1.5 text-right tabular-nums text-muted-foreground" data-col="expected">
                    {r.expected}
                  </td>
                  <td className="px-2 py-1.5 text-right tabular-nums font-semibold" data-col="actual">
                    {r.actual ?? <span className="text-xs font-normal text-muted-foreground">not measured{r.note ? ` (${r.note})` : ""}</span>}
                  </td>
                  <td className="px-2 py-1.5 text-center">
                    {match === null ? (
                      <Minus className="mx-auto size-4 text-muted-foreground" aria-label="not compared" />
                    ) : match ? (
                      <Check className="mx-auto size-4 text-emerald-600" aria-label="matches" />
                    ) : (
                      <X className="mx-auto size-4 text-red-600" aria-label="differs" />
                    )}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <p className="mt-1 text-xs text-muted-foreground">
        Expected values are a fixture from {source}. They are never shown as actual results.
      </p>
    </div>
  );
}
