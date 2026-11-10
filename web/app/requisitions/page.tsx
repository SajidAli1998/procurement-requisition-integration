import { Suspense } from "react";
import type { Metadata } from "next";
import { RequisitionsExplorer } from "@/components/requisitions/explorer";

export const metadata: Metadata = { title: "Requisitions" };

export default function RequisitionsPage() {
  return (
    <Suspense>
      <RequisitionsExplorer />
    </Suspense>
  );
}
