import type { Metadata } from "next";
import { ReconciliationPage } from "@/components/reconciliation/reconciliation-page";

export const metadata: Metadata = { title: "Reconciliation" };

export default function Page() {
  return <ReconciliationPage />;
}
