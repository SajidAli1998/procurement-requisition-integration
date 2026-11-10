"use client";

import Link from "next/link";
import { useEffect, useMemo, useRef } from "react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import type { ColumnFiltersState, PaginationState } from "@tanstack/react-table";
import { Download, FilePlus2 } from "lucide-react";
import { DataTable } from "@/components/data-table";
import { PageHeader } from "@/components/page-header";
import { QueryState } from "@/components/query-state";
import { EmptyState } from "@/components/empty-state";
import { OutcomeBadge } from "@/components/status-badge";
import { Button } from "@/components/ui/button";
import { NativeSelect } from "@/components/ui/select";
import { api, qk } from "@/lib/api";
import { toCsv } from "@/lib/csv";
import { downloadJson, downloadText } from "@/lib/download";
import { outcomeStyle } from "@/lib/status";
import type { RequisitionRow } from "@/lib/types";
import { DEFAULT_HIDDEN, EXPORT_COLUMNS, requisitionColumns, rowIssues } from "./columns";

const OUTCOME_ORDER = ["Created", "Linked", "Updated", "Quarantined", "Skipped", "Pending", "Retrying", "Unresolved (verifying in SAP)", "Unresolved (mid-send)", "Resolved by operator", "Not processed"];

function Expanded({ r }: { r: RequisitionRow }) {
  const issues = Object.entries(rowIssues(r));
  const style = outcomeStyle(r.outcome);
  return (
    <div className="grid gap-3 text-sm md:grid-cols-2">
      <div className="space-y-1">
        <div className="flex items-center gap-2">
          <OutcomeBadge outcome={r.outcome} />
          <span className="text-xs text-muted-foreground">{style.description}</span>
        </div>
        <p>
          <span className="font-medium">Integration state:</span> {r.state} · <span className="font-medium">Reconciliation:</span> {r.reconciliation}
        </p>
        {r.quarantineSummary ? (
          <p className="break-anywhere">
            <span className="font-medium">Quarantine:</span> {r.quarantineReason}: {r.quarantineSummary}
          </p>
        ) : null}
        {r.lastError ? (
          <p className="break-anywhere text-red-700 dark:text-red-300">
            <span className="font-medium">Last error:</span> {r.lastError}
          </p>
        ) : null}
        <Link className="text-primary underline underline-offset-2" href={`/requisitions/${encodeURIComponent(r.id)}`}>
          Open full detail for {r.id}
        </Link>
      </div>
      <div>
        <div className="font-medium">Highlighted values</div>
        {issues.length === 0 ? (
          <p className="text-muted-foreground">No missing or inconsistent values.</p>
        ) : (
          <ul className="list-disc pl-5">
            {issues.map(([f, why]) => (
              <li key={f}>
                <span className="font-mono text-xs">{f}</span>: {why}
              </li>
            ))}
          </ul>
        )}
      </div>
    </div>
  );
}

