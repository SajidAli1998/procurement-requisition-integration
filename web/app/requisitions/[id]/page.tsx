"use client";

import { use } from "react";
import { RequisitionDetailView } from "@/components/requisition/requisition-detail";

export default function RequisitionDetailPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  return <RequisitionDetailView id={decodeURIComponent(id)} />;
}
