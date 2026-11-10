"use client";

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Bomb, CalendarClock, Play, Power, RefreshCw, Repeat, Scale, Square, StepForward, Timer } from "lucide-react";
import { ActionStatus } from "@/components/action-status";
import { PageHeader } from "@/components/page-header";
import { QueryState } from "@/components/query-state";
import { StatCard } from "@/components/stat-card";
import { HttpExchangeList } from "@/components/http-exchange-list";
import { InvariantBadge, ToneBadge } from "@/components/status-badge";
import { httpEvents, useLive } from "@/components/live-provider";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { useLabAction } from "@/hooks/use-lab-action";
import { api, isApiError, qk, WORKSPACE_KEY } from "@/lib/api";
import { fmtMs, fmtNumber } from "@/lib/format";
import { eventsOfLatestRun, isErrorExchange, requisitionsTouched, stagesFor, type StageId } from "@/lib/pipeline";
import type { InvariantRow, WorkspaceRun } from "@/lib/types";
import { Pipeline } from "./pipeline";
import { RunHistory } from "./run-history";

function useNow(intervalMs: number, enabled: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!enabled) return;
    const t = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(t);
  }, [intervalMs, enabled]);
  return now;
}

function ConflictNotice({ error }: { error: unknown }) {
  if (!isApiError(error) || error.status !== 409) return null;
  return (
    <Alert variant="warning" data-testid="run-conflict">
      <AlertTitle>A run is already in progress (HTTP 409)</AlertTitle>
      <AlertDescription>
        {error.problem?.detail ?? "One run at a time, as the integration's lease enforces."} Your request was refused and nothing new was started. Wait for the current run to finish (it is shown below), or stop it.
      </AlertDescription>
    </Alert>
  );
}

