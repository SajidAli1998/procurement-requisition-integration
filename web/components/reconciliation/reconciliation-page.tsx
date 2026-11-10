"use client";

import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { Download } from "lucide-react";
import { ActionStatus } from "@/components/action-status";
import { DataTable } from "@/components/data-table";
import { PageHeader } from "@/components/page-header";
import { QueryState } from "@/components/query-state";
import { StatCard } from "@/components/stat-card";
import { InvariantBadge, OutcomeBadge, ToneBadge } from "@/components/status-badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import { useLabAction } from "@/hooks/use-lab-action";
import { api, qk, saveResponse } from "@/lib/api";
import { fmtAmount, fmtDateTime } from "@/lib/format";
import type { ComparisonRow, ComparisonSide, ReconciliationView, RequisitionRow } from "@/lib/types";
import { cn } from "@/lib/utils";
import { explain, type Explanation } from "./explanations";

const FIELDS: { key: keyof ComparisonSide; label: string; diff: string }[] = [
  { key: "result", label: "Result", diff: "result" },
  { key: "document", label: "SAP document", diff: "document" },
  { key: "attempts", label: "Attempts", diff: "attempts" },
  { key: "category", label: "Failure category", diff: "category" },
  { key: "version", label: "Version", diff: "version" },
];

function Pair({ row, f }: { row: ComparisonRow; f: (typeof FIELDS)[number] }) {
  const differs = row.differences.includes(f.diff) || row.actual === null;
  const e = row.expected[f.key];
  const a = row.actual?.[f.key];
  return (
    <div className={cn("rounded px-1", differs && "bg-red-100 dark:bg-red-950/50")} data-differs={differs}>
      <div className="text-muted-foreground">{e === null || e === undefined || e === "" ? "-" : String(e)}</div>
      <div className="font-medium">{row.actual === null ? <span className="text-red-700 dark:text-red-300">no outcome</span> : a === null || a === undefined || a === "" ? "-" : String(a)}</div>
    </div>
  );
}

const comparisonColumns: ColumnDef<ComparisonRow, unknown>[] = [
  { id: "id", accessorKey: "id", header: "Requisition", meta: { label: "Requisition" }, cell: ({ row }) => <span className="font-mono text-xs">{row.original.id}</span> },
  {
    id: "matches",
    accessorFn: (r) => (r.matches ? "Match" : "Mismatch"),
    header: "Match",
    meta: { label: "Match" },
    cell: ({ row }) => (row.original.matches ? <ToneBadge tone="success">Match</ToneBadge> : <ToneBadge tone="danger">Mismatch: {row.original.differences.join(", ")}</ToneBadge>),
  },
  ...FIELDS.map<ColumnDef<ComparisonRow, unknown>>((f) => ({
    id: f.key,
    accessorFn: (r) => `${r.expected[f.key] ?? ""} ${r.actual?.[f.key] ?? ""}`,
    header: () => (
      <span>
        {f.label}
        <span className="block text-[10px] font-normal">expected / actual</span>
      </span>
    ),
    meta: { label: f.label },
    cell: ({ row }) => <Pair row={row.original} f={f} />,
  })),
  {
    id: "explanation",
    accessorFn: (r) => `${r.expected.explanation ?? ""} ${r.actual?.explanation ?? ""}`,
    header: "Explanation (expected / actual)",
    meta: { label: "Explanation" },
    cell: ({ row }) => (
      <div className="max-w-80 text-xs">
        <div className="text-muted-foreground break-anywhere">{row.original.expected.explanation}</div>
        <div className="break-anywhere">{row.original.actual?.explanation ?? "-"}</div>
      </div>
    ),
  },
];

const explanationColumns: ColumnDef<Explanation, unknown>[] = [
  { id: "id", accessorKey: "id", header: "Requisition", meta: { label: "Requisition" }, cell: ({ row }) => <span className="font-mono text-xs">{row.original.id}</span> },
  { id: "outcome", accessorKey: "outcome", header: "Outcome", meta: { label: "Outcome" }, cell: ({ row }) => <OutcomeBadge outcome={row.original.outcome} /> },
  { id: "sapNumber", accessorKey: "sapNumber", header: "SAP number", meta: { label: "SAP number" }, cell: ({ row }) => <span className="font-mono text-xs">{row.original.sapNumber ?? "-"}</span> },
  { id: "reconciliation", accessorKey: "reconciliation", header: "Reconciliation", meta: { label: "Reconciliation" } },
  { id: "explanation", accessorKey: "explanation", header: "Explanation", meta: { label: "Explanation" }, cell: ({ row }) => <span className="break-anywhere">{row.original.explanation}</span> },
];

function Comparison({ c }: { c: NonNullable<ReconciliationView["comparison"]> }) {
  const [mismatchOnly, setMismatchOnly] = useState(false);
  const rows = mismatchOnly ? c.rows.filter((r) => !r.matches) : c.rows;
  return (
    <Card>
      <CardHeader>
        <CardTitle>Expected (client workbook) vs actual (lab)</CardTitle>
        <CardDescription>
          Row by row against the workbook&apos;s Reconciliation_Expected sheet. Expected values are a fixture; actual values are this lab&apos;s latest outcome per requisition.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div className="mb-2 flex flex-wrap items-center gap-3">
          <ToneBadge tone={c.matched === c.total ? "success" : "danger"}>
            <span data-testid="comparison-count">
              {c.matched}/{c.total} rows match
            </span>
          </ToneBadge>
          <div className="flex items-center gap-2">
            <Switch id="mismatch-only" checked={mismatchOnly} onCheckedChange={setMismatchOnly} />
            <Label htmlFor="mismatch-only">Mismatches only</Label>
          </div>
        </div>
        <DataTable testId="comparison" data={rows} columns={comparisonColumns} getRowId={(r) => r.id} emptyMessage={mismatchOnly ? "No mismatches." : "No rows."} />
      </CardContent>
    </Card>
  );
}

