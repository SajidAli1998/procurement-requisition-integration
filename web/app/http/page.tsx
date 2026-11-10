import { Suspense } from "react";
import type { Metadata } from "next";
import { HttpLog } from "@/components/http/http-log";

export const metadata: Metadata = { title: "HTTP log" };

export default function Page() {
  return (
    <Suspense>
      <HttpLog />
    </Suspense>
  );
}
