"use client";

import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { ToneBadge } from "@/components/status-badge";
import { DEFAULT_FAULT_SETTINGS, faultsActive } from "@/lib/faults";
import { fmtDateTime } from "@/lib/format";
import type { FaultSettings, FaultState, ScriptedFault } from "@/lib/types";

export type Target = "sap" | "source";

export const TARGET_LABEL: Record<Target, string> = { sap: "SAP", source: "Source" };

const SETTING_LABELS: Record<keyof FaultSettings, string> = {
  transientErrorRate: "Transient 503 rate",
  throttleRate: "429 throttle rate",
  throttleRetryAfterSeconds: "Retry-After (s)",
  lostResponseRate: "Lost response rate",
  latencyMs: "Latency (ms)",
  pageOverlap: "Page overlap",
  honorIdempotencyKey: "Honour Idempotency-Key",
  referenceSearchLag: "Reference search lag",
  seed: "Seed",
};

function settingText(key: keyof FaultSettings, value: FaultSettings[keyof FaultSettings]): string {
  if (typeof value === "boolean") return value ? "on" : "off";
  if (key.endsWith("Rate")) return `${Math.round(Number(value) * 1000) / 10}%`;
  return String(value);
}

function scriptSummary(f: ScriptedFault): string {
  const parts: string[] = [];
  if (f.kind === "Error" || f.kind === "Respond" || f.kind === "CommitThenRespond") parts.push(`status ${f.statusCode ?? 503}`);
  if (f.kind === "Throttle") parts.push(f.omitRetryAfter ? "no Retry-After" : `Retry-After ${f.retryAfterSeconds ?? 1}s${f.retryAfterAsDate ? " (as date)" : ""}`);
  if (f.kind === "Delay") parts.push(`${f.delayMs ?? 0} ms`);
  if (f.kind === "CommitThenTransform") parts.push(f.transform === "SetField" ? `set ${f.field ?? "?"} = ${f.value ?? "(removed)"}` : "truncate body");
  if (f.body) parts.push(`body ${f.body.length > 40 ? `${f.body.slice(0, 40)}...` : f.body}`);
  return parts.join(", ");
}

