import type { Metadata } from "next";
import { WhatIfPage } from "@/components/what-if/what-if-page";

export const metadata: Metadata = { title: "What if" };

export default function Page() {
  return <WhatIfPage />;
}
