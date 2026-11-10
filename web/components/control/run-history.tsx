"use client";

import type { ColumnDef } from "@tanstack/react-table";
import { DataTable } from "@/components/data-table";
import { ToneBadge } from "@/components/status-badge";
import { fmtDateTime, fmtMs } from "@/lib/format";
import type { WorkspaceRun } from "@/lib/types";
import type { Tone } from "@/lib/status";

function outcomeTone(r: WorkspaceRun): Tone {
  if (r.state === "Running") return "info";
  switch (r.outcome) {
    case "Completed":
      return "success";
    case "CompletedWithIssues":
      return "warning";
    case "Skipped":
      return "muted";
    case "Stopped":
      return "critical";
    default:
      return "danger";
  }
}

const columns: ColumnDef<WorkspaceRun, unknown>[] = [
  { id: "id", accessorKey: "id", header: "Run", meta: { label: "Run" }, cell: ({ row }) => <span className="font-mono text-xs">{row.original.id}</span> },
  { id: "kind", accessorKey: "kind", header: "Kind", meta: { label: "Kind" } },
  { id: "startedAt", accessorKey: "startedAt", header: "Started", meta: { label: "Started" }, cell: ({ row }) => <span className="whitespace-nowrap">{fmtDateTime(row.original.startedAt)}</span> },
  {
    id: "outcome",
    accessorFn: (r) => r.outcome ?? r.state,
    header: "Outcome",
    meta: { label: "Outcome" },
    cell: ({ row }) => <ToneBadge tone={outcomeTone(row.original)}>{row.original.state === "Running" ? "Running" : (row.original.outcome ?? "-")}</ToneBadge>,
  },
  { id: "durationMs", accessorKey: "durationMs", header: "Duration", meta: { label: "Duration", className: "text-right" }, cell: ({ row }) => fmtMs(row.original.durationMs) },
  {
    id: "events",
    accessorFn: (r) => Object.entries(r.events ?? {}).map(([k, v]) => `${k} ${v}`).join(", "),
    header: "Events",
    meta: { label: "Events" },
    cell: ({ getValue }) => <span className="text-xs break-anywhere">{String(getValue() || "-")}</span>,
  },
  { id: "pending", accessorKey: "pending", header: "Pending", meta: { label: "Pending", className: "text-right" } },
  { id: "openQuarantine", accessorKey: "openQuarantine", header: "Quarantine", meta: { label: "Quarantine", className: "text-right" } },
  { id: "message", accessorKey: "message", header: "Message", meta: { label: "Message" }, cell: ({ row }) => <span className="text-xs break-anywhere">{row.original.message ?? ""}</span> },
];

export function RunHistory({ runs }: { runs: WorkspaceRun[] }) {
  return (
    <DataTable
      testId="run-history"
      caption="Runs in this workspace, newest first"
      data={runs}
      columns={columns}
      getRowId={(r) => r.id}
      initialPageSize={10}
      emptyMessage="No runs yet in this workspace."
      renderExpanded={undefined}
    />
  );
}
