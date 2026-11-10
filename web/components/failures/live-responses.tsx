"use client";

import Link from "next/link";
import { ExternalLink } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { HttpExchangeList } from "@/components/http-exchange-list";
import { httpEvents, useLive } from "@/components/live-provider";
import { TARGET_LABEL, type Target } from "./fault-state-panel";

export function LiveResponses({ target, limit = 25 }: { target: Target; limit?: number }) {
  const live = useLive();
  const channel = `integration→${target}`;
  // The live buffer is shared and mutated in place; useLive re-renders (throttled) when it changes.
  const exchanges = httpEvents(live.events).filter((e) => e.channel === channel).reverse().slice(0, limit);
  const faulty = exchanges.filter((e) => !e.status || e.status >= 400).length;
  return (
    <Card>
      <CardHeader>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <CardTitle>Latest responses: integration → {TARGET_LABEL[target]}</CardTitle>
          <Link href="/http" className="inline-flex items-center gap-1 text-xs text-primary underline-offset-2 hover:underline">
            Full HTTP log <ExternalLink className="size-3" aria-hidden />
          </Link>
        </div>
        <CardDescription>
          Exact status and bodies as captured by the lab (live stream: {live.status}). {exchanges.length > 0 ? `${faulty} of the last ${exchanges.length} were errors or had no response.` : ""}
        </CardDescription>
      </CardHeader>
      <CardContent>
        <HttpExchangeList exchanges={exchanges} empty={`No ${TARGET_LABEL[target]} traffic seen since this page opened. Run a sync to exercise the faults.`} />
      </CardContent>
    </Card>
  );
}
