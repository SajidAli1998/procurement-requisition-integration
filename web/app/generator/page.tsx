import type { Metadata } from "next";
import { GeneratorPage } from "@/components/generator/generator-page";

export const metadata: Metadata = { title: "Data generator" };

export default function Page() {
  return <GeneratorPage />;
}
