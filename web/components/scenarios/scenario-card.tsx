"use client";

import { Loader2, Play } from "lucide-react";
import { memo } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { ScenarioStatusBadge } from "@/components/status-badge";
import { fmtDateTime } from "@/lib/format";
import type { Tone } from "@/lib/status";
import type { Priority, ScenarioDefinition, ScenarioResult } from "@/lib/types";
import type { LiveScenarioStatus } from "./use-batch-run";

const PRIORITY_TONE: Record<Priority, Tone> = { Critical: "danger", High: "critical", Medium: "warning", Low: "muted" };

export interface CardResult {
  result?: ScenarioResult;
  batchId?: string;
  live?: LiveScenarioStatus;
  liveActual?: string;
}

function ScenarioCardImpl({
  scenario,
  info,
  selected,
  onSelect,
  onOpen,
  onRun,
  runDisabled,
}: {
  scenario: ScenarioDefinition;
  info: CardResult;
  selected: boolean;
  onSelect: (id: string, on: boolean) => void;
  onOpen: (id: string) => void;
  onRun: (id: string) => void;
  runDisabled: boolean;
}) {
  const s = scenario;
  const liveRunning = info.live === "Running";
  // Pass/Fail only ever comes from the backend: a stored result, or the runner's scenario-finished event.
  const status = info.live && info.live !== "Running" ? info.live : info.result?.status ?? (s.implemented ? (s.blockedReason ? "Blocked" : "NotRun") : "NotImplemented");
  const actual = info.live && info.live !== "Running" ? info.liveActual : info.result?.actual;
  const checkboxId = `select-${s.id}`;
  return (
    <article data-testid={`scenario-card-${s.id}`} data-scenario-id={s.id} className="flex flex-col gap-2 rounded-lg border bg-card p-3 shadow-sm" aria-labelledby={`title-${s.id}`}>
      <div className="flex items-start gap-2">
        <Checkbox id={checkboxId} checked={selected} onCheckedChange={(v) => onSelect(s.id, v === true)} aria-label={`Select ${s.id}`} className="mt-0.5" />
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="font-mono text-xs font-semibold">{s.id}</span>
            <Badge tone={PRIORITY_TONE[s.priority] ?? "neutral"}>{s.priority}</Badge>
            <Badge tone="neutral">{s.severity}</Badge>
            {liveRunning ? (
              <Badge tone="info" data-status="Running">
                <Loader2 className="animate-spin" aria-hidden /> Running
              </Badge>
            ) : (
              <ScenarioStatusBadge status={status} />
            )}
          </div>
          <h3 id={`title-${s.id}`} className="mt-1 text-sm font-medium">
            <button type="button" className="text-left hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring rounded break-anywhere" onClick={() => onOpen(s.id)}>
              {s.title}
            </button>
          </h3>
          <p className="text-xs text-muted-foreground break-anywhere">{s.category}</p>
        </div>
        <Button size="sm" variant="outline" onClick={() => onRun(s.id)} disabled={runDisabled} aria-label={`Run ${s.id}`}>
          <Play /> Run
        </Button>
      </div>
      <dl className="grid gap-1 text-xs sm:grid-cols-[auto_1fr] sm:gap-x-3">
        <dt className="font-medium text-muted-foreground">Expected</dt>
        <dd className="break-anywhere">{s.expected?.summary || "-"}</dd>
        <dt className="font-medium text-muted-foreground">Actual</dt>
        <dd className="break-anywhere" data-testid="scenario-actual">
          {liveRunning ? (
            <span className="text-muted-foreground">Pending: waiting for the backend result...</span>
          ) : actual ? (
            actual
          ) : status === "Blocked" && s.blockedReason ? (
            <span className="text-muted-foreground">Blocked: {s.blockedReason}</span>
          ) : status === "NotImplemented" ? (
            <span className="text-muted-foreground">Not implemented: never counted as passed</span>
          ) : (
            <span className="text-muted-foreground">No backend result loaded</span>
          )}
        </dd>
      </dl>
      <div className="flex flex-wrap items-center gap-1">
        {s.modes.map((m) => (
          <Badge key={m} tone="muted" className="text-[10px]">
            {m}
          </Badge>
        ))}
        {info.result && !info.live ? (
          <span className="ml-auto text-[11px] text-muted-foreground">
            {info.batchId ? `${info.batchId} · ` : ""}
            {fmtDateTime(info.result.startedAt)}
          </span>
        ) : null}
      </div>
    </article>
  );
}

export const ScenarioCard = memo(ScenarioCardImpl);
