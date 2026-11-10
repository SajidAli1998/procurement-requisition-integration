"use client";

import { AlertTriangle, CheckCircle2, ChevronRight, XCircle } from "lucide-react";
import { useState } from "react";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { InvariantBadge, ScenarioStatusBadge } from "@/components/status-badge";
import { HttpExchangeList } from "@/components/http-exchange-list";
import { JsonDiff, JsonViewer } from "@/components/monaco";
import { fmtDateTime, fmtMs, fmtTime } from "@/lib/format";
import type { ScenarioDefinition, ScenarioResult, StateSnapshot } from "@/lib/types";
import { cn } from "@/lib/utils";

function Section({ title, children, count, testId }: { title: string; children: React.ReactNode; count?: number; testId?: string }) {
  return (
    <section className="flex flex-col gap-2" data-testid={testId}>
      <h3 className="text-sm font-semibold">
        {title}
        {count !== undefined ? <span className="ml-1.5 text-xs font-normal text-muted-foreground">({count})</span> : null}
      </h3>
      {children}
    </section>
  );
}

function Expandable({ title, children, defaultOpen = false }: { title: string; children: React.ReactNode; defaultOpen?: boolean }) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <Collapsible open={open} onOpenChange={setOpen} className="rounded-md border">
      <CollapsibleTrigger className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm font-semibold hover:bg-muted/40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring">
        <ChevronRight className={cn("size-4 transition-transform", open && "rotate-90")} aria-hidden />
        {title}
      </CollapsibleTrigger>
      <CollapsibleContent className="border-t p-3">{open ? children : null}</CollapsibleContent>
    </Collapsible>
  );
}

function Definition({ s }: { s: ScenarioDefinition }) {
  const rows: [string, React.ReactNode][] = [
    ["Description", s.description],
    ["Preconditions", s.preconditions],
    ["Fixture", s.fixture],
    ["Faults", s.faults],
    ["Modes", s.modes.join(", ")],
    ["Expected HTTP", s.expected?.http],
    ["Expected source", s.expected?.source],
    ["Expected SAP", s.expected?.sap],
    ["Expected database", s.expected?.database],
    ["Expected reconciliation", s.expected?.reconciliation],
    ["Covered by", s.coveredBy],
    ["Timeout", s.timeoutSeconds ? `${s.timeoutSeconds}s` : undefined],
  ];
  return (
    <div className="flex flex-col gap-2 text-sm">
      <dl className="grid gap-x-3 gap-y-1 sm:grid-cols-[10rem_1fr]">
        {rows
          .filter(([, v]) => v !== undefined && v !== null && v !== "")
          .map(([k, v]) => (
            <div key={k} className="contents">
              <dt className="text-xs font-medium text-muted-foreground">{k}</dt>
              <dd className="break-anywhere">{v}</dd>
            </div>
          ))}
      </dl>
      {s.steps.length > 0 ? (
        <div>
          <div className="text-xs font-medium text-muted-foreground">Steps</div>
          <ol className="ml-5 list-decimal text-sm">
            {s.steps.map((st, i) => (
              <li key={i} className="break-anywhere">
                {st}
              </li>
            ))}
          </ol>
        </div>
      ) : null}
    </div>
  );
}

const SNAPSHOT_PARTS: (keyof StateSnapshot)[] = ["source", "sap", "database"];

function Snapshots({ before, after }: { before?: StateSnapshot; after?: StateSnapshot }) {
  if (!before && !after) return <p className="text-sm text-muted-foreground">No snapshots were recorded for this result.</p>;
  return (
    <Tabs defaultValue="source">
      <TabsList aria-label="Snapshot">
        {SNAPSHOT_PARTS.map((p) => (
          <TabsTrigger key={p} value={p}>
            {p === "sap" ? "SAP" : p.charAt(0).toUpperCase() + p.slice(1)}
          </TabsTrigger>
        ))}
      </TabsList>
      {SNAPSHOT_PARTS.map((p) => (
        <TabsContent key={p} value={p}>
          <JsonDiff original={before?.[p] ?? null} modified={after?.[p] ?? null} labels={["Before", "After"]} height={360} />
        </TabsContent>
      ))}
    </Tabs>
  );
}