export function ControlCenter() {
  const live = useLive();
  const workspace = useQuery({ queryKey: qk.workspace, queryFn: api.workspace, refetchInterval: live.activeRun ? 2_000 : 10_000 });
  const dashboard = useQuery({ queryKey: qk.dashboard, queryFn: api.dashboard, refetchInterval: live.activeRun ? 3_000 : 15_000 });
  const runs = useQuery({ queryKey: qk.runs, queryFn: api.runs, refetchInterval: live.activeRun ? 2_000 : 15_000 });

  // The backend decides whether a run is active. The stream is just faster; the GET covers refreshes and other tabs.
  const backendActive = workspace.data?.active ?? null;
  const activeRun: WorkspaceRun | null = backendActive ?? (workspace.isFetched ? null : live.activeRun);
  const running = !!activeRun;
  const now = useNow(500, running);

  const latest = useMemo(() => eventsOfLatestRun(live.events), [live.events]);
  const runExchanges = useMemo(() => httpEvents(latest.events), [latest.events]);
  const lastRun = runs.data?.[0] ?? null;
  const shownRun = activeRun ?? latest.finished ?? lastRun;
  const exchangesForPipeline = runExchanges.length > 0 ? runExchanges : httpEvents(live.events);
  const activeStage = useMemo<StageId | null>(() => {
    const last = runExchanges[runExchanges.length - 1];
    return last ? (stagesFor(last)[0] ?? null) : null;
  }, [runExchanges]);
  const touched = useMemo(() => requisitionsTouched(runExchanges), [runExchanges]);
  const errors = useMemo(() => [...runExchanges].filter(isErrorExchange).reverse().slice(0, 30), [runExchanges]);

  const [seconds, setSeconds] = useState("10");
  const [replayIds, setReplayIds] = useState("");
  const [crashPoint, setCrashPoint] = useState<"after-sap-create" | "mid-page">("after-sap-create");
  const [reconcileResult, setReconcileResult] = useState<InvariantRow[] | null>(null);

  const runAccepted = (r: WorkspaceRun) => `run ${r.id} (${r.kind}) accepted by the lab and started. Its outcome appears below when the lab reports it finished.`;

  const sync = useLabAction({ name: "Run sync", run: () => api.sync(), confirm: runAccepted });
  const refreshStatuses = useLabAction({ name: "Refresh SAP statuses", run: () => api.sync(), confirm: runAccepted });
  const schedule = useLabAction<boolean, { enabled: boolean; seconds: number }>({
    name: "Schedule",
    run: (enabled) => api.schedule(enabled, Number(seconds) || 10),
    confirm: (r) => (r.enabled ? `the lab reports the schedule running every ${r.seconds}s.` : "the lab reports the schedule paused."),
  });
  const stop = useLabAction({
    name: "Stop",
    run: () => api.stop(),
    confirm: (r) => (r.stopped ? "the lab cancelled the run in progress like a killed process. Records that were mid-send are re-checked in SAP on the next run." : "the lab reports no run was in progress (the schedule, if any, is paused)."),
  });
  const replay = useLabAction<string[], WorkspaceRun>({ name: "Replay", run: (ids) => api.replay(ids), confirm: runAccepted });
  const crash = useLabAction({ name: "Simulate crash", run: () => api.crash(crashPoint), confirm: (r) => `the lab armed a crash at "${r.armed}". It fires during the next run that reaches that point.` });
  const restart = useLabAction({ name: "Restart process", run: () => api.restart(), confirm: () => "the lab restarted the integration process (in-memory state dropped, database kept). Run a sync to see recovery." });
  const reconcile = useLabAction({
    name: "Reconcile",
    run: () => api.reconcile(),
    invalidate: [WORKSPACE_KEY],
    confirm: (r) => {
      const inv = (r as { invariants?: { name: string; outcome: unknown; detail: string }[] }).invariants ?? [];
      const rows = inv.map((i) => ({ name: i.name, outcome: String(i.outcome), detail: i.detail }));
      setReconcileResult(rows);
      const failed = rows.filter((i) => i.outcome === "Fail").length;
      return `the lab reconciled source, SAP and database: ${rows.length - failed} invariant(s) pass, ${failed} fail.`;
    },
  });
  const advance = useLabAction({ name: "Advance SAP lifecycle", run: () => api.advanceSap(), confirm: () => "the SAP mock moved its documents one lifecycle step. Run a sync (status sync) to write the new statuses back to the source." });

  const anyRunStarting = sync.pending || replay.pending || refreshStatuses.pending;
  const ids = replayIds
    .split(/[\s,;]+/)
    .map((s) => s.trim())
    .filter(Boolean);
  const t = dashboard.data?.totals;
  const elapsed = activeRun ? Math.max(0, now - new Date(activeRun.startedAt).getTime()) : null;
  const approved = t?.approved ?? 0;

  const lastAction = [sync, refreshStatuses, schedule, stop, replay, crash, restart, reconcile, advance].filter((a) => a.phase !== "idle").sort((a, b) => (b.at ?? 0) - (a.at ?? 0))[0];
  const lastConflict = [sync, refreshStatuses, replay].find((a) => a.phase === "failed" && isApiError(a.error) && a.error.status === 409);

  return (
    <div className="space-y-4">
      <PageHeader
        title="Integration control center"
        description="Drive the real integration worker inside the isolated lab. Every button calls the Test Lab API; results are shown only once the lab reports them."
      />

      <Card>
        <CardHeader>
          <CardTitle>Actions</CardTitle>
          <CardDescription>Validation and idempotency always apply: there is no way to bypass them from here.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-3">
          <div className="flex flex-wrap gap-2">
            <Button onClick={() => void sync.execute()} disabled={sync.pending || anyRunStarting || running} data-testid="run-sync">
              <Play /> {sync.pending ? "Starting..." : running ? "Run in progress" : "Run sync"}
            </Button>
            <Button variant="outline" onClick={() => void stop.execute()} disabled={stop.pending} title="Cancels the run like a killed process">
              <Square /> Stop
            </Button>
            <Button variant="outline" onClick={() => void reconcile.execute()} disabled={reconcile.pending}>
              <Scale /> Reconcile
            </Button>
            <Button variant="outline" onClick={() => void refreshStatuses.execute()} disabled={refreshStatuses.pending || anyRunStarting || running} title="Status sync is part of every run">
              <RefreshCw /> Refresh SAP statuses
            </Button>
            <Button variant="outline" onClick={() => void advance.execute()} disabled={advance.pending}>
              <StepForward /> Advance SAP lifecycle
            </Button>
            <Button variant="outline" onClick={() => void restart.execute()} disabled={restart.pending}>
              <Power /> Restart process
            </Button>
          </div>
          <p className="text-xs text-muted-foreground">
            <strong>Stop</strong> acts like a killed process: the run is cancelled where it is; records that were mid-send stay &quot;Unresolved&quot; and are re-checked in SAP (lookup by reference) on the next run, never blindly re-created.{" "}
            <strong>Refresh SAP statuses</strong> starts a normal sync: reading SAP statuses and writing them back is part of every run.
          </p>
          {lastConflict ? <ConflictNotice error={lastConflict.error} /> : null}
          {lastAction ? <ActionStatus phase={lastAction.phase} message={lastAction.message} /> : null}

          <div className="grid gap-4 border-t pt-3 md:grid-cols-3">
            <div className="space-y-2">
              <div className="flex items-center gap-2 text-sm font-medium">
                <CalendarClock className="size-4" /> Schedule
              </div>
              <div className="flex items-center gap-2">
                <Switch
                  id="schedule-enabled"
                  checked={!!workspace.data?.scheduled}
                  disabled={schedule.pending || !workspace.data}
                  onCheckedChange={(v) => void schedule.execute(v)}
                  aria-label="Scheduled syncs"
                />
                <Label htmlFor="schedule-enabled">{workspace.data?.scheduled ? `Running every ${workspace.data.scheduleSeconds}s` : "Paused"}</Label>
              </div>
              <div className="flex items-center gap-2">
                <Label htmlFor="schedule-seconds" className="whitespace-nowrap">
                  Every (seconds)
                </Label>
                <Input id="schedule-seconds" inputMode="numeric" value={seconds} onChange={(e) => setSeconds(e.target.value.replace(/[^\d]/g, ""))} className="w-20" />
              </div>
            </div>
            <div className="space-y-2">
              <div className="flex items-center gap-2 text-sm font-medium">
                <Repeat /> Replay
              </div>
              <Label htmlFor="replay-ids">Requisition ids (comma or space separated)</Label>
              <Input id="replay-ids" value={replayIds} onChange={(e) => setReplayIds(e.target.value)} placeholder="REQ-7071, REQ-7073" />
              <Button size="sm" variant="outline" onClick={() => void replay.execute(ids)} disabled={replay.pending || ids.length === 0 || running}>
                <Repeat /> {ids.length > 1 ? `Replay ${ids.length} selected` : "Replay one"}
              </Button>
              <p className="text-xs text-muted-foreground">Replays go through the normal pipeline: validation, duplicate detection and idempotency all apply.</p>
            </div>
            <div className="space-y-2">
              <div className="flex items-center gap-2 text-sm font-medium">
                <Bomb className="size-4" /> Simulate crash
              </div>
              <Label htmlFor="crash-point">Crash point</Label>
              <NativeSelect id="crash-point" value={crashPoint} onChange={(e) => setCrashPoint(e.target.value as typeof crashPoint)}>
                <option value="after-sap-create">After SAP created a requisition, before the mapping is saved</option>
                <option value="mid-page">While saving a page checkpoint</option>
              </NativeSelect>
              <Button size="sm" variant="outline" onClick={() => void crash.execute()} disabled={crash.pending}>
                <Bomb /> Arm crash
              </Button>
            </div>
          </div>
          {reconcileResult ? (
            <ul className="space-y-1 border-t pt-3 text-sm" aria-label="Reconcile result">
              {reconcileResult.map((i) => (
                <li key={i.name} className="flex items-start gap-2">
                  <InvariantBadge outcome={i.outcome} />
                  <span>
                    <span className="font-medium">{i.name}</span> <span className="text-muted-foreground break-anywhere">{i.detail}</span>
                  </span>
                </li>
              ))}
            </ul>
          ) : null}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="flex flex-wrap items-center gap-2">
            Current run
            {activeRun ? (
              <ToneBadge tone="info">
                <Timer className="animate-pulse" /> Running
              </ToneBadge>
            ) : shownRun ? (
              <ToneBadge tone="neutral">Idle (last run shown)</ToneBadge>
            ) : (
              <ToneBadge tone="neutral">Idle</ToneBadge>
            )}
          </CardTitle>
          <CardDescription>Live from the workspace event stream ({live.status}); counts are confirmed by the lab when the run finishes.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6" data-testid="current-run">
            <StatCard label="Run" value={<span className="font-mono text-sm">{shownRun?.id ?? "-"}</span>} hint={shownRun ? `${shownRun.kind} · ${shownRun.state === "Running" ? "running" : (shownRun.outcome ?? "")}` : undefined} />
            <StatCard label="Duration" value={activeRun ? fmtMs(elapsed) : fmtMs(shownRun?.durationMs)} />
            <StatCard label="Reached SAP (live)" value={fmtNumber(touched.size)} hint={approved ? `remaining ≈ ${Math.max(0, approved - touched.size)} of ${approved} approved (estimate from live traffic)` : "estimate from live traffic"} />
            <StatCard label="Retry queue" value={fmtNumber(t?.retrying)} tone={t?.retrying ? "warning" : "neutral"} />
            <StatCard label="Quarantine" value={fmtNumber(t?.quarantined)} tone={t?.quarantined ? "danger" : "neutral"} />
            <StatCard label="Breaker" value={<span className="text-sm">{dashboard.data?.health.breaker ?? "-"}</span>} tone={dashboard.data?.health.breaker.startsWith("Open") ? "danger" : "success"} />
          </div>
          <div>
            <h3 className="mb-2 text-sm font-semibold">Pipeline</h3>
            <Pipeline exchanges={exchangesForPipeline} running={running} run={shownRun} activeStage={activeStage} />
            <p className="mt-1 text-xs text-muted-foreground">Click a stage for its recent exchanges, inputs, outputs, durations, statuses and correlation ids.</p>
          </div>
          <div>
            <div className="mb-2 flex items-center justify-between">
              <h3 className="text-sm font-semibold">Latest errors in this run</h3>
              <Link href="/http" className="text-xs text-primary underline underline-offset-2">
                Full HTTP log
              </Link>
            </div>
            <HttpExchangeList exchanges={errors} limit={30} empty="No failed exchanges in the current stream." />
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>Run history</CardTitle>
        </CardHeader>
        <CardContent>
          <QueryState query={runs} title="Could not load run history">
            {(data) => <RunHistory runs={data} />}
          </QueryState>
        </CardContent>
      </Card>
    </div>
  );
}
