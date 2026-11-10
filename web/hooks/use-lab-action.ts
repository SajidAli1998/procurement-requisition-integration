"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useQueryClient, type QueryKey } from "@tanstack/react-query";
import { ApiError, WORKSPACE_KEY, isApiError } from "@/lib/api";

// Success is never taken from local state: "confirmed" only follows a re-read of the lab.
export type ActionPhase = "idle" | "sending" | "confirming" | "confirmed" | "unknown" | "failed";

interface ActionState<R> {
  phase: ActionPhase;
  message?: string;
  error?: ApiError | Error;
  result?: R;
  at?: number;
}

interface LabActionOptions<V, R> {
  name: string;
  run: (vars: V) => Promise<R>;
  invalidate?: QueryKey[];
  confirm?: (result: R, vars: V) => string | Promise<string>;
  onConfirmed?: (result: R, vars: V) => void;
}

export function useLabAction<V = void, R = unknown>(opts: LabActionOptions<V, R>) {
  const qc = useQueryClient();
  const [state, setState] = useState<ActionState<R>>({ phase: "idle" });
  const busy = useRef(false);
  const optsRef = useRef(opts);
  useEffect(() => {
    optsRef.current = opts;
  });

  const reread = useCallback(async () => {
    const keys = optsRef.current.invalidate ?? [WORKSPACE_KEY];
    // Bounded: a slow or failing re-read must not leave the action stuck (and further clicks ignored).
    await Promise.race([Promise.allSettled(keys.map((queryKey) => qc.invalidateQueries({ queryKey }))), new Promise((r) => setTimeout(r, 10_000))]);
  }, [qc]);

  const execute = useCallback(
    async (vars: V): Promise<R | undefined> => {
      // A ref, not state: two clicks in the same tick must not both get through.
      if (busy.current) return undefined;
      busy.current = true;
      const o = optsRef.current;
      setState({ phase: "sending", message: `${o.name}: sending to the lab...`, at: Date.now() });
      try {
        const result = await o.run(vars);
        setState({ phase: "confirming", message: `${o.name}: the lab accepted it. Checking the result...`, result, at: Date.now() });
        await reread();
        const message = o.confirm ? await o.confirm(result, vars) : `${o.name}: confirmed by the lab`;
        setState({ phase: "confirmed", message, result, at: Date.now() });
        o.onConfirmed?.(result, vars);
        return result;
      } catch (e) {
        const err = e instanceof Error ? e : new Error(String(e));
        if (isApiError(err) && err.outcomeUnknown) {
          setState({
            phase: "unknown",
            error: err,
            message: `${o.name}: no confirmation from the lab (${err.message}). It may or may not have been applied; the current state has been re-read. Check it before retrying.`,
            at: Date.now(),
          });
          void reread();
        } else {
          setState({ phase: "failed", error: err, message: `${o.name} failed: ${err.message}`, at: Date.now() });
          void reread();
        }
        return undefined;
      } finally {
        busy.current = false;
      }
    },
    [reread],
  );

  const reset = useCallback(() => setState({ phase: "idle" }), []);
  const pending = state.phase === "sending" || state.phase === "confirming";
  return { ...state, execute, reset, pending };
}
