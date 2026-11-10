import type { Metadata } from "next";
import { TryPage } from "@/components/try/try-page";

export const metadata: Metadata = { title: "Try it yourself" };

export default function Page() {
  return <TryPage />;
}
