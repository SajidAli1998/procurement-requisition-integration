"use client";

import { createContext, useCallback, useContext, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { WORKSPACE_KEY, labUrl, qk } from "@/lib/api";
import type { HttpExchange, LiveEvent, WorkspaceRun } from "@/lib/types";
import { useEventSource, type StreamStatus } from "@/hooks/use-event-source";

const LIVE_EVENT_TYPES = ["hello", "http", "run-started", "run-finished", "schedule", "restart", "workspace-reset"];
const MAX_EVENTS = 1500;

type Listener = (e: LiveEvent) => void;

class LiveStore {
  private buffer: LiveEvent[] = [];
  // Replaced on every notify so memoised consumers recompute.
  events: LiveEvent[] = [];
  version = 0;
  activeRun: WorkspaceRun | null = null;
  lastRun: WorkspaceRun | null = null;
  private listeners = new Set<() => void>();
  private eventListeners = new Set<Listener>();
  private pendingNotify = false;

  push(e: LiveEvent) {
    this.buffer.push(e);
    if (this.buffer.length > MAX_EVENTS) this.buffer.splice(0, this.buffer.length - MAX_EVENTS);
    if (e.type === "run-started") this.activeRun = e.data as WorkspaceRun;
    if (e.type === "run-finished") {
      this.lastRun = e.data as WorkspaceRun;
      if (!this.activeRun || this.activeRun.id === this.lastRun?.id) this.activeRun = null;
    }
    if (e.type === "workspace-reset" || e.type === "restart") this.activeRun = null;
    for (const l of this.eventListeners) l(e);
    this.scheduleNotify();
  }

  // Batches re-renders: an active run can emit hundreds of HTTP events per second.
  private scheduleNotify() {
    if (this.pendingNotify) return;
    this.pendingNotify = true;
    setTimeout(() => {
      this.pendingNotify = false;
      this.events = this.buffer.slice();
      this.version++;
      for (const l of this.listeners) l();
    }, 150);
  }

  clear() {
    this.buffer = [];
    this.events = [];
    this.version++;
    for (const l of this.listeners) l();
  }

  subscribe = (l: () => void) => {
    this.listeners.add(l);
    return () => this.listeners.delete(l);
  };

  onEvent(l: Listener) {
    this.eventListeners.add(l);
    return () => this.eventListeners.delete(l);
  }

  getVersion = () => this.version;
}

interface LiveContextValue {
  status: StreamStatus;
  store: LiveStore;
}

const LiveContext = createContext<LiveContextValue | null>(null);

export function LiveProvider({ children }: { children: React.ReactNode }) {
  const qc = useQueryClient();
  const [store] = useState(() => new LiveStore());
  const invalidateTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const invalidateSoon = useCallback(() => {
    if (invalidateTimer.current) return;
    invalidateTimer.current = setTimeout(() => {
      invalidateTimer.current = null;
      void qc.invalidateQueries({ queryKey: WORKSPACE_KEY });
      void qc.invalidateQueries({ queryKey: qk.batches });
    }, 300);
  }, [qc]);

  const onEvent = useCallback(
    (type: string, data: unknown) => {
      const e: LiveEvent = data && typeof data === "object" && "type" in (data as object) ? (data as LiveEvent) : { type, at: new Date().toISOString(), data };
      store.push({ ...e, type });
      if (type !== "http" && type !== "hello") invalidateSoon();
    },
    [store, invalidateSoon],
  );

  const status = useEventSource(labUrl("/workspace/live"), { events: LIVE_EVENT_TYPES, onEvent });
  const value = useMemo(() => ({ status, store }), [status, store]);
  return <LiveContext.Provider value={value}>{children}</LiveContext.Provider>;
}

function useLiveContext(): LiveContextValue {
  const ctx = useContext(LiveContext);
  if (!ctx) throw new Error("useLive must be used inside LiveProvider");
  return ctx;
}

export function useLive() {
  const { status, store } = useLiveContext();
  useSyncExternalStore(store.subscribe, store.getVersion, () => 0);
  return {
    status,
    events: store.events,
    activeRun: store.activeRun,
    lastRun: store.lastRun,
    clear: () => store.clear(),
    onEvent: (l: Listener) => store.onEvent(l),
  };
}

export function useLiveStatus(): StreamStatus {
  return useLiveContext().status;
}

export function httpEvents(events: LiveEvent[]): HttpExchange[] {
  const out: HttpExchange[] = [];
  for (const e of events) if (e.type === "http" && e.data) out.push(e.data as HttpExchange);
  return out;
}
