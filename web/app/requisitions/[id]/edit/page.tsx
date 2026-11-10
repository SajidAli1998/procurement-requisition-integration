"use client";

import Link from "next/link";
import { use } from "react";
import { useQuery } from "@tanstack/react-query";
import { ArrowLeft } from "lucide-react";
import { EmptyState } from "@/components/empty-state";
import { ErrorPanel } from "@/components/error-panel";
import { PageHeader } from "@/components/page-header";
import { RequisitionEditor } from "@/components/requisition/requisition-editor";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { api, isApiError, qk } from "@/lib/api";

export default function EditRequisitionPage({ params }: { params: Promise<{ id: string }> }) {
  const { id: raw } = use(params);
  const id = decodeURIComponent(raw);
  // Read once: background refetches must not replace what the operator is editing.
  const query = useQuery({ queryKey: qk.requisition(id), queryFn: () => api.requisition(id), refetchOnWindowFocus: false, staleTime: Infinity });

  return (
    <div>
      <PageHeader
        title={`Edit ${id}`}
        description="Correct the record in the isolated lab source. The next sync (or a replay) sends it through the normal pipeline."
        actions={
          <Button asChild variant="ghost" size="sm">
            <Link href={`/requisitions/${encodeURIComponent(id)}`}>
              <ArrowLeft /> Back to detail
            </Link>
          </Button>
        }
      />
      {query.isPending ? (
        <div role="status" aria-busy="true" aria-label="Loading requisition" data-testid="loading" className="space-y-2">
          <Skeleton className="h-10 w-full" />
          <Skeleton className="h-64 w-full" />
        </div>
      ) : query.isError ? (
        isApiError(query.error) && query.error.kind === "not-found" ? (
          <EmptyState
            title={`Requisition ${id} not found`}
            description="There is nothing to edit. Create it instead."
            action={
              <Button asChild size="sm" variant="outline">
                <Link href="/requisitions/new">New requisition</Link>
              </Button>
            }
          />
        ) : (
          <ErrorPanel error={query.error} title={`Could not load ${id}`} onRetry={() => void query.refetch()} />
        )
      ) : (
        <RequisitionEditor
          key={id}
          mode="edit"
          lockedId={id}
          initial={
            query.data.source
              ? { record: query.data.source, original: query.data.source }
              : { record: { id }, original: null, note: `${id} is known to the integration but not in the lab source; saving recreates it there.` }
          }
        />
      )}
    </div>
  );
}
