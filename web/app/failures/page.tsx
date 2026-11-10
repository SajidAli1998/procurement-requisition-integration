"use client";

import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Eraser, Play, RotateCcw } from "lucide-react";
import { PageHeader } from "@/components/page-header";
import { ActionStatus } from "@/components/action-status";
import { ErrorPanel } from "@/components/error-panel";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, DialogTrigger, DialogClose } from "@/components/ui/dialog";
import { FaultStatePanel, TARGET_LABEL, type Target } from "@/components/failures/fault-state-panel";
import { SettingsForm } from "@/components/failures/settings-form";
import { ScriptBuilder } from "@/components/failures/script-builder";
import { Presets } from "@/components/failures/presets";
import { DbCrashPanel } from "@/components/failures/db-crash-panel";
import { LiveResponses } from "@/components/failures/live-responses";
import { useLabAction } from "@/hooks/use-lab-action";
import { WORKSPACE_KEY, api, isApiError, qk } from "@/lib/api";
import type { Dataset, WorkspaceRun } from "@/lib/types";

export default function FailuresPage() {
  const [target, setTarget] = useState<Target>("sap");
  const ws = useQuery({ queryKey: qk.workspace, queryFn: api.workspace, refetchInterval: 5_000 });

  const clear = useLabAction<void, void>({
    name: "Clear all faults",
    run: () => api.clearFaults(),
    invalidate: [qk.workspace],
    confirm: () => "The lab cleared every random setting, scripted fault, DB fault and crash point. The state panel shows what the lab now reports.",
  });
  const sync = useLabAction<void, WorkspaceRun>({
    name: "Run sync",
    run: () => api.sync(),
    invalidate: [WORKSPACE_KEY],
    confirm: (run) => `The lab started run ${run.id}. Watch the latest responses below; the run's outcome is in the control center.`,
  });
  const reset = useLabAction<{ dataset: Dataset; seed?: number; count?: number }, void>({
    name: "Reset to fixture",
    run: (v) => api.resetWorkspace(v),
    invalidate: [WORKSPACE_KEY],
    confirm: async (_r, v) => {
      const now = await api.workspace();
      return `The lab rebuilt the ${now.dataset} workspace${v.dataset === "Generated" ? ` (seed ${now.seed ?? "?"}, ${now.count ?? "?"} records)` : ""}: source, SAP, database and faults are back to the fixture.`;
    },
  });

  const dataset = (ws.data?.dataset ?? "Workbook") as Dataset;
  const syncConflict = sync.phase === "failed" && isApiError(sync.error) && sync.error.status === 409;

  return (
    <div className="space-y-4">
      <PageHeader
        title="Failure injection lab"
        description="Make the SAP and source mocks misbehave on purpose, run the real integration against them, and inspect the exact responses. Every change goes to the isolated lab's mocks only."
        actions={
          <>
            <Button onClick={() => void sync.execute()} disabled={sync.pending}>
              <Play /> Run sync now
            </Button>
            <Button variant="outline" onClick={() => void clear.execute()} disabled={clear.pending}>
              <Eraser /> Clear all faults
            </Button>
            <Dialog>
              <DialogTrigger asChild>
                <Button variant="outline" disabled={reset.pending}>
                  <RotateCcw /> Reset to fixture
                </Button>
              </DialogTrigger>
              <DialogContent>
                <DialogHeader>
                  <DialogTitle>Reset the lab to the {dataset} fixture?</DialogTitle>
                  <DialogDescription>
                    This rebuilds the whole lab workspace: source records, SAP documents, the integration database, run history and all faults are replaced by a fresh {dataset} fixture
                    {dataset === "Generated" ? ` (seed ${ws.data?.seed ?? 42}, ${ws.data?.count ?? 250} records, as the lab reports it was built)` : ""}. Edits made in the lab are lost. No real system is touched.
                  </DialogDescription>
                </DialogHeader>
                <DialogFooter>
                  <DialogClose asChild>
                    <Button variant="outline">Cancel</Button>
                  </DialogClose>
                  <DialogClose asChild>
                    <Button variant="destructive" onClick={() => void reset.execute({ dataset, seed: ws.data?.seed ?? undefined, count: ws.data?.count ?? undefined })}>
                      Reset lab
                    </Button>
                  </DialogClose>
                </DialogFooter>
              </DialogContent>
            </Dialog>
          </>
        }
      />

      <div className="space-y-2">
        {syncConflict ? (
          <ActionStatus phase="failed" message="A run is already in progress (HTTP 409). The lab runs one sync at a time; wait for it to finish or stop it in the control center." />
        ) : (
          <ActionStatus phase={sync.phase} message={sync.message} />
        )}
        <ActionStatus phase={clear.phase} message={clear.message} />
        <ActionStatus phase={reset.phase} message={reset.message} />
      </div>

      {ws.isError ? <ErrorPanel error={ws.error} onRetry={() => void ws.refetch()} /> : null}
      {ws.isPending ? (
        <Skeleton className="h-40 w-full" />
      ) : ws.data ? (
        <FaultStatePanel sap={ws.data.faults.sap} source={ws.data.faults.source} crashArmed={ws.data.crashArmed} crashFiredAt={ws.data.crashFiredAt ?? null} dbFaultsArmed={ws.data.dbFaultsArmed ?? []} dbFaultsFired={ws.data.dbFaultsFired ?? []} />
      ) : null}

      <div className="flex flex-wrap items-center gap-3">
        <span className="text-sm font-medium" id="target-label">
          Target
        </span>
        <div role="group" aria-labelledby="target-label" className="inline-flex gap-1 rounded-lg bg-muted p-1">
          {(["sap", "source"] as const).map((t) => (
            <button
              key={t}
              type="button"
              aria-pressed={target === t}
              onClick={() => setTarget(t)}
              className={cn(
                "rounded-md px-3 py-1 text-sm font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring",
                target === t ? "bg-background text-foreground shadow-sm" : "text-muted-foreground",
              )}
            >
              {t === "sap" ? "SAP" : "Source"}
            </button>
          ))}
        </div>
        <span className="text-xs text-muted-foreground">Settings, scripts and the response list below apply to the {TARGET_LABEL[target]} mock.</span>
      </div>

      <div className="grid gap-4 xl:grid-cols-2">
        <SettingsForm key={`settings-${target}`} target={target} current={ws.data?.faults[target]?.settings ?? null} />
        <LiveResponses target={target} />
      </div>
      <ScriptBuilder key={`script-${target}`} target={target} />
      <Presets currentSettings={{ sap: ws.data?.faults.sap?.settings ?? null, source: ws.data?.faults.source?.settings ?? null }} />
      <DbCrashPanel />
    </div>
  );
}
