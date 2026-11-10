"use client";

import { Ban, CheckCircle2, Loader2, X, XCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Progress } from "@/components/ui/progress";
import { ActionStatus } from "@/components/action-status";
import type { ActionPhase } from "@/hooks/use-lab-action";
import type { StreamStatus } from "@/hooks/use-event-source";
import { fmtTime } from "@/lib/format";
import { cn } from "@/lib/utils";
import type { LiveBatch } from "./use-batch-run";

export function BatchProgress({
  live,
  stream,
  onCancel,
  cancelPhase,
  cancelMessage,
  onDismiss,
}: {
  live: LiveBatch;
  stream: StreamStatus;
  onCancel: () => void;
  cancelPhase: ActionPhase;
  cancelMessage?: string;
  onDismiss: () => void;
}) {
  const t = live.tally;
  const total = t?.total ?? live.total;
  const done = t ? t.total - t.notRun : 0;
  const running = live.state === "Running";
  const current = Object.entries(live.statuses)
    .filter(([, s]) => s === "Running")
    .map(([id]) => id);
  const recent = live.logs.slice(-8).reverse();
  const StateIcon = running ? Loader2 : live.state === "Finished" ? CheckCircle2 : live.state === "Cancelled" ? Ban : XCircle;

  return (
    <section aria-label="Batch progress" data-testid="batch-progress" data-state={live.state} className="rounded-lg border bg-card p-3 shadow-sm">
      <div className="flex flex-wrap items-center gap-2">
        <StateIcon className={cn("size-4", running && "animate-spin")} aria-hidden />
        <h2 className="text-sm font-semibold">
          Batch <span className="break-anywhere">{live.name}</span>
        </h2>
        <Badge tone={running ? "info" : live.state === "Finished" ? "success" : live.state === "Cancelled" ? "warning" : "danger"} data-testid="batch-state">
          {live.state}
        </Badge>
        <span className="text-xs text-muted-foreground">
          {live.savedId ? `saved as ${live.savedId}` : `id ${live.id}`} · stream {stream}
        </span>
        <div className="ml-auto flex gap-2">
          {running ? (
            <Button size="sm" variant="destructive" onClick={onCancel} disabled={cancelPhase === "sending" || cancelPhase === "confirming"}>
              <Ban /> Cancel batch
            </Button>
          ) : (
            <Button size="sm" variant="ghost" onClick={onDismiss} aria-label="Dismiss batch progress">
              <X /> Dismiss
            </Button>
          )}
        </div>
      </div>
      <div className="mt-2 flex items-center gap-2">
        <Progress value={done} max={Math.max(1, total)} label="Scenarios finished" className="flex-1" />
        <span className="text-xs tabular-nums text-muted-foreground" aria-live="polite">
          {done} / {total}
        </span>
      </div>
      {t ? (
        <dl className="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs" aria-label="Live tally">
          <div>
            <dt className="inline text-muted-foreground">Executed </dt>
            <dd className="inline font-semibold tabular-nums">{t.executed}</dd>
          </div>
          <div>
            <dt className="inline text-emerald-700 dark:text-emerald-400">Passed </dt>
            <dd className="inline font-semibold tabular-nums">{t.passed}</dd>
          </div>
          <div>
            <dt className="inline text-red-700 dark:text-red-400">Failed </dt>
            <dd className="inline font-semibold tabular-nums">{t.failed}</dd>
          </div>
          <div>
            <dt className="inline text-amber-700 dark:text-amber-400">Blocked </dt>
            <dd className="inline font-semibold tabular-nums">{t.blocked}</dd>
          </div>
          <div>
            <dt className="inline text-muted-foreground">Not implemented </dt>
            <dd className="inline font-semibold tabular-nums">{t.notImplemented}</dd>
          </div>
          <div>
            <dt className="inline text-muted-foreground">Not finished </dt>
            <dd className="inline font-semibold tabular-nums">{t.notRun}</dd>
          </div>
        </dl>
      ) : (
        <p className="mt-2 text-xs text-muted-foreground">Waiting for the first event from the runner...</p>
      )}
      {current.length > 0 ? (
        <p className="mt-1 text-xs text-muted-foreground break-anywhere">Running now: {current.join(", ")}</p>
      ) : null}
      {live.error ? <p className="mt-1 text-xs text-red-700 dark:text-red-400 break-anywhere">Runner error: {live.error}</p> : null}
      {!running && live.state === "Finished" && !live.savedId ? (
        <p className="mt-1 text-xs text-muted-foreground">The batch finished but the runner did not report a saved batch id; results are taken from history once it appears.</p>
      ) : null}
      {recent.length > 0 ? (
        <ol className="mt-2 max-h-36 space-y-0.5 overflow-y-auto rounded border bg-muted/30 p-2 font-mono text-[11px]" aria-label="Latest log lines">
          {recent.map((l, i) => (
            <li key={`${l.log.at}-${i}`} className="break-anywhere">
              <span className="text-muted-foreground">{fmtTime(l.log.at)}</span> {l.scenarioId ? <span className="font-semibold">{l.scenarioId}</span> : null} {l.log.text}
              {l.log.simulated ? <Badge tone="muted" className="ml-1 py-0 text-[10px]">simulated</Badge> : null}
            </li>
          ))}
        </ol>
      ) : null}
      {cancelPhase !== "idle" ? <ActionStatus phase={cancelPhase} message={cancelMessage} className="mt-2" /> : null}
    </section>
  );
}
