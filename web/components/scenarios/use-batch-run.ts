"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { api, labUrl, qk } from "@/lib/api";
import { useEventSource, type StreamStatus } from "@/hooks/use-event-source";
import { useLabAction } from "@/hooks/use-lab-action";
import type { BatchAccepted, LogEntry, RunScenariosRequest, RunnerEvent, ScenarioStatus, Tally } from "@/lib/types";

const BATCH_EVENTS = ["batch-started", "scenario-started", "log", "scenario-finished", "batch-finished", "saved", "error", "end"];

type LiveState = "Starting" | "Running" | "Finished" | "Cancelled" | "Failed";
export type LiveScenarioStatus = "Running" | ScenarioStatus;

interface LiveLog {
  scenarioId?: string;
  log: LogEntry;
}

export interface LiveBatch {
  id: string;
  name: string;
  total: number;
  state: LiveState;
  tally: Tally | null;
  statuses: Record<string, LiveScenarioStatus>;
  actual: Record<string, string>;
  logs: LiveLog[];
  savedId?: string;
  error?: string;
  startedAt: number;
}

const MAX_LOGS = 300;

function newLive(a: BatchAccepted, tally: Tally | null = null): LiveBatch {
  return { id: a.id, name: a.name, total: a.total, state: "Running", tally, statuses: {}, actual: {}, logs: [], startedAt: Date.now() };
}

function applyRunnerEvent(live: LiveBatch, type: string, data: unknown): LiveBatch {
  const e = (data && typeof data === "object" ? data : {}) as RunnerEvent;
  const next: LiveBatch = { ...live };
  if (e.tally) next.tally = e.tally;
  switch (type) {
    case "scenario-started":
      if (e.scenarioId) next.statuses = { ...live.statuses, [e.scenarioId]: "Running" };
      break;
    case "scenario-finished":
      if (e.scenarioId && e.status) {
        next.statuses = { ...live.statuses, [e.scenarioId]: e.status };
        if (e.text) next.actual = { ...live.actual, [e.scenarioId]: e.text };
      }
      break;
    case "log":
      if (e.log) next.logs = [...live.logs, { scenarioId: e.scenarioId, log: e.log }].slice(-MAX_LOGS);
      break;
    case "saved":
      if (e.text) next.savedId = e.text;
      break;
    case "error":
      next.error = e.text ?? "The batch failed";
      break;
    case "end": {
      const state = (data as { state?: string } | null)?.state;
      next.state = state === "Cancelled" ? "Cancelled" : state === "Failed" ? "Failed" : "Finished";
      break;
    }
  }
  return next;
}

export function useBatchRun(onSaved: (batchId: string) => void) {
  const qc = useQueryClient();
  const [live, setLive] = useState<LiveBatch | null>(null);
  const savedHandled = useRef<string | null>(null);
  const onSavedRef = useRef(onSaved);
  useEffect(() => {
    onSavedRef.current = onSaved;
  }, [onSaved]);

  const start = useLabAction<RunScenariosRequest, BatchAccepted>({
    name: "Start scenario batch",
    run: (req) => api.runBatch(req),
    invalidate: [qk.batches],
    confirm: (r) => `The lab accepted batch "${r.name}" (${r.total} scenario${r.total === 1 ? "" : "s"}); results appear as the backend reports them.`,
    onConfirmed: (r) => setLive(newLive(r)),
  });

  const cancel = useLabAction<string, void>({
    name: "Cancel batch",
    run: (id) => api.cancelBatch(id),
    invalidate: [qk.batches],
    confirm: () => "Cancellation requested; waiting for the runner to stop (scenarios already finished keep their results).",
  });

  const onEvent = useCallback((type: string, data: unknown) => {
    setLive((cur) => (cur ? applyRunnerEvent(cur, type, data) : cur));
  }, []);

  const streaming = !!live && live.state === "Running";
  const stream: StreamStatus = useEventSource(live ? labUrl(`/batches/live/${encodeURIComponent(live.id)}`) : null, {
    events: BATCH_EVENTS,
    onEvent,
    enabled: streaming,
    closeOn: "end",
  });

  useEffect(() => {
    if (live?.savedId && savedHandled.current !== live.savedId) {
      savedHandled.current = live.savedId;
      void qc.invalidateQueries({ queryKey: qk.batches });
      void qc.invalidateQueries({ queryKey: qk.scenariosLatest });
      onSavedRef.current(live.savedId);
    }
  }, [live?.savedId, qc]);

  const attach = useCallback((a: BatchAccepted, tally: Tally | null) => {
    setLive((cur) => (cur && cur.id === a.id ? cur : newLive(a, tally)));
  }, []);

  const dismiss = useCallback(() => setLive(null), []);

  return { live, stream, start, cancel, attach, dismiss, running: streaming || start.pending };
}
