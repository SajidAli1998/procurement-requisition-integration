"use client";

import Link from "next/link";
import { useQuery } from "@tanstack/react-query";
import { Activity, Database } from "lucide-react";
import { PageHeader } from "@/components/page-header";
import { QueryState } from "@/components/query-state";
import { StatCard } from "@/components/stat-card";
import { EmptyState } from "@/components/empty-state";
import { InvariantBadge, ToneBadge } from "@/components/status-badge";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Button } from "@/components/ui/button";
import { OutcomesChart, NameValueBar, RunsChart, ScenarioHistoryChart } from "@/components/dashboard/charts";
import { BaselineTable, baselineRows } from "@/components/dashboard/baseline-table";
import { WorkspaceSwitcher } from "@/components/dashboard/workspace-switcher";
import { api, qk } from "@/lib/api";
import { fmtMs, fmtNumber, fmtPct } from "@/lib/format";
import type { Dashboard } from "@/lib/types";
import type { Tone } from "@/lib/status";

function DashboardSkeleton() {
  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
        {Array.from({ length: 12 }).map((_, i) => (
          <Skeleton key={i} className="h-20" />
        ))}
      </div>
      <div className="grid gap-4 lg:grid-cols-2">
        <Skeleton className="h-64" />
        <Skeleton className="h-64" />
      </div>
    </div>
  );
}

function healthTone(status: string): Tone {
  if (status.startsWith("Healthy with")) return "warning";
  if (status === "Healthy") return "success";
  return "danger";
}

function Totals({ d }: { d: Dashboard }) {
  const t = d.totals;
  const cards: { key: string; label: string; value: number; tone: Tone; hint?: string }[] = [
    { key: "total", label: "Total in source", value: t.total, tone: "neutral" },
    { key: "approved", label: "Approved", value: t.approved, tone: "neutral" },
    { key: "created", label: "Created in SAP", value: t.created, tone: "success", hint: "synchronised" },
    { key: "linked", label: "Linked (existing SAP doc)", value: t.linked, tone: "info", hint: "synchronised, not created" },
    { key: "skipped", label: "Skipped (not approved)", value: t.skipped, tone: "muted", hint: "not synchronised" },
    { key: "quarantined", label: "Quarantined", value: t.quarantined, tone: "danger", hint: "not synchronised" },
    { key: "pending", label: "Pending", value: t.pending, tone: "warning", hint: "not yet confirmed" },
    { key: "retrying", label: "Retrying", value: t.retrying, tone: "warning" },
    { key: "unresolved", label: "Unresolved", value: t.unresolved, tone: "critical", hint: "outcome unknown" },
    { key: "needsIntervention", label: "Needs intervention", value: t.needsIntervention, tone: t.needsIntervention > 0 ? "critical" : "neutral" },
    { key: "duplicateReferencesInSap", label: "Duplicate references in SAP", value: t.duplicateReferencesInSap, tone: t.duplicateReferencesInSap > 0 ? "danger" : "success" },
    { key: "sapDocuments", label: "SAP documents", value: t.sapDocuments, tone: "neutral" },
  ];
  return (
    <section aria-labelledby="totals-h" className="space-y-2">
      <h2 id="totals-h" className="sr-only">
        Totals
      </h2>
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
        {cards.map((c) => (
          <StatCard key={c.key} testId={`stat-${c.key}`} label={c.label} value={fmtNumber(c.value)} tone={c.tone} hint={c.hint} />
        ))}
      </div>
    </section>
  );
}

function Health({ d }: { d: Dashboard }) {
  const h = d.health;
  return (
    <Card>
      <CardHeader>
        <CardTitle>Health</CardTitle>
        <CardDescription>Based on the lab&apos;s recorded traffic and finished runs.</CardDescription>
      </CardHeader>
      <CardContent>
        <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm sm:grid-cols-4" data-testid="health">
          <div className="col-span-2 sm:col-span-4">
            <dt className="text-xs text-muted-foreground">Status</dt>
            <dd>
              <ToneBadge tone={healthTone(h.status)}>{h.status}</ToneBadge>
            </dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">Circuit breaker</dt>
            <dd className="font-medium">{h.breaker}</dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">Breaker opened</dt>
            <dd className="font-medium tabular-nums">{fmtNumber(h.breakerOpenedCount)}×</dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">Average run</dt>
            <dd className="font-medium tabular-nums">{fmtMs(h.averageRunMs)}</dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">SAP success rate</dt>
            <dd className="font-medium tabular-nums">{h.sapSuccessRate === null ? "no SAP calls yet" : fmtPct(h.sapSuccessRate)}</dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">SAP calls</dt>
            <dd className="font-medium tabular-nums">{fmtNumber(h.sapCalls)}</dd>
          </div>
          <div>
            <dt className="text-xs text-muted-foreground">Retries</dt>
            <dd className="font-medium tabular-nums">{fmtNumber(h.retries)}</dd>
          </div>
          <div className="col-span-2">
            <dt className="text-xs text-muted-foreground">SAP latency p50 / p95 / p99</dt>
            <dd className="font-medium tabular-nums">
              {fmtMs(h.latencyMs.p50)} / {fmtMs(h.latencyMs.p95)} / {fmtMs(h.latencyMs.p99)}
            </dd>
          </div>
        </dl>
      </CardContent>
    </Card>
  );
}

