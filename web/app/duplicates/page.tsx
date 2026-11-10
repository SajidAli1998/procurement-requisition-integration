"use client";

import Link from "next/link";
import { useMemo } from "react";
import { useQueries, useQuery } from "@tanstack/react-query";
import type { ColumnDef, FilterFn } from "@tanstack/react-table";
import { PageHeader } from "@/components/page-header";
import { ErrorPanel } from "@/components/error-panel";
import { EmptyState } from "@/components/empty-state";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Progress } from "@/components/ui/progress";
import { Skeleton } from "@/components/ui/skeleton";
import { DataTable, exactFilter, textFilter } from "@/components/data-table";
import { OutcomeBadge, ToneBadge } from "@/components/status-badge";
import { Protections } from "@/components/duplicates/protections";
import { ActionsPanel } from "@/components/duplicates/actions-panel";
import { SapAudit } from "@/components/duplicates/sap-audit";
import { LAYER_LABEL, classifyLayer, type LayerKey } from "@/components/duplicates/layers";
import { api, qk } from "@/lib/api";
import { outcomeCategory } from "@/lib/status";
import type { RequisitionRow } from "@/lib/types";

const MAX_DETAILS = 300;

interface DecisionRow {
  id: string;
  outcome: string;
  layerKey: LayerKey | "loading" | "error";
  layer: string;
  detail: string;
  created: number | null;
  sapNumber: string | null;
}

function reachedSap(r: RequisitionRow): boolean {
  const c = outcomeCategory(r.outcome);
  return c === "created" || c === "linked" || c === "updated" || !!r.sapNumber;
}

const columns: ColumnDef<DecisionRow, unknown>[] = [
  {
    accessorKey: "id",
    header: "Requisition",
    meta: { label: "Requisition", filter: "text" },
    filterFn: textFilter as FilterFn<DecisionRow>,
    cell: (c) => (
      <Link href={`/requisitions/${encodeURIComponent(String(c.getValue()))}`} className="font-mono text-xs text-primary underline-offset-2 hover:underline">
        {String(c.getValue())}
      </Link>
    ),
  },
  { accessorKey: "outcome", header: "Outcome", meta: { label: "Outcome" }, cell: (c) => <OutcomeBadge outcome={String(c.getValue())} /> },
  {
    accessorKey: "layer",
    header: "Deciding protection",
    meta: { label: "Deciding protection", filter: "select", options: Object.values(LAYER_LABEL) },
    filterFn: exactFilter as FilterFn<DecisionRow>,
    cell: (c) => {
      const k = c.row.original.layerKey;
      if (k === "loading") return <span className="text-xs text-muted-foreground">loading...</span>;
      if (k === "error") return <ToneBadge tone="danger">could not load</ToneBadge>;
      const tone = k === "created" ? "success" : k === "ambiguous" ? "critical" : k === "not-reached" || k === "other" ? "muted" : "info";
      return <ToneBadge tone={tone}>{String(c.getValue())}</ToneBadge>;
    },
  },
  { accessorKey: "detail", header: "Detail", meta: { label: "Detail", className: "min-w-64" }, cell: (c) => <span className="text-xs break-anywhere">{String(c.getValue())}</span> },
  {
    accessorKey: "created",
    header: "SAP creates",
    meta: { label: "SAP creates" },
    cell: (c) => {
      const n = c.getValue() as number | null;
      if (n === null) return "-";
      return <span className={n > 1 ? "font-semibold text-destructive" : "tabular-nums"}>{n}</span>;
    },
  },
  { accessorKey: "sapNumber", header: "SAP number", meta: { label: "SAP number" }, cell: (c) => <span className="font-mono text-xs">{(c.getValue() as string | null) ?? "-"}</span> },
];

