"use client";

import { ChevronRight } from "lucide-react";
import { useState } from "react";
import { HttpStatusBadge } from "@/components/status-badge";
import { JsonViewer } from "@/components/monaco";
import { Badge } from "@/components/ui/badge";
import { fmtMs, fmtTime } from "@/lib/format";
import type { HttpExchange } from "@/lib/types";
import { cn } from "@/lib/utils";

function ExchangeRow({ e, defaultOpen }: { e: HttpExchange; defaultOpen?: boolean }) {
  const [open, setOpen] = useState(!!defaultOpen);
  return (
    <li className="rounded-md border" data-testid="http-exchange">
      <button
        type="button"
        className="flex w-full flex-wrap items-center gap-2 px-2 py-1.5 text-left text-xs hover:bg-muted/50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
      >
        <ChevronRight className={cn("size-3.5 shrink-0 transition-transform", open && "rotate-90")} aria-hidden />
        <span className="text-muted-foreground tabular-nums">#{e.seq}</span>
        <span className="tabular-nums text-muted-foreground">{fmtTime(e.at)}</span>
        <Badge tone="neutral" className="font-mono">
          {e.method}
        </Badge>
        <HttpStatusBadge status={e.status} />
        <span className="min-w-0 flex-1 truncate font-mono" title={e.url}>
          {e.url}
        </span>
        <span className="text-muted-foreground">{e.channel}</span>
        <span className="tabular-nums text-muted-foreground">{fmtMs(e.durationMs)}</span>
      </button>
      {open ? (
        <div className="space-y-2 border-t p-2 text-xs">
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-0.5">
            <dt className="font-medium">URL</dt>
            <dd className="font-mono break-anywhere">{e.url}</dd>
            <dt className="font-medium">Host</dt>
            <dd>{e.host}</dd>
            <dt className="font-medium">Correlation</dt>
            <dd className="font-mono">{e.correlationId ?? "-"}</dd>
            <dt className="font-medium">Idempotency-Key</dt>
            <dd className="font-mono break-anywhere">{e.idempotencyKey ?? "-"}</dd>
            {e.outcome ? (
              <>
                <dt className="font-medium">Outcome</dt>
                <dd>{e.outcome}</dd>
              </>
            ) : null}
          </dl>
          <div className="grid gap-2 lg:grid-cols-2">
            <div>
              <div className="mb-1 font-medium">Request body</div>
              {e.requestBody ? <JsonViewer value={e.requestBody} maxHeight={260} label="Request body" /> : <p className="text-muted-foreground">(none)</p>}
            </div>
            <div>
              <div className="mb-1 font-medium">Response body</div>
              {e.responseBody ? <JsonViewer value={e.responseBody} maxHeight={260} label="Response body" /> : <p className="text-muted-foreground">{e.status ? "(empty)" : "(no response received)"}</p>}
            </div>
          </div>
        </div>
      ) : null}
    </li>
  );
}

export function HttpExchangeList({ exchanges, empty = "No HTTP exchanges recorded.", limit = 200 }: { exchanges: HttpExchange[]; empty?: string; limit?: number }) {
  if (exchanges.length === 0) return <p className="text-sm text-muted-foreground">{empty}</p>;
  const shown = exchanges.slice(0, limit);
  return (
    <div>
      <ul className="space-y-1">
        {shown.map((e) => (
          <ExchangeRow key={`${e.seq}-${e.at}`} e={e} />
        ))}
      </ul>
      {exchanges.length > limit ? <p className="mt-1 text-xs text-muted-foreground">Showing {limit} of {exchanges.length}.</p> : null}
    </div>
  );
}
