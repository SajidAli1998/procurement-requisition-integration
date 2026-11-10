"use client";

import Link from "next/link";
import type { ColumnDef } from "@tanstack/react-table";
import { AlertTriangle, ChevronRight } from "lucide-react";
import { exactFilter, textFilter } from "@/components/data-table";
import { OutcomeBadge, ToneBadge } from "@/components/status-badge";
import { fmtDateOnly, fmtDateTime } from "@/lib/format";
import type { RequisitionRow } from "@/lib/types";
import { asNumber, asText, cn, isBlank } from "@/lib/utils";
import { isSynchronised } from "@/lib/status";

export function rowIssues(r: RequisitionRow): Record<string, string> {
  const issues: Record<string, string> = {};
  for (const f of ["requester", "costCenter", "companyCode", "currency", "requiredDelivery", "materialCode", "plant", "approvalStatus"] as const) {
    if (isBlank(r[f])) issues[f] = "missing";
  }
  const total = asNumber(r.totalAmount);
  if (total === null) issues.totalAmount = isBlank(r.totalAmount) ? "missing" : "not a number";
  else if (total <= 0) issues.totalAmount = "must be greater than zero";
  const qty = asNumber(r.quantity);
  if (qty === null) issues.quantity = isBlank(r.quantity) ? "missing" : "not a number";
  else if (qty <= 0) issues.quantity = "must be greater than zero";
  else if ((r.unitOfMeasure === "EA" || r.unitOfMeasure === "BOX") && !Number.isInteger(qty)) issues.quantity = `decimal quantity for ${asText(r.unitOfMeasure)}`;
  const price = asNumber(r.unitPrice);
  if (price !== null && price < 0) issues.unitPrice = "negative";
  if (r.itemCount === 0) issues.itemCount = "no lines";
  if (isSynchronised(r.outcome) && !r.sapNumber) issues.sapNumber = "synchronised outcome without a SAP number";
  if (!isSynchronised(r.outcome) && r.sapNumber && r.outcome !== "Resolved by operator") issues.sapNumber = "SAP number on a record that is not synchronised";
  if (r.reconciliation === "Missing in SAP" || r.reconciliation === "Linked to another reference") issues.reconciliation = r.reconciliation;
  return issues;
}

function Cell({ row, field, value, render }: { row: RequisitionRow; field: string; value: unknown; render?: (v: unknown) => React.ReactNode }) {
  const issue = rowIssues(row)[field];
  if (isBlank(value)) {
    return issue ? (
      <span className="inline-flex items-center rounded bg-red-100 px-1.5 text-[11px] font-medium text-red-800 dark:bg-red-950 dark:text-red-200" title={`${field}: ${issue}`} data-issue={field}>
        missing
      </span>
    ) : (
      <span className="text-muted-foreground">-</span>
    );
  }
  return (
    <span
      className={cn(issue && "inline-flex items-center gap-1 rounded bg-amber-100 px-1 text-amber-900 dark:bg-amber-950 dark:text-amber-200")}
      title={issue ? `${field}: ${issue}` : undefined}
      data-issue={issue ? field : undefined}
    >
      {issue ? <AlertTriangle className="size-3" aria-label={`Inconsistent: ${issue}`} /> : null}
      {render ? render(value) : asText(value)}
    </span>
  );
}

type Col = ColumnDef<RequisitionRow, unknown>;

function plain(key: keyof RequisitionRow, label: string, opts: { filter?: "text" | "select"; options?: string[]; hidden?: boolean; className?: string; render?: (v: unknown) => React.ReactNode } = {}): Col {
  return {
    id: key,
    accessorFn: (r) => r[key],
    header: label,
    meta: { label, filter: opts.filter ?? "text", options: opts.options, className: opts.className },
    filterFn: opts.filter === "select" ? exactFilter : textFilter,
    sortingFn: "alphanumeric",
    cell: ({ row }) => <Cell row={row.original} field={key} value={row.original[key]} render={opts.render} />,
  };
}

function numeric(key: keyof RequisitionRow, label: string): Col {
  return {
    ...plain(key, label, { className: "text-right tabular-nums" }),
    sortingFn: (a, b) => (asNumber(a.original[key]) ?? -Infinity) - (asNumber(b.original[key]) ?? -Infinity),
  };
}

