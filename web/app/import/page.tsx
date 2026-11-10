import type { Metadata } from "next";
import { ImportPage } from "@/components/import/import-page";

export const metadata: Metadata = { title: "Import" };

export default function Page() {
  return <ImportPage />;
}
