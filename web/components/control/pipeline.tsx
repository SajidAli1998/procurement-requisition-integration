"use client";

import { useMemo, useState } from "react";
import { ArrowRight } from "lucide-react";
import { HttpExchangeList } from "@/components/http-exchange-list";
import { HttpStatusBadge } from "@/components/status-badge";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { fmtMs } from "@/lib/format";
import { STAGES, groupByStage, isErrorExchange, type StageId } from "@/lib/pipeline";
import type { HttpExchange, WorkspaceRun } from "@/lib/types";
import { cn } from "@/lib/utils";

interface StageStats {
  count: number;
  errors: number;
  avgMs: number | null;
  last?: HttpExchange;
}

function stats(list: HttpExchange[]): StageStats {
  const timed = list.filter((e) => typeof e.durationMs === "number");
  return {
    count: list.length,
    errors: list.filter(isErrorExchange).length,
    avgMs: timed.length ? timed.reduce((s, e) => s + e.durationMs, 0) / timed.length : null,
    last: list[list.length - 1],
  };
}

function eventSummary(stage: StageId, run: WorkspaceRun | null): [string, number][] {
  const ev = run?.events ?? {};
  const pick = (...keys: string[]) => keys.filter((k) => ev[k] !== undefined).map((k) => [k, ev[k]] as [string, number]);
  switch (stage) {
    case "source":
    case "pagination":
      return pick("Extracted");
    case "validation":
      return pick("Staged", "Quarantined", "VersionRefreshed");
    case "approval":
      return pick("Ignored", "Staged");
    case "duplicate":
      return pick("AlreadyInSap", "InDoubtResolved", "Conflict");
    case "sap-request":
    case "sap-response":
      return pick("Created", "Amended", "RetryScheduled", "AlreadyInSap");
    case "writeback":
      return pick("WrittenBack", "StatusUpdated");
    case "reconciliation":
      return [];
  }
}

export function Pipeline({ exchanges, running, run, activeStage }: { exchanges: HttpExchange[]; running: boolean; run: WorkspaceRun | null; activeStage: StageId | null }) {
  const [open, setOpen] = useState<StageId | null>(null);
  const grouped = useMemo(() => groupByStage(exchanges), [exchanges]);
  const selected = STAGES.find((s) => s.id === open);
  const selectedList = open ? [...grouped[open]].reverse() : [];
  return (
    <div>
      <ol className="flex flex-col gap-2 md:flex-row md:flex-wrap md:items-stretch" aria-label="Integration pipeline" data-testid="pipeline">
        {STAGES.map((s, i) => {
          const st = stats(grouped[s.id]);
          const active = running && activeStage === s.id;
          return (
            <li key={s.id} className="flex items-center gap-2 md:contents">
              <button
                type="button"
                onClick={() => setOpen(s.id)}
                data-stage={s.id}
                data-active={active}
                aria-haspopup="dialog"
                className={cn(
                  "flex min-w-0 flex-1 flex-col items-start gap-1 rounded-lg border bg-card p-2.5 text-left text-xs shadow-sm transition-colors hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring md:min-w-[8.5rem] md:flex-none",
                  active && "pipeline-active border-primary",
                  st.errors > 0 && "border-red-500/60",
                )}
              >
                <span className="flex w-full items-center justify-between gap-2">
                  <span className="text-sm font-semibold">{s.label}</span>
                  <span className="text-muted-foreground">{i + 1}</span>
                </span>
                <span className="tabular-nums text-muted-foreground">
                  {st.count} call{st.count === 1 ? "" : "s"}
                  {st.errors ? <span className="text-red-600 dark:text-red-400"> · {st.errors} err</span> : null}
                  {st.avgMs !== null ? ` · ${fmtMs(st.avgMs)}` : ""}
                </span>
                {st.last ? <HttpStatusBadge status={st.last.status} /> : <span className="text-muted-foreground">{eventSummary(s.id, run).map(([k, v]) => `${k} ${v}`).join(", ") || "-"}</span>}
              </button>
              {i < STAGES.length - 1 ? (
                <span aria-hidden className="hidden items-center md:flex">
                  {running ? <span className="pipeline-flow block h-0.5 w-4" /> : <ArrowRight className="size-4 text-muted-foreground" />}
                </span>
              ) : null}
            </li>
          );
        })}
      </ol>
      <Sheet open={open !== null} onOpenChange={(o) => !o && setOpen(null)}>
        <SheetContent>
          {selected ? (
            <>
              <SheetHeader>
                <SheetTitle>{selected.label}</SheetTitle>
                <SheetDescription>{selected.description}</SheetDescription>
              </SheetHeader>
              <div className="space-y-3 text-sm">
                <div>
                  <h3 className="mb-1 font-semibold">Run counters ({run ? run.id : "no run yet"})</h3>
                  {eventSummary(selected.id, run).length === 0 ? (
                    <p className="text-muted-foreground">No counters for this stage{run?.state === "Running" ? " until the run finishes" : ""}.</p>
                  ) : (
                    <ul className="flex flex-wrap gap-2">
                      {eventSummary(selected.id, run).map(([k, v]) => (
                        <li key={k} className="rounded border px-2 py-0.5 tabular-nums">
                          {k}: {v}
                        </li>
                      ))}
                    </ul>
                  )}
                  {selected.id === "reconciliation" && run?.openQuarantine !== undefined && run?.openQuarantine !== null ? (
                    <p className="mt-1">
                      Open quarantine after the run: {run.openQuarantine}; pending: {run.pending ?? 0}. See the reconciliation page for the invariants.
                    </p>
                  ) : null}
                </div>
                <div>
                  <h3 className="mb-1 font-semibold">Recent exchanges (newest first): inputs, outputs, durations, statuses, correlation ids</h3>
                  <HttpExchangeList exchanges={selectedList} limit={60} empty="No HTTP exchanges for this stage in the current stream. Stages without HTTP traffic are described by the run counters above." />
                </div>
              </div>
            </>
          ) : null}
        </SheetContent>
      </Sheet>
    </div>
  );
}