function Invariants({ d }: { d: Dashboard }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Reconciliation invariants</CardTitle>
        <CardDescription>From the latest finished run&apos;s reconciliation report.</CardDescription>
      </CardHeader>
      <CardContent>
        {!d.reconciliation || d.reconciliation.length === 0 ? (
          <p className="text-sm text-muted-foreground">No reconciliation report yet. Run a sync first.</p>
        ) : (
          <ul className="space-y-1.5 text-sm" data-testid="invariants">
            {d.reconciliation.map((i) => (
              <li key={i.name} className="flex items-start gap-2">
                <InvariantBadge outcome={i.outcome} />
                <div className="min-w-0">
                  <div className="font-medium">{i.name}</div>
                  <div className="text-xs text-muted-foreground break-anywhere">{i.detail}</div>
                </div>
              </li>
            ))}
          </ul>
        )}
      </CardContent>
    </Card>
  );
}

function DashboardBody({ d }: { d: Dashboard }) {
  const reconciliation = useQuery({ queryKey: qk.reconciliation, queryFn: api.reconciliation, enabled: d.dataset === "Workbook" && d.totals.total > 0 });
  if (d.totals.total === 0) {
    return (
      <EmptyState
        icon={Database}
        title="The lab workspace is empty"
        description="There are no requisitions in the lab yet. Load the client's workbook or generate some test data, then run a sync."
        action={
          <div className="flex flex-wrap justify-center gap-2">
            <WorkspaceSwitcher current={d.dataset} />
            <Button asChild variant="outline" size="sm">
              <Link href="/generator">Open the data generator</Link>
            </Button>
          </div>
        }
      />
    );
  }
  return (
    <div className="space-y-4">
      <Totals d={d} />
      <div className="grid gap-4 lg:grid-cols-2">
        <Health d={d} />
        <Invariants d={d} />
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Baseline fixture vs actual</CardTitle>
          <CardDescription>
            &quot;Expected (client workbook)&quot; is a fixed reference from the client&apos;s spreadsheet. &quot;Actual (lab)&quot; is what this lab measured on the {d.dataset} dataset.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <BaselineTable rows={baselineRows(d.baseline, d.totals, reconciliation.data, d.dataset)} source={d.baseline.source} />
        </CardContent>
      </Card>
      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Outcomes</CardTitle>
          </CardHeader>
          <CardContent>
            <OutcomesChart data={d.outcomes} />
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Open quarantine by error type</CardTitle>
          </CardHeader>
          <CardContent>
            <NameValueBar data={d.errorTypes} caption="Error types" empty="Nothing in quarantine" colourByIndex />
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>SAP responses</CardTitle>
            <CardDescription>HTTP status of every call the integration made to SAP.</CardDescription>
          </CardHeader>
          <CardContent>
            <NameValueBar data={d.sapResponses} caption="SAP responses" empty="No SAP calls yet" />
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Throughput and duration per run</CardTitle>
          </CardHeader>
          <CardContent>
            <RunsChart runs={d.runs} />
          </CardContent>
        </Card>
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Scenario run history</CardTitle>
          <CardDescription>Pass/fail per saved scenario batch, oldest first.</CardDescription>
        </CardHeader>
        <CardContent>
          <ScenarioHistoryChart history={d.scenarioHistory} />
        </CardContent>
      </Card>
    </div>
  );
}

export default function DashboardPage() {
  const dashboard = useQuery({ queryKey: qk.dashboard, queryFn: api.dashboard, refetchInterval: 10_000 });
  return (
    <div>
      <PageHeader
        title="Dashboard"
        description="What the integration did with every requisition, how SAP answered, and whether the numbers add up."
        actions={
          <>
            <Button asChild size="sm">
              <Link href="/try">
                <Activity /> Try it yourself
              </Link>
            </Button>
            <WorkspaceSwitcher current={dashboard.data?.dataset} />
          </>
        }
      />
      <QueryState query={dashboard} skeleton={<DashboardSkeleton />} title="Could not load the dashboard">
        {(d) => <DashboardBody d={d} />}
      </QueryState>
    </div>
  );
}