function TargetState({ target, state }: { target: Target; state: FaultState | null }) {
  if (!state) return <p className="text-sm text-muted-foreground">No fault state reported for {TARGET_LABEL[target]}.</p>;
  const changed = (Object.keys(SETTING_LABELS) as (keyof FaultSettings)[]).filter(
    (k) => k !== "seed" && state.settings[k] !== DEFAULT_FAULT_SETTINGS[k] && !(target === "sap" && k === "pageOverlap") && !(target === "source" && (k === "honorIdempotencyKey" || k === "referenceSearchLag")),
  );
  const injected = Object.entries(state.injected);
  return (
    <div className="space-y-3" data-testid={`fault-state-${target}`}>
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-sm font-semibold">{TARGET_LABEL[target]} mock</h3>
        {faultsActive(state) ? <ToneBadge tone="warning">Faults active</ToneBadge> : <ToneBadge tone="success">No faults</ToneBadge>}
      </div>
      <div>
        <div className="mb-1 text-xs font-medium text-muted-foreground">Random settings (seed {state.settings.seed})</div>
        {changed.length === 0 ? (
          <p className="text-xs text-muted-foreground">All at defaults.</p>
        ) : (
          <ul className="flex flex-wrap gap-1.5">
            {changed.map((k) => (
              <li key={k}>
                <ToneBadge tone="warning">
                  {SETTING_LABELS[k]}: {settingText(k, state.settings[k])}
                </ToneBadge>
              </li>
            ))}
          </ul>
        )}
      </div>
      <div>
        <div className="mb-1 text-xs font-medium text-muted-foreground">Pending scripted faults ({state.pendingScript.length})</div>
        {state.pendingScript.length === 0 ? (
          <p className="text-xs text-muted-foreground">None queued.</p>
        ) : (
          <div className="overflow-x-auto rounded-md border">
            <table className="w-full text-xs">
              <thead className="bg-muted/40 text-left text-muted-foreground">
                <tr>
                  <th className="px-2 py-1 font-semibold">Kind</th>
                  <th className="px-2 py-1 font-semibold">Match</th>
                  <th className="px-2 py-1 font-semibold">Count</th>
                  <th className="px-2 py-1 font-semibold">Skip</th>
                  <th className="px-2 py-1 font-semibold">Detail</th>
                </tr>
              </thead>
              <tbody>
                {state.pendingScript.map((f, i) => (
                  <tr key={i} className="border-t align-top">
                    <td className="px-2 py-1 font-medium">{f.kind}</td>
                    <td className="px-2 py-1 font-mono break-anywhere">
                      {f.method ?? "any"} {f.pathContains ?? "*"}
                      {f.bodyContains ? ` body∋${f.bodyContains}` : ""}
                    </td>
                    <td className="px-2 py-1 tabular-nums">{f.count ?? 1}</td>
                    <td className="px-2 py-1 tabular-nums">{f.skip ?? 0}</td>
                    <td className="px-2 py-1 break-anywhere">{scriptSummary(f)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      <div>
        <div className="mb-1 text-xs font-medium text-muted-foreground">Injected so far</div>
        {injected.length === 0 ? (
          <p className="text-xs text-muted-foreground">Nothing injected yet.</p>
        ) : (
          <ul className="flex flex-wrap gap-1.5">
            {injected.map(([k, v]) => (
              <li key={k}>
                <ToneBadge tone="neutral">
                  {k}: <span className="tabular-nums">{v}</span>
                </ToneBadge>
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

export function FaultStatePanel({
  sap,
  source,
  crashArmed,
  crashFiredAt,
  dbFaultsArmed,
  dbFaultsFired,
}: {
  sap: FaultState | null;
  source: FaultState | null;
  crashArmed: string | null;
  crashFiredAt: string | null;
  dbFaultsArmed: string[];
  dbFaultsFired: string[];
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Current fault state</CardTitle>
        <CardDescription>Read back from the lab after every change (GET /api/workspace). Nothing here is assumed from local state.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="grid gap-4 md:grid-cols-2">
          <TargetState target="sap" state={sap} />
          <TargetState target="source" state={source} />
        </div>
        <div className="space-y-1 border-t pt-3 text-xs">
          <div className="font-medium text-muted-foreground">Crash and database faults</div>
          <dl className="grid gap-2 sm:grid-cols-2" data-testid="crash-db-state">
            <div>
              <dt className="text-muted-foreground">Crash point</dt>
              <dd>
                {crashArmed ? <ToneBadge tone="warning">Armed: {crashArmed}</ToneBadge> : <span>None armed</span>}
                {crashFiredAt ? (
                  <span className="ml-2">
                    <ToneBadge tone="critical">Crash fired</ToneBadge> at {fmtDateTime(crashFiredAt)}
                  </span>
                ) : null}
              </dd>
            </div>
            <div>
              <dt className="text-muted-foreground">Database faults</dt>
              <dd>
                {dbFaultsArmed.length === 0 ? (
                  <span>None armed</span>
                ) : (
                  <ul className="list-inside list-disc">
                    {dbFaultsArmed.map((f, i) => (
                      <li key={i} className="break-anywhere">
                        <ToneBadge tone="warning">Armed</ToneBadge> {f}
                      </li>
                    ))}
                  </ul>
                )}
                {dbFaultsFired.length > 0 ? (
                  <ul className="mt-1 list-inside list-disc">
                    {dbFaultsFired.map((f, i) => (
                      <li key={i} className="break-anywhere">
                        <ToneBadge tone="critical">Fired</ToneBadge> {f}
                      </li>
                    ))}
                  </ul>
                ) : null}
              </dd>
            </div>
          </dl>
        </div>
      </CardContent>
    </Card>
  );
}