export function ResultDrawer({
  scenario,
  result,
  batchId,
  invariants,
  open,
  onOpenChange,
  onRun,
  runDisabled,
}: {
  scenario: ScenarioDefinition | null;
  result?: ScenarioResult;
  batchId?: string;
  invariants: { id: string; name: string }[];
  open: boolean;
  onOpenChange: (v: boolean) => void;
  onRun: (id: string) => void;
  runDisabled: boolean;
}) {
  if (!scenario) return null;
  const s = scenario;
  const r = result;
  const reported = new Map((r?.invariants ?? []).map((i) => [i.id, i]));
  const failedAssertions = r?.assertions.filter((a) => !a.passed).length ?? 0;

  return (
    <Sheet open={open} onOpenChange={onOpenChange}>
      <SheetContent data-testid="scenario-drawer" aria-describedby="scenario-drawer-desc">
        <SheetHeader>
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-mono text-sm font-semibold">{s.id}</span>
            <ScenarioStatusBadge status={r?.status ?? (s.implemented ? (s.blockedReason ? "Blocked" : "NotRun") : "NotImplemented")} />
            <Badge tone="neutral">{s.priority}</Badge>
            <Badge tone="neutral">{s.severity}</Badge>
          </div>
          <SheetTitle className="break-anywhere">{s.title}</SheetTitle>
          <SheetDescription id="scenario-drawer-desc" className="break-anywhere">
            {s.category}
            {r ? ` · ${batchId ? `batch ${batchId} · ` : ""}started ${fmtDateTime(r.startedAt)} · ${fmtMs(r.durationMs)} · seed ${r.seed}` : " · no backend result loaded"}
          </SheetDescription>
          <div>
            <Button size="sm" variant="outline" onClick={() => onRun(s.id)} disabled={runDisabled} aria-label={`Run ${s.id} again`}>
              Run this scenario
            </Button>
          </div>
        </SheetHeader>

        {r?.error ? (
          <Alert variant="destructive">
            <XCircle />
            <AlertTitle>Error</AlertTitle>
            <AlertDescription>
              <pre className="whitespace-pre-wrap font-mono text-xs break-anywhere">{r.error}</pre>
            </AlertDescription>
          </Alert>
        ) : null}
        {r?.blockedReason || (!r && s.blockedReason) ? (
          <Alert variant="warning">
            <AlertTriangle />
            <AlertTitle>Blocked: not executed, never counted as passed</AlertTitle>
            <AlertDescription>{r?.blockedReason ?? s.blockedReason}</AlertDescription>
          </Alert>
        ) : null}

        <Section title="Expected vs actual">
          <dl className="grid gap-x-3 gap-y-1 text-sm sm:grid-cols-[6rem_1fr]">
            <dt className="text-xs font-medium text-muted-foreground">Expected</dt>
            <dd className="break-anywhere">{r?.expected || s.expected?.summary || "-"}</dd>
            <dt className="text-xs font-medium text-muted-foreground">Actual</dt>
            <dd className="break-anywhere">{r?.actual || <span className="text-muted-foreground">No backend result yet</span>}</dd>
          </dl>
        </Section>

        <Expandable title="Scenario definition" defaultOpen={!r}>
          <Definition s={s} />
        </Expandable>

        <Section title="Timeline" count={r?.log.length ?? 0}>
          {r && r.log.length > 0 ? (
            <ol className="max-h-72 space-y-0.5 overflow-y-auto rounded-md border bg-muted/20 p-2 text-xs">
              {r.log.map((l, i) => (
                <li key={i} className="flex gap-2">
                  <span className="shrink-0 tabular-nums text-muted-foreground">{fmtTime(l.at)}</span>
                  <Badge tone={l.kind === "Error" ? "danger" : l.kind === "Warn" ? "warning" : l.kind === "Run" ? "info" : l.kind === "Step" ? "accent" : "neutral"} className="shrink-0 py-0 text-[10px]">
                    {l.kind}
                  </Badge>
                  <span className="min-w-0 break-anywhere">{l.text}</span>
                  {l.simulated ? (
                    <Badge tone="muted" className="shrink-0 py-0 text-[10px]" title="This step stands for time passing or an event the lab only imitates">
                      simulated
                    </Badge>
                  ) : null}
                </li>
              ))}
            </ol>
          ) : (
            <p className="text-sm text-muted-foreground">No timeline recorded.</p>
          )}
        </Section>

        <Section title="Assertions" count={r?.assertions.length ?? 0} testId="drawer-assertions">
          {r && r.assertions.length > 0 ? (
            <>
              <p className="text-xs text-muted-foreground">
                {r.assertions.length - failedAssertions} passed, {failedAssertions} failed
              </p>
              <ul className="divide-y rounded-md border text-sm">
                {r.assertions.map((a, i) => (
                  <li key={i} className="flex gap-2 p-2" data-passed={a.passed}>
                    {a.passed ? (
                      <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-emerald-600" aria-label="passed" />
                    ) : (
                      <XCircle className="mt-0.5 size-4 shrink-0 text-red-600" aria-label="failed" />
                    )}
                    <div className="min-w-0 flex-1">
                      <div className="break-anywhere">
                        {a.name}
                        {a.invariant ? (
                          <Badge tone="muted" className="ml-1.5 py-0 text-[10px]">
                            {a.invariant}
                          </Badge>
                        ) : null}
                      </div>
                      <dl className="mt-0.5 grid grid-cols-[auto_1fr] gap-x-2 text-xs text-muted-foreground">
                        <dt>expected</dt>
                        <dd className="font-mono break-anywhere">{a.expected ?? "-"}</dd>
                        <dt>actual</dt>
                        <dd className={cn("font-mono break-anywhere", !a.passed && "text-red-700 dark:text-red-400")}>{a.actual ?? "-"}</dd>
                      </dl>
                    </div>
                  </li>
                ))}
              </ul>
            </>
          ) : (
            <p className="text-sm text-muted-foreground">No assertions recorded{r ? "" : " (not run)"}.</p>
          )}
        </Section>

        <Section title="Invariants" count={invariants.length} testId="drawer-invariants">
          <ul className="divide-y rounded-md border text-sm">
            {invariants.map((inv) => {
              const got = reported.get(inv.id);
              return (
                <li key={inv.id} className="flex flex-wrap items-start gap-2 p-2" data-invariant={inv.id}>
                  <span className="w-14 shrink-0 font-mono text-xs">{inv.id}</span>
                  <InvariantBadge outcome={got?.outcome ?? "NotApplicable"} />
                  <div className="min-w-0 flex-1">
                    <div>{inv.name}</div>
                    <div className="text-xs text-muted-foreground break-anywhere">{got?.detail ?? (r ? "Not checked by this scenario" : "Not run")}</div>
                  </div>
                </li>
              );
            })}
          </ul>
        </Section>

        <Section title="Runs" count={r?.runs.length ?? 0}>
          {r && r.runs.length > 0 ? (
            <div className="overflow-x-auto rounded-md border">
              <table className="w-full text-xs">
                <thead className="bg-muted/40 text-muted-foreground">
                  <tr>
                    <th scope="col" className="px-2 py-1 text-left">Run</th>
                    <th scope="col" className="px-2 py-1 text-left">Worker</th>
                    <th scope="col" className="px-2 py-1 text-left">Outcome</th>
                    <th scope="col" className="px-2 py-1 text-right">Duration</th>
                    <th scope="col" className="px-2 py-1 text-left">Events</th>
                    <th scope="col" className="px-2 py-1 text-right">Pending</th>
                    <th scope="col" className="px-2 py-1 text-right">Quarantine</th>
                  </tr>
                </thead>
                <tbody>
                  {r.runs.map((run, i) => (
                    <tr key={i} className="border-t align-top">
                      <td className="px-2 py-1 break-anywhere">{run.label}</td>
                      <td className="px-2 py-1">{run.worker}</td>
                      <td className="px-2 py-1">
                        {run.status ?? run.kind}
                        {run.message ? <div className="text-muted-foreground break-anywhere">{run.message}</div> : null}
                      </td>
                      <td className="px-2 py-1 text-right tabular-nums">{fmtMs(run.durationMs)}</td>
                      <td className="px-2 py-1 break-anywhere">
                        {Object.entries(run.events ?? {})
                          .map(([k, v]) => `${k} ${v}`)
                          .join(", ") || "-"}
                        {run.transport && Object.keys(run.transport).length > 0 ? (
                          <div className="text-muted-foreground">
                            {Object.entries(run.transport)
                              .map(([k, v]) => `${k} ${v}`)
                              .join(", ")}
                          </div>
                        ) : null}
                      </td>
                      <td className="px-2 py-1 text-right tabular-nums">{run.pending ?? "-"}</td>
                      <td className="px-2 py-1 text-right tabular-nums">{run.openQuarantine ?? "-"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="text-sm text-muted-foreground">No sync runs recorded.</p>
          )}
        </Section>

        <Expandable title={`HTTP exchanges (${r?.http.length ?? 0})`}>
          <HttpExchangeList exchanges={r?.http ?? []} limit={300} />
        </Expandable>

        <Section title="Evidence" testId="drawer-evidence">
          {r && Object.keys(r.evidence ?? {}).length > 0 ? (
            <JsonViewer value={r.evidence} label="Evidence JSON" maxHeight={420} />
          ) : (
            <p className="text-sm text-muted-foreground">No evidence recorded{r ? "" : " (not run)"}.</p>
          )}
        </Section>

        <Expandable title="Before / after snapshots (source, SAP, database)">
          <Snapshots before={r?.before} after={r?.after} />
        </Expandable>
      </SheetContent>
    </Sheet>
  );
}