function Body({ v, rows }: { v: ReconciliationView; rows: RequisitionRow[] }) {
  const explanations = useMemo(() => explain(rows, v.outcomes), [rows, v.outcomes]);
  const failing = v.invariants.filter((i) => i.outcome === "Fail").length;
  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        {Object.entries(v.stateTotals).map(([k, n]) => (
          <StatCard key={k} label={`State: ${k}`} value={n} tone={k === "Synced" ? "success" : k === "Quarantined" ? "danger" : k === "Ignored" ? "muted" : "warning"} />
        ))}
        <StatCard label="Invariants failing" value={failing} tone={failing ? "danger" : "success"} />
      </div>
      <div className="grid gap-4 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Invariants</CardTitle>
            <CardDescription>Generated {fmtDateTime(v.generatedAt)}</CardDescription>
          </CardHeader>
          <CardContent>
            <ul className="space-y-1.5 text-sm" data-testid="reconciliation-invariants">
              {v.invariants.map((i) => (
                <li key={i.name} className="flex items-start gap-2">
                  <InvariantBadge outcome={i.outcome} />
                  <span>
                    <span className="font-medium">{i.name}</span>
                    <span className="block text-xs text-muted-foreground break-anywhere">{i.detail}</span>
                  </span>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Currency totals (source vs SAP)</CardTitle>
          </CardHeader>
          <CardContent>
            {v.totals.length === 0 ? (
              <p className="text-sm text-muted-foreground">Nothing synchronised yet.</p>
            ) : (
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead className="text-xs text-muted-foreground">
                    <tr>
                      <th className="py-1 text-left">Currency</th>
                      <th className="py-1 text-right">Source (count / amount)</th>
                      <th className="py-1 text-right">SAP (count / amount)</th>
                      <th className="py-1 text-center">Match</th>
                    </tr>
                  </thead>
                  <tbody>
                    {v.totals.map((t) => (
                      <tr key={t.currency} className="border-t">
                        <td className="py-1">{t.currency}</td>
                        <td className="py-1 text-right tabular-nums">
                          {t.localCount} / {fmtAmount(t.localAmount, t.currency)}
                        </td>
                        <td className="py-1 text-right tabular-nums">
                          {t.sapCount} / {fmtAmount(t.sapAmount, t.currency)}
                        </td>
                        <td className="py-1 text-center">{t.matches ? <ToneBadge tone="success">Yes</ToneBadge> : <ToneBadge tone="danger">No</ToneBadge>}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </CardContent>
        </Card>
      </div>
      {v.comparison ? <Comparison c={v.comparison} /> : null}
      <Card>
        <CardHeader>
          <CardTitle>Open failures ({v.openFailures.length})</CardTitle>
        </CardHeader>
        <CardContent>
          {v.openFailures.length === 0 ? (
            <p className="text-sm text-muted-foreground">No open failures.</p>
          ) : (
            <ul className="divide-y text-sm">
              {v.openFailures.map((f) => (
                <li key={`${f.key}-${f.version}`} className="py-1.5">
                  <span className="font-mono text-xs">{f.key}</span> v{f.version} · <span className="font-medium">{f.reason}</span> ({f.stage}, {f.occurrences}×): <span className="break-anywhere">{f.summary}</span>
                  <span className="block text-xs text-muted-foreground break-anywhere">{f.suggestedAction}</span>
                </li>
              ))}
            </ul>
          )}
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Every requisition explained</CardTitle>
          <CardDescription>Including pending, retrying and unresolved records: nothing is left without an explanation.</CardDescription>
        </CardHeader>
        <CardContent>
          <DataTable testId="explanations" data={explanations} columns={explanationColumns} getRowId={(r) => r.id} />
        </CardContent>
      </Card>
    </div>
  );
}

export function ReconciliationPage() {
  const recon = useQuery({ queryKey: qk.reconciliation, queryFn: api.reconciliation, refetchInterval: 20_000 });
  const rows = useQuery({ queryKey: qk.requisitions, queryFn: api.requisitions });
  const download = useLabAction<"json" | "md" | "html" | "csv", void>({
    name: "Download report",
    invalidate: [],
    run: async (f) => saveResponse(await api.report(f), `reconciliation-latest.${f}`),
    confirm: (_, f) => `the lab served its latest ${f.toUpperCase()} report.`,
  });
  return (
    <div>
      <PageHeader
        title="Reconciliation & reports"
        description="The source system, SAP and the integration's own records, checked against each other. The reports are the files the lab wrote after its last run."
        actions={(["json", "md", "html", "csv"] as const).map((f) => (
          <Button key={f} variant="outline" size="sm" onClick={() => void download.execute(f)} disabled={download.pending}>
            <Download /> {f.toUpperCase()}
          </Button>
        ))}
      />
      {download.phase !== "idle" ? <ActionStatus className="mb-3" phase={download.phase} message={download.message} /> : null}
      <QueryState query={recon} title="Could not load reconciliation">
        {(v) => <Body v={v} rows={rows.data ?? []} />}
      </QueryState>
    </div>
  );
}