export function requisitionColumns(data: RequisitionRow[]): Col[] {
  const distinct = (k: keyof RequisitionRow) => [...new Set(data.map((r) => asText(r[k])).filter(Boolean))].sort();
  return [
    {
      id: "expand",
      header: () => <span className="sr-only">Expand</span>,
      enableHiding: false,
      enableSorting: false,
      cell: ({ row }) => (
        <button
          type="button"
          onClick={row.getToggleExpandedHandler()}
          aria-expanded={row.getIsExpanded()}
          aria-label={`${row.getIsExpanded() ? "Collapse" : "Expand"} ${row.original.id}`}
          className="rounded p-0.5 hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        >
          <ChevronRight className={cn("size-4 transition-transform", row.getIsExpanded() && "rotate-90")} />
        </button>
      ),
    },
    {
      id: "id",
      accessorKey: "id",
      header: "ID",
      enableHiding: false,
      meta: { label: "ID", filter: "text" },
      filterFn: textFilter,
      cell: ({ row }) => (
        <Link href={`/requisitions/${encodeURIComponent(row.original.id)}`} className="font-mono text-xs font-medium text-primary hover:underline whitespace-nowrap">
          {row.original.id}
        </Link>
      ),
    },
    {
      id: "outcome",
      accessorKey: "outcome",
      header: "Outcome",
      meta: { label: "Outcome", filter: "select", options: distinct("outcome") },
      filterFn: exactFilter,
      cell: ({ row }) => <OutcomeBadge outcome={row.original.outcome} />,
    },
    plain("sourceSystem", "Source system", { hidden: true }),
    plain("requester", "Requester"),
    plain("department", "Department"),
    plain("approvalStatus", "Approval", { filter: "select", options: distinct("approvalStatus") }),
    numeric("itemCount", "Items"),
    plain("materialCode", "Material"),
    plain("description", "Description", { className: "max-w-56 truncate" }),
    numeric("quantity", "Qty"),
    plain("unitOfMeasure", "UoM", { filter: "select", options: distinct("unitOfMeasure") }),
    numeric("unitPrice", "Unit price"),
    plain("currency", "Currency", { filter: "select", options: distinct("currency") }),
    numeric("totalAmount", "Total"),
    plain("costCenter", "Cost center"),
    plain("companyCode", "Company"),
    plain("plant", "Plant", { filter: "select", options: distinct("plant") }),
    plain("purchasingGroup", "Purch. group"),
    plain("requiredDelivery", "Required delivery", { render: (v) => fmtDateOnly(asText(v)) }),
    plain("sapNumber", "SAP number", { render: (v) => <span className="font-mono text-xs">{asText(v)}</span> }),
    plain("sapStatus", "SAP status", { filter: "select", options: distinct("sapStatus") }),
    plain("lastAttempt", "Last attempt", { render: (v) => fmtDateTime(asText(v)) }),
    numeric("retryCount", "Retries"),
    {
      ...plain("validation", "Validation", { filter: "select", options: ["Valid", "Invalid"] }),
      cell: ({ row }) =>
        row.original.validation === "Invalid" ? (
          <ToneBadge tone="danger">Invalid</ToneBadge>
        ) : row.original.validation === "Valid" ? (
          <ToneBadge tone="success">Valid</ToneBadge>
        ) : (
          <span className="text-muted-foreground">not checked</span>
        ),
    },
    plain("quarantineReason", "Quarantine reason", { filter: "select", options: distinct("quarantineReason") }),
    plain("reconciliation", "Reconciliation", { filter: "select", options: distinct("reconciliation") }),
  ];
}

export const DEFAULT_HIDDEN: Record<string, boolean> = { sourceSystem: false, purchasingGroup: false, department: false, description: false, lastAttempt: false };

export const EXPORT_COLUMNS: (keyof RequisitionRow)[] = [
  "id",
  "sourceSystem",
  "requester",
  "department",
  "approvalStatus",
  "itemCount",
  "materialCode",
  "description",
  "quantity",
  "unitOfMeasure",
  "unitPrice",
  "currency",
  "totalAmount",
  "costCenter",
  "companyCode",
  "plant",
  "purchasingGroup",
  "requiredDelivery",
  "sapNumber",
  "sapStatus",
  "lastAttempt",
  "retryCount",
  "validation",
  "quarantineReason",
  "quarantineSummary",
  "reconciliation",
  "outcome",
  "state",
  "lastError",
];