function Explorer({ rows }: { rows: RequisitionRow[] }) {
  const router = useRouter();
  const pathname = usePathname();
  const params = useSearchParams();
  const columns = useMemo(() => requisitionColumns(rows), [rows]);

  // Search, outcome filter and page live in the URL so back/forward and refresh keep the view.
  const q = params.get("q") ?? "";
  const outcome = params.get("outcome") ?? "";
  const page = Math.max(0, Number(params.get("page") ?? "1") - 1) || 0;
  const size = Number(params.get("size") ?? "25") || 25;

  // Several updates can fire in one event (filter + page reset): merge them instead of letting the last one win.
  const pending = useRef<URLSearchParams | null>(null);
  useEffect(() => {
    pending.current = null;
  }, [params]);
  const setParams = (patch: Record<string, string | null>, push = false) => {
    const next = new URLSearchParams((pending.current ?? params).toString());
    for (const [k, v] of Object.entries(patch)) {
      if (v === null || v === "") next.delete(k);
      else next.set(k, v);
    }
    pending.current = next;
    const url = `${pathname}${next.toString() ? `?${next}` : ""}`;
    if (push) router.push(url, { scroll: false });
    else router.replace(url, { scroll: false });
  };

  const otherFilters = useMemo<ColumnFiltersState>(() => {
    const raw = params.get("f");
    if (!raw) return [];
    try {
      const parsed = JSON.parse(raw) as Record<string, string>;
      return Object.entries(parsed).map(([id, value]) => ({ id, value }));
    } catch {
      return [];
    }
  }, [params]);
  const filters: ColumnFiltersState = [...otherFilters.filter((f) => f.id !== "outcome"), ...(outcome ? [{ id: "outcome", value: outcome }] : [])];
  const pagination: PaginationState = { pageIndex: page, pageSize: size };

  const outcomes = useMemo(() => {
    const present = new Set(rows.map((r) => r.outcome));
    return [...OUTCOME_ORDER.filter((o) => present.has(o)), ...[...present].filter((o) => !OUTCOME_ORDER.includes(o))];
  }, [rows]);

  return (
    <DataTable
      testId="requisitions"
      caption="Requisitions in the lab source with their integration outcome"
      data={rows}
      columns={columns}
      getRowId={(r) => r.id}
      columnFilters
      initialVisibility={DEFAULT_HIDDEN}
      initialSorting={[{ id: "id", desc: false }]}
      pageSizes={[10, 25, 50, 100, 250]}
      renderExpanded={(row) => <Expanded r={row.original} />}
      onRowClick={(r) => router.push(`/requisitions/${encodeURIComponent(r.id)}`)}
      rowClassName={(r) => (outcomeStyle(r.outcome).category === "quarantined" ? "bg-red-50/40 dark:bg-red-950/10" : undefined)}
      searchPlaceholder="Search id, requester, material, reason"
      globalFilter={q}
      onGlobalFilterChange={(v) => setParams({ q: v, page: null })}
      filters={filters}
      onFiltersChange={(f) => {
        const o = f.find((x) => x.id === "outcome")?.value as string | undefined;
        const rest = Object.fromEntries(f.filter((x) => x.id !== "outcome" && x.value).map((x) => [x.id, String(x.value)]));
        setParams({ outcome: o ?? null, f: Object.keys(rest).length ? JSON.stringify(rest) : null, page: null });
      }}
      pagination={pagination}
      onPaginationChange={(p) => setParams({ page: p.pageIndex > 0 ? String(p.pageIndex + 1) : null, size: p.pageSize !== 25 ? String(p.pageSize) : null }, true)}
      toolbar={(table) => {
        const exportRows = () => table.getPrePaginationRowModel().rows.map((r) => r.original);
        const stamp = new Date().toISOString().replace(/[:.]/g, "-");
        return (
          <>
            <NativeSelect aria-label="Filter by outcome" value={outcome} onChange={(e) => setParams({ outcome: e.target.value, page: null })} className="w-48" data-testid="outcome-filter">
              <option value="">All outcomes</option>
              {outcomes.map((o) => (
                <option key={o} value={o}>
                  {o}
                </option>
              ))}
            </NativeSelect>
            <Button
              variant="outline"
              size="sm"
              onClick={() => downloadText(toCsv(exportRows(), EXPORT_COLUMNS.map((key) => ({ key }))), `requisitions-${stamp}.csv`, "text/csv")}
            >
              <Download /> Export CSV
            </Button>
            <Button variant="outline" size="sm" onClick={() => downloadJson(exportRows(), `requisitions-${stamp}.json`)}>
              <Download /> Export JSON
            </Button>
            {q || filters.length > 0 ? (
              <Button variant="ghost" size="sm" onClick={() => setParams({ q: null, outcome: null, f: null, page: null })}>
                Clear filters
              </Button>
            ) : null}
          </>
        );
      }}
    />
  );
}

export function RequisitionsExplorer() {
  const query = useQuery({ queryKey: qk.requisitions, queryFn: api.requisitions, refetchInterval: 15_000 });
  return (
    <div>
      <PageHeader
        title="Requisitions"
        description="Every requisition in the lab and what the integration did with it. Missing values are marked in red and values that do not add up in amber. Click a row to see the full record."
        actions={
          <Button asChild size="sm">
            <Link href="/requisitions/new">
              <FilePlus2 /> New requisition
            </Link>
          </Button>
        }
      />
      <QueryState query={query} title="Could not load requisitions">
        {(rows) =>
          rows.length === 0 ? (
            <EmptyState title="No requisitions in the lab source" description="Reset the workspace to the Workbook dataset, generate data or import a file." />
          ) : (
            <Explorer rows={rows} />
          )
        }
      </QueryState>
    </div>
  );
}
