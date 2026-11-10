"use client";

import { useMemo } from "react";
import type { ColumnDef, FilterFn } from "@tanstack/react-table";
import { AlertTriangle } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { DataTable, exactFilter, textFilter } from "@/components/data-table";
import { StatCard } from "@/components/stat-card";
import { ToneBadge } from "@/components/status-badge";
import { fmtDateTime } from "@/lib/format";
import type { SapDocument } from "@/lib/types";
import { asText } from "@/lib/utils";

interface AuditRow {
  number: string;
  reference: string;
  version: string;
  createdBy: string;
  origin: "Created by integration" | "Pre-existing (earlier load)" | "Inserted in the lab" | "Other";
  createdAt: string;
  processingStatus: string;
  releaseStatus: string;
  items: number;
  duplicate: boolean;
}

function originOf(createdBy: string): AuditRow["origin"] {
  if (createdBy === "INTEGRATION") return "Created by integration";
  if (createdBy === "EARLIER-LOAD") return "Pre-existing (earlier load)";
  if (createdBy.includes("LAB") || createdBy.includes("TEST") || createdBy === "SETUP") return "Inserted in the lab";
  return "Other";
}

function toAuditRows(docs: SapDocument[]): AuditRow[] {
  const counts = new Map<string, number>();
  for (const d of docs) {
    const ref = asText(d.ExternalReference);
    counts.set(ref, (counts.get(ref) ?? 0) + 1);
  }
  return docs.map((d) => {
    const createdBy = asText(d.CreatedByUser);
    const reference = asText(d.ExternalReference);
    return {
      number: asText(d.PurchaseRequisition),
      reference,
      version: asText(d.ExternalVersion),
      createdBy,
      origin: originOf(createdBy),
      createdAt: asText(d.CreationDateTime),
      processingStatus: asText(d.ProcessingStatus),
      releaseStatus: asText(d.ReleaseStatus),
      items: Array.isArray(d.to_PurchaseReqnItem) ? d.to_PurchaseReqnItem.length : 0,
      duplicate: reference !== "" && (counts.get(reference) ?? 0) > 1,
    };
  });
}

const columns: ColumnDef<AuditRow, unknown>[] = [
  { accessorKey: "number", header: "SAP number", meta: { label: "SAP number", filter: "text" }, filterFn: textFilter as FilterFn<AuditRow>, cell: (c) => <span className="font-mono text-xs">{String(c.getValue())}</span> },
  {
    accessorKey: "reference",
    header: "External reference",
    meta: { label: "External reference", filter: "text" },
    filterFn: textFilter as FilterFn<AuditRow>,
    cell: (c) => (
      <span className="inline-flex items-center gap-1 font-mono text-xs">
        {String(c.getValue()) || <em className="text-muted-foreground">none</em>}
        {c.row.original.duplicate ? (
          <ToneBadge tone="danger" title="More than one SAP document has this reference">
            <AlertTriangle aria-hidden /> duplicate
          </ToneBadge>
        ) : null}
      </span>
    ),
  },
  { accessorKey: "version", header: "Version", meta: { label: "Version" } },
  {
    accessorKey: "origin",
    header: "Origin",
    meta: { label: "Origin", filter: "select", options: ["Created by integration", "Pre-existing (earlier load)", "Inserted in the lab", "Other"] },
    filterFn: exactFilter as FilterFn<AuditRow>,
    cell: (c) => {
      const o = c.getValue() as AuditRow["origin"];
      return (
        <ToneBadge tone={o === "Created by integration" ? "success" : o === "Pre-existing (earlier load)" ? "info" : "muted"} title={`CreatedByUser: ${c.row.original.createdBy}`}>
          {o}
        </ToneBadge>
      );
    },
  },
  { accessorKey: "createdBy", header: "Created by user", meta: { label: "Created by user" }, cell: (c) => <span className="font-mono text-xs">{String(c.getValue())}</span> },
  {
    accessorKey: "createdAt",
    header: "Created at",
    meta: { label: "Created at" },
    cell: (c) => <span className="text-xs whitespace-nowrap">{fmtDateTime(String(c.getValue()))}</span>,
  },
  { accessorKey: "processingStatus", header: "Processing", meta: { label: "Processing status" } },
  { accessorKey: "releaseStatus", header: "Release", meta: { label: "Release status" }, cell: (c) => String(c.getValue()) || "-" },
  { accessorKey: "items", header: "Items", meta: { label: "Items" } },
];

export function SapAudit({ docs, duplicateReferencesInSap, created, linked }: { docs: SapDocument[]; duplicateReferencesInSap: number | null; created: number; linked: number }) {
  const rows = useMemo(() => toAuditRows(docs), [docs]);
  const distinct = new Set(rows.map((r) => r.reference)).size;
  const dupRefs = new Set(rows.filter((r) => r.duplicate).map((r) => r.reference)).size;
  const byIntegration = rows.filter((r) => r.origin === "Created by integration").length;
  const preExisting = rows.length - byIntegration;
  return (
    <Card>
      <CardHeader>
        <CardTitle>SAP audit trail</CardTitle>
        <CardDescription>
          Every document in the SAP mock (GET /api/workspace/sap/documents). Rows flagged duplicate share an ExternalReference with another document.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <div className="grid grid-cols-2 gap-2 md:grid-cols-3 xl:grid-cols-6">
          <StatCard label="SAP documents" value={rows.length} testId="dup-docs" />
          <StatCard label="Distinct references" value={distinct} />
          <StatCard label="Duplicate references" value={dupRefs} tone={dupRefs > 0 ? "danger" : "success"} hint={duplicateReferencesInSap !== null ? `Dashboard reports ${duplicateReferencesInSap}` : undefined} testId="dup-duplicates" />
          <StatCard label="Created by integration" value={byIntegration} tone="success" />
          <StatCard label="Pre-existing / inserted" value={preExisting} tone="info" hint="Linked, never created by the integration" />
          <StatCard label="Requisitions created / linked" value={`${created} / ${linked}`} hint="From requisition outcomes" />
        </div>
        <DataTable
          data={rows}
          columns={columns}
          getRowId={(r, i) => `${r.number}-${i}`}
          columnFilters
          initialSorting={[{ id: "reference", desc: false }]}
          rowClassName={(r) => (r.duplicate ? "bg-red-50 dark:bg-red-950/30" : undefined)}
          searchPlaceholder="Search SAP documents"
          testId="sap-audit"
          emptyMessage="SAP holds no documents."
          caption="SAP documents"
        />
      </CardContent>
    </Card>
  );
}
