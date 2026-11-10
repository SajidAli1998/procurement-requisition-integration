"use client";

import { useMemo } from "react";
import { useSearchParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { ErrorPanel } from "@/components/error-panel";
import { PageHeader } from "@/components/page-header";
import { Skeleton } from "@/components/ui/skeleton";
import { api, qk } from "@/lib/api";
import { buildTemplates, newLabId } from "@/lib/requisition-form";
import type { SourceRecord } from "@/lib/types";
import { RequisitionEditor, type EditorInitial } from "./requisition-editor";

export function NewRequisition() {
  const params = useSearchParams();
  const cloneId = params.get("clone");
  const templateId = params.get("template");

  const clone = useQuery({
    queryKey: qk.requisition(cloneId ?? ""),
    queryFn: () => api.requisition(cloneId ?? ""),
    enabled: !!cloneId,
    refetchOnWindowFocus: false,
    staleTime: Infinity,
  });

  const templateInitial = useMemo<EditorInitial | undefined>(() => {
    if (!templateId) return undefined;
    const t = buildTemplates().find((x) => x.id === templateId);
    if (!t) return { note: `Unknown template "${templateId}"; starting from an empty form.` };
    return t.raw ? { raw: t.raw, original: null, note: `Template "${t.name}" loaded as raw JSON.` } : { record: t.record, note: `Template "${t.name}": ${t.description}` };
  }, [templateId]);

  const header = (
    <PageHeader
      title="New requisition"
      description="Create a record in the isolated lab source: valid, or deliberately broken to test how the integration handles it."
    />
  );

  if (cloneId) {
    if (clone.isPending)
      return (
        <div>
          {header}
          <div role="status" aria-busy="true" aria-label="Loading record to clone" data-testid="loading" className="space-y-2">
            <Skeleton className="h-10 w-full" />
            <Skeleton className="h-64 w-full" />
          </div>
        </div>
      );
    if (clone.isError)
      return (
        <div className="space-y-4">
          {header}
          <ErrorPanel error={clone.error} title={`Could not load ${cloneId} to clone`} onRetry={() => void clone.refetch()} />
          <RequisitionEditor key="empty" mode="new" />
        </div>
      );
    const src = clone.data.source;
    const newId = newLabId();
    const record: SourceRecord | undefined = src ? { ...src, id: newId } : undefined;
    if (record) {
      delete record.version;
      delete record.updatedAt;
      delete record.erpSync;
    }
    return (
      <div>
        {header}
        <RequisitionEditor
          key={`clone-${cloneId}`}
          mode="new"
          initial={
            record
              ? { record, original: src, note: `Cloned from ${cloneId} as ${newId}. Saving creates a new requisition.` }
              : { note: `${cloneId} has no record in the lab source; starting from an empty form.` }
          }
        />
      </div>
    );
  }

  return (
    <div>
      {header}
      <RequisitionEditor key={`t-${templateId ?? "none"}`} mode="new" initial={templateInitial} />
    </div>
  );
}
