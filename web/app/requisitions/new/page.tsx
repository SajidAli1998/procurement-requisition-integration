import { Suspense } from "react";
import { NewRequisition } from "@/components/requisition/new-requisition";

export const metadata = { title: "New requisition" };

export default function NewRequisitionPage() {
  return (
    <Suspense fallback={<div role="status" aria-busy="true" data-testid="loading" className="h-64 animate-pulse rounded-md bg-muted" />}>
      <NewRequisition />
    </Suspense>
  );
}
