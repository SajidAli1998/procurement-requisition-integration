"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Download, GitCompare } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { ScenarioStatusBadge } from "@/components/status-badge";
import { ErrorPanel } from "@/components/error-panel";
import { ActionStatus } from "@/components/action-status";
import { api, qk, saveResponse } from "@/lib/api";
import { compareBatches, type StatusChange } from "@/lib/scenarios";
import { fmtDateTime } from "@/lib/format";
import { useLabAction } from "@/hooks/use-lab-action";
import type { BatchResult, BatchSummary } from "@/lib/types";
import { cn } from "@/lib/utils";

const FORMATS = ["json", "csv", "md", "html"] as const;
const KIND_TONE: Record<StatusChange["kind"], "success" | "danger" | "warning" | "info" | "muted" | "neutral"> = {
  fixed: "success",
  regressed: "danger",
  changed: "warning",
  new: "info",
  removed: "muted",
  same: "neutral",
};

function TallyLine({ b }: { b: BatchSummary }) {
  const t = b.tally;
  return (
    <span className="text-xs tabular-nums text-muted-foreground">
      {t.total} · <span className="text-emerald-700 dark:text-emerald-400">{t.passed} pass</span> · <span className="text-red-700 dark:text-red-400">{t.failed} fail</span> ·{" "}
      <span className="text-amber-700 dark:text-amber-400">{t.blocked} blocked</span>
      {t.notImplemented ? ` · ${t.notImplemented} not impl.` : ""}
      {t.notRun ? ` · ${t.notRun} not run` : ""}
    </span>
  );
}

export function HistoryPanel({
  history,
  selectedId,
  onSelect,
  selected,
}: {
  history: BatchSummary[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  selected?: BatchResult;
}) {
  const [compare, setCompare] = useState(false);
  const [showSame, setShowSame] = useState(false);
  const idx = history.findIndex((h) => h.id === selectedId);
  const previous = idx >= 0 ? history[idx + 1] : undefined;
  const prevQuery = useQuery({
    queryKey: qk.batchSummary(previous?.id ?? "none"),
    queryFn: () => api.batchSummary(previous!.id),
    enabled: compare && !!previous,
    staleTime: Infinity,
  });

  const exporter = useLabAction<{ id: string; format: (typeof FORMATS)[number] }, void>({
    name: "Export",
    run: async ({ id, format }) => saveResponse(await api.batchExport(id, format), `${id}.${format}`),
    invalidate: [],
    confirm: (_r, v) => `Downloaded ${v.id}.${v.format} as produced by the lab.`,
  });

  const changes = compare && selected && prevQuery.data ? compareBatches(selected, prevQuery.data) : null;
  const shownChanges = changes?.filter((c) => showSame || c.kind !== "same") ?? [];
  const counts = changes?.reduce<Record<string, number>>((acc, c) => ((acc[c.kind] = (acc[c.kind] ?? 0) + 1), acc), {}) ?? {};

  return (
    <div className="flex flex-col gap-4">
      {history.length === 0 ? (
        <p className="text-sm text-muted-foreground">No saved batches yet. Run a scenario to create one.</p>
      ) : (
        <ul className="max-h-80 divide-y overflow-y-auto rounded-md border" aria-label="Batch history">
          {history.map((h) => {
            const on = h.id === selectedId;
            return (
              <li key={h.id}>
                <button
                  type="button"
                  aria-pressed={on}
                  onClick={() => {
                    onSelect(h.id);
                    setCompare(false);
                  }}
                  className={cn("flex w-full flex-col items-start gap-0.5 px-3 py-2 text-left hover:bg-muted/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring", on && "bg-primary/10")}
                >
                  <span className="flex w-full flex-wrap items-center gap-2 text-sm">
                    <span className="font-medium break-anywhere">{h.name}</span>
                    {on ? <Badge tone="info">viewing</Badge> : null}
                    <span className="ml-auto text-xs text-muted-foreground">{fmtDateTime(h.startedAt)}</span>
                  </span>
                  <span className="font-mono text-[11px] text-muted-foreground">{h.id}</span>
                  <TallyLine b={h} />
                </button>
              </li>
            );
          })}
        </ul>
      )}

      {selectedId ? (
        <div className="flex flex-col gap-2">
          <div className="flex flex-wrap items-center gap-2">
            <span className="text-sm font-medium">Export {selectedId}:</span>
            {FORMATS.map((f) => (
              <Button key={f} size="sm" variant="outline" disabled={exporter.pending} onClick={() => void exporter.execute({ id: selectedId, format: f })} aria-label={`Export batch as ${f.toUpperCase()}`}>
                <Download /> {f.toUpperCase()}
              </Button>
            ))}
            <Button size="sm" variant="outline" disabled={!previous} onClick={() => setCompare((v) => !v)} aria-pressed={compare} title={previous ? `Compare with ${previous.id}` : "No earlier batch to compare with"}>
              <GitCompare /> Compare with previous
            </Button>
          </div>
          {exporter.phase !== "idle" ? <ActionStatus phase={exporter.phase} message={exporter.message} /> : null}
        </div>
      ) : null}

      {compare && previous ? (
        <section aria-label="Comparison with previous batch" className="flex flex-col gap-2" data-testid="batch-compare">
          <h3 className="text-sm font-semibold">
            {selectedId} vs {previous.id}
          </h3>
          {prevQuery.isError ? <ErrorPanel error={prevQuery.error} onRetry={() => void prevQuery.refetch()} compact /> : null}
          {prevQuery.isPending || !selected ? <p className="text-sm text-muted-foreground">Loading both batches...</p> : null}
          {changes ? (
            <>
              <div className="flex flex-wrap items-center gap-2 text-xs">
                {(["regressed", "fixed", "changed", "new", "removed", "same"] as const).map((k) => (
                  <Badge key={k} tone={KIND_TONE[k]}>
                    {k}: {counts[k] ?? 0}
                  </Badge>
                ))}
                <label className="ml-auto flex items-center gap-1">
                  <input type="checkbox" checked={showSame} onChange={(e) => setShowSame(e.target.checked)} /> show unchanged
                </label>
              </div>
              {shownChanges.length === 0 ? (
                <p className="text-sm text-muted-foreground">No status changes between the two batches.</p>
              ) : (
                <div className="overflow-x-auto rounded-md border">
                  <table className="w-full text-sm">
                    <thead className="bg-muted/40 text-xs text-muted-foreground">
                      <tr>
                        <th scope="col" className="px-2 py-1 text-left">Scenario</th>
                        <th scope="col" className="px-2 py-1 text-left">Previous</th>
                        <th scope="col" className="px-2 py-1 text-left">Current</th>
                        <th scope="col" className="px-2 py-1 text-left">Change</th>
                      </tr>
                    </thead>
                    <tbody>
                      {shownChanges.map((c) => (
                        <tr key={c.id} className="border-t">
                          <td className="px-2 py-1">
                            <span className="font-mono text-xs">{c.id}</span> <span className="break-anywhere">{c.title}</span>
                          </td>
                          <td className="px-2 py-1">{c.previous ? <ScenarioStatusBadge status={c.previous} /> : <span className="text-xs text-muted-foreground">not in batch</span>}</td>
                          <td className="px-2 py-1">{c.current ? <ScenarioStatusBadge status={c.current} /> : <span className="text-xs text-muted-foreground">not in batch</span>}</td>
                          <td className="px-2 py-1">
                            <Badge tone={KIND_TONE[c.kind]}>{c.kind}</Badge>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          ) : null}
        </section>
      ) : null}
    </div>
  );
}
