"use client";

import { useMemo, useState } from "react";
import { useSearchParams } from "next/navigation";
import { useQuery } from "@tanstack/react-query";
import { RefreshCw, Search } from "lucide-react";
import { HttpExchangeList } from "@/components/http-exchange-list";
import { PageHeader } from "@/components/page-header";
import { QueryState } from "@/components/query-state";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect } from "@/components/ui/select";
import { api, qk } from "@/lib/api";
import type { HttpExchange } from "@/lib/types";

function statusClass(e: HttpExchange): string {
  if (!e.status) return "none";
  return `${Math.floor(e.status / 100)}xx`;
}

export function HttpLog() {
  const params = useSearchParams();
  const [take, setTake] = useState(500);
  const [q, setQ] = useState(params.get("q") ?? "");
  const [channel, setChannel] = useState(params.get("channel") ?? "");
  const [method, setMethod] = useState("");
  const [status, setStatus] = useState(params.get("status") ?? "");
  const query = useQuery({ queryKey: qk.http(take), queryFn: () => api.http(take), refetchInterval: 5_000 });

  const channels = useMemo(() => [...new Set((query.data ?? []).map((e) => e.channel))].sort(), [query.data]);
  const filtered = useMemo(() => {
    const needle = q.trim().toLowerCase();
    return (query.data ?? []).filter((e) => {
      if (channel && e.channel !== channel) return false;
      if (method && e.method !== method) return false;
      if (status && statusClass(e) !== status) return false;
      if (!needle) return true;
      return [e.url, e.correlationId, e.idempotencyKey, e.requestBody, e.responseBody, String(e.status ?? "")].some((x) => (x ?? "").toLowerCase().includes(needle));
    });
  }, [query.data, q, channel, method, status]);

  return (
    <div>
      <PageHeader
        title="HTTP log"
        description="Every request between the lab's components (integration, source mock, SAP mock, assistant), newest first, with exact status and bodies (bodies truncated at 2 KB by the lab)."
        actions={
          <Button variant="outline" size="sm" onClick={() => void query.refetch()} disabled={query.isFetching}>
            <RefreshCw /> Refresh
          </Button>
        }
      />
      <div className="mb-3 flex flex-wrap items-center gap-2">
        <div className="relative w-full sm:w-80">
          <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
          <Input value={q} onChange={(e) => setQ(e.target.value)} placeholder="URL, correlation id, body text, status" aria-label="Search HTTP log" className="pl-8" />
        </div>
        <NativeSelect aria-label="Channel" value={channel} onChange={(e) => setChannel(e.target.value)} className="w-48">
          <option value="">All channels</option>
          {channels.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
        </NativeSelect>
        <NativeSelect aria-label="Method" value={method} onChange={(e) => setMethod(e.target.value)} className="w-28">
          <option value="">Any method</option>
          {["GET", "POST", "PUT", "PATCH", "DELETE"].map((m) => (
            <option key={m}>{m}</option>
          ))}
        </NativeSelect>
        <NativeSelect aria-label="Status" value={status} onChange={(e) => setStatus(e.target.value)} className="w-36">
          <option value="">Any status</option>
          <option value="2xx">2xx</option>
          <option value="4xx">4xx</option>
          <option value="5xx">5xx</option>
          <option value="none">No response</option>
        </NativeSelect>
        <NativeSelect aria-label="How many to load" value={String(take)} onChange={(e) => setTake(Number(e.target.value))} className="w-32">
          {[200, 500, 1000, 5000].map((n) => (
            <option key={n} value={n}>
              Last {n}
            </option>
          ))}
        </NativeSelect>
        <span className="text-xs text-muted-foreground" aria-live="polite">
          {filtered.length} shown
        </span>
      </div>
      <QueryState query={query} title="Could not load the HTTP log">
        {() => <HttpExchangeList exchanges={filtered} limit={300} empty="No exchanges match." />}
      </QueryState>
    </div>
  );
}
