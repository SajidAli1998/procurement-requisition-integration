"use client";

import Link from "next/link";
import { useCallback, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import type { ColumnDef } from "@tanstack/react-table";
import { ChevronRight, FlaskConical } from "lucide-react";
import { DataTable, exactFilter } from "@/components/data-table";
import { PageHeader } from "@/components/page-header";
import { QueryState } from "@/components/query-state";
import { EmptyState } from "@/components/empty-state";
import { StatCard } from "@/components/stat-card";
import { ToneBadge } from "@/components/status-badge";
import { NativeSelect } from "@/components/ui/select";
import { api, qk } from "@/lib/api";
import { fmtDateTime } from "@/lib/format";
import { severityTone } from "@/lib/status";
import type { QuarantineItem } from "@/lib/types";
import { cn } from "@/lib/utils";
import { PinContext } from "./pin-context";
import { QuarantineRepair } from "./quarantine-repair";

type Col = ColumnDef<QuarantineItem, unknown>;

const columns: Col[] = [
  {
    id: "expand",
    header: () => <span className="sr-only">Expand</span>,
    enableHiding: false,
    cell: ({ row }) => (
      <button
        type="button"
        onClick={row.getToggleExpandedHandler()}
        aria-expanded={row.getIsExpanded()}
        aria-label={`${row.getIsExpanded() ? "Collapse" : "Open repair for"} ${row.original.sourceKey}`}
        className="rounded p-0.5 hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
      >
        <ChevronRight className={cn("size-4 transition-transform", row.getIsExpanded() && "rotate-90")} />
      </button>
    ),
  },
  {
    id: "sourceKey",
    accessorKey: "sourceKey",
    header: "Requisition",
    meta: { label: "Requisition" },
    cell: ({ row }) => (
      <Link href={`/requisitions/${encodeURIComponent(row.original.sourceKey)}`} className="font-mono text-xs text-primary hover:underline">
        {row.original.sourceKey}
      </Link>
    ),
  },
  { id: "sourceVersion", accessorKey: "sourceVersion", header: "Ver.", meta: { label: "Version" } },
  { id: "reason", accessorKey: "reason", header: "Reason / code", meta: { label: "Reason" }, filterFn: exactFilter },
  { id: "stage", accessorKey: "stage", header: "Stage", meta: { label: "Stage" } },
  {
    id: "severity",
    accessorKey: "severity",
    header: "Severity",
    meta: { label: "Severity" },
    filterFn: exactFilter,
    cell: ({ row }) => <ToneBadge tone={severityTone(row.original.severity)}>{row.original.severity}</ToneBadge>,
  },
  { id: "summary", accessorKey: "summary", header: "Summary", meta: { label: "Summary" }, cell: ({ row }) => <span className="break-anywhere">{row.original.summary}</span> },
  {
    id: "problems",
    accessorFn: (r) => r.problems.map((p) => `${p.field}: ${p.message}`).join("; "),
    header: "Problems",
    meta: { label: "Problems" },
    cell: ({ row }) => <span className="tabular-nums">{row.original.problems.length}</span>,
  },
  {
    id: "correctable",
    accessorFn: (r) => (r.correctable ? "Yes" : "No"),
    header: "Correctable",
    meta: { label: "Correctable" },
    cell: ({ row }) => (
      <span title={row.original.correctableIn}>
        {row.original.correctable ? "Yes" : "No"} <span className="text-xs text-muted-foreground">({row.original.correctableIn})</span>
      </span>
    ),
  },
  { id: "attempts", accessorKey: "attempts", header: "Attempts", meta: { label: "Attempts", className: "text-right" } },
  { id: "lastSeenAt", accessorKey: "lastSeenAt", header: "Last seen", meta: { label: "Last seen" }, cell: ({ row }) => <span className="whitespace-nowrap text-xs">{fmtDateTime(row.original.lastSeenAt)}</span> },
  {
    id: "status",
    accessorFn: (r) => (r.resolvedAt ? "Resolved" : "Open"),
    header: "Status",
    meta: { label: "Status" },
    cell: ({ row }) => (row.original.resolvedAt ? <ToneBadge tone="muted">Resolved</ToneBadge> : <ToneBadge tone="danger">Open</ToneBadge>),
  },
  { id: "suggestedAction", accessorKey: "suggestedAction", header: "Suggested action", meta: { label: "Suggested action" }, cell: ({ row }) => <span className="text-xs break-anywhere">{row.original.suggestedAction}</span> },
];

function Body({ items }: { items: QuarantineItem[] }) {
  const [status, setStatus] = useState("Open");
  const [severity, setSeverity] = useState("");
  const [reason, setReason] = useState("");
  const [pinned, setPinned] = useState<ReadonlySet<string>>(new Set());
  const pin = useCallback((id: string) => setPinned((p) => (p.has(id) ? p : new Set(p).add(id))), []);
  const reasons = useMemo(() => [...new Set(items.map((i) => i.reason))].sort(), [items]);
  const open = items.filter((i) => !i.resolvedAt);
  const shown = items.filter((i) => !status || (status === "Open") === !i.resolvedAt || pinned.has(i.sourceKey));
  const filters = [
    ...(severity ? [{ id: "severity", value: severity }] : []),
    ...(reason ? [{ id: "reason", value: reason }] : []),
  ];
  if (items.length === 0) {
    return <EmptyState icon={FlaskConical} title="Nothing in quarantine" description="No requisition has been held back by the integration in this workspace." />;
  }
  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <StatCard label="Open" value={open.length} tone={open.length ? "danger" : "success"} testId="q-open" />
        <StatCard label="Critical (open)" value={open.filter((i) => i.severity === "Critical").length} tone="critical" />
        <StatCard label="Correctable (open)" value={open.filter((i) => i.correctable).length} tone="warning" />
        <StatCard label="Resolved" value={items.length - open.length} tone="muted" />
      </div>
      <PinContext.Provider value={pin}>
      <DataTable
        testId="quarantine"
        caption="Quarantine entries"
        data={shown}
        columns={columns}
        getRowId={(r) => String(r.id)}
        initialVisibility={{ stage: false, suggestedAction: false, sourceVersion: false }}
        filters={filters}
        onFiltersChange={(f) => {
          setSeverity(String(f.find((x) => x.id === "severity")?.value ?? ""));
          setReason(String(f.find((x) => x.id === "reason")?.value ?? ""));
        }}
        renderExpanded={(row) => <QuarantineRepair item={row.original} />}
        searchPlaceholder="Search id, reason, message"
        toolbar={() => (
          <>
            <NativeSelect aria-label="Filter by status" value={status} onChange={(e) => setStatus(e.target.value)} className="w-32">
              <option value="Open">Open</option>
              <option value="Resolved">Resolved</option>
              <option value="">All</option>
            </NativeSelect>
            <NativeSelect aria-label="Filter by severity" value={severity} onChange={(e) => setSeverity(e.target.value)} className="w-36">
              <option value="">All severities</option>
              <option value="Critical">Critical</option>
              <option value="High">High</option>
              <option value="Medium">Medium</option>
            </NativeSelect>
            <NativeSelect aria-label="Filter by reason" value={reason} onChange={(e) => setReason(e.target.value)} className="w-52">
              <option value="">All reasons</option>
              {reasons.map((r) => (
                <option key={r} value={r}>
                  {r}
                </option>
              ))}
            </NativeSelect>
          </>
        )}
      />
      </PinContext.Provider>
    </div>
  );
}

export function QuarantinePage() {
  const query = useQuery({ queryKey: qk.quarantine, queryFn: api.quarantine, refetchInterval: 15_000 });
  return (
    <div>
      <PageHeader
        title="Quarantine"
        description="Requisitions the integration held back instead of sending to SAP. Open one to fix it, check it again and send it back through, or close it with a note. Nothing in quarantine is ever sent to SAP."
      />
      <QueryState query={query} title="Could not load quarantine">
        {(items) => <Body items={items} />}
      </QueryState>
    </div>
  );
}
