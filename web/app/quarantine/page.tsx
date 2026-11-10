import type { Metadata } from "next";
import { QuarantinePage } from "@/components/quarantine/quarantine-page";

export const metadata: Metadata = { title: "Quarantine" };

export default function Page() {
  return <QuarantinePage />;
}