export default function DuplicatesPage() {
  const rowsQ = useQuery({ queryKey: qk.requisitions, queryFn: api.requisitions });
  const docsQ = useQuery({ queryKey: qk.sapDocuments, queryFn: api.sapDocuments });
  const wsQ = useQuery({ queryKey: qk.workspace, queryFn: api.workspace });
  const dashQ = useQuery({ queryKey: qk.dashboard, queryFn: api.dashboard });

  const candidates = useMemo(() => (rowsQ.data ?? []).filter(reachedSap).slice(0, MAX_DETAILS), [rowsQ.data]);
  const truncated = (rowsQ.data ?? []).filter(reachedSap).length > MAX_DETAILS;

  const details = useQueries({
    queries: candidates.map((r) => ({ queryKey: qk.requisition(r.id), queryFn: () => api.requisition(r.id), staleTime: 10_000 })),
  });

  const decisions: DecisionRow[] = candidates.map((r, i) => {
    const q = details[i];
    const d = q?.data?.duplicateDecision;
    if (!d) {
      return { id: r.id, outcome: r.outcome, layerKey: q?.isError ? "error" : "loading", layer: "", detail: q?.isError ? String((q.error as Error).message) : "", created: null, sapNumber: r.sapNumber };
    }
    const key = classifyLayer(d.layer);
    return { id: r.id, outcome: r.outcome, layerKey: key, layer: LAYER_LABEL[key], detail: `${d.layer}: ${d.detail}`, created: d.created, sapNumber: r.sapNumber };
  });

  const counts = decisions.reduce(
    (acc, d) => {
      if (d.layerKey !== "loading" && d.layerKey !== "error") acc[d.layerKey]++;
      return acc;
    },
    { mapping: 0, idempotency: 0, lookup: 0, ambiguous: 0, created: 0, "not-reached": 0, other: 0 } as Record<LayerKey, number>,
  );
  const loaded = decisions.filter((d) => d.layerKey !== "loading").length;
  const multiCreates = decisions.filter((d) => (d.created ?? 0) > 1);
  const created = (rowsQ.data ?? []).filter((r) => outcomeCategory(r.outcome) === "created").length;
  const linked = (rowsQ.data ?? []).filter((r) => outcomeCategory(r.outcome) === "linked").length;

  return (
    <div className="space-y-4">
      <PageHeader
        title="Duplicate detection lab"
        description="Try to make the integration create a requisition in SAP twice, and see which protection stopped it. The audit trail below is read from the SAP mock, not inferred."
      />

      {rowsQ.isError ? <ErrorPanel error={rowsQ.error} onRetry={() => void rowsQ.refetch()} /> : null}

      <Protections counts={counts} loaded={loaded} total={candidates.length} />

      {multiCreates.length > 0 ? (
        <ErrorPanel
          title="More than one SAP create for a requisition"
          error={new Error(`${multiCreates.map((d) => `${d.id} (${d.created}×)`).join(", ")}: the integration created more than one SAP document for these requisitions.`)}
        />
      ) : null}

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_minmax(0,1.1fr)]">
        <ActionsPanel ids={(rowsQ.data ?? []).map((r) => r.id)} sapSettings={wsQ.data?.faults.sap?.settings ?? null} />
        <Card>
          <CardHeader>
            <CardTitle>Decision per requisition</CardTitle>
            <CardDescription>
              For every requisition that reached SAP or was linked: which protection decided it, from GET /api/workspace/requisitions/&#123;id&#125; (duplicateDecision).
              {truncated ? ` Showing the first ${MAX_DETAILS}.` : ""}
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-2">
            {rowsQ.isPending ? (
              <Skeleton className="h-48 w-full" />
            ) : candidates.length === 0 ? (
              <EmptyState title="No requisition has reached SAP yet" description="Run a sync, then come back to see how each record was decided." />
            ) : (
              <>
                {loaded < candidates.length ? (
                  <div className="space-y-1">
                    <Progress value={loaded} max={candidates.length} label="Loading decisions" />
                    <p className="text-xs text-muted-foreground">
                      Loading decisions: {loaded} of {candidates.length}
                    </p>
                  </div>
                ) : null}
                <DataTable
                  data={decisions}
                  columns={columns}
                  getRowId={(r) => r.id}
                  columnFilters
                  initialPageSize={10}
                  searchPlaceholder="Search decisions"
                  testId="decisions"
                  caption="Duplicate decision per requisition"
                />
              </>
            )}
          </CardContent>
        </Card>
      </div>

      {docsQ.isError ? (
        <ErrorPanel error={docsQ.error} onRetry={() => void docsQ.refetch()} />
      ) : docsQ.isPending ? (
        <Skeleton className="h-64 w-full" />
      ) : (
        <SapAudit docs={docsQ.data ?? []} duplicateReferencesInSap={dashQ.data?.totals.duplicateReferencesInSap ?? null} created={created} linked={linked} />
      )}
    </div>
  );
}
