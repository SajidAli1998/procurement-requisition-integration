"use client";

import { useEffect, useRef, useState } from "react";

export type StreamStatus = "connecting" | "open" | "closed" | "error";

interface EventSourceOptions {
  // EventSource only delivers named events to listeners registered by name.
  events: string[];
  onEvent: (type: string, data: unknown) => void;
  enabled?: boolean;
  closeOn?: string;
}

export function useEventSource(url: string | null, opts: EventSourceOptions): StreamStatus {
  const [status, setStatus] = useState<StreamStatus>("connecting");
  const handler = useRef(opts.onEvent);
  useEffect(() => {
    handler.current = opts.onEvent;
  });
  const events = opts.events.join("|");
  const enabled = opts.enabled ?? true;
  const closeOn = opts.closeOn;

  useEffect(() => {
    if (!url || !enabled || typeof EventSource === "undefined") return;
    const source = new EventSource(url);
    source.onopen = () => setStatus("open");
    source.onerror = () => setStatus(source.readyState === EventSource.CLOSED ? "closed" : "error");
    const listeners = events.split("|").map((type) => {
      const listener = (e: MessageEvent) => {
        let data: unknown = e.data;
        try {
          data = JSON.parse(e.data);
        } catch {
        }
        handler.current(type, data);
        if (closeOn && type === closeOn) {
          source.close();
          setStatus("closed");
        }
      };
      source.addEventListener(type, listener as EventListener);
      return [type, listener] as const;
    });
    return () => {
      for (const [type, l] of listeners) source.removeEventListener(type, l as EventListener);
      source.close();
    };
  }, [url, enabled, events, closeOn]);

  return !url || !enabled ? "closed" : status;
}
