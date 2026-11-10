"use client";

import Link from "next/link";
import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { CheckCircle2, CircleDashed, Pause, Play, Power, RotateCcw, XCircle } from "lucide-react";
import { ActionStatus } from "@/components/action-status";
import { httpEvents, useLive } from "@/components/live-provider";
import { PageHeader } from "@/components/page-header";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { NativeSelect } from "@/components/ui/select";
import { useLabAction } from "@/hooks/use-lab-action";
import { api, qk } from "@/lib/api";
import type { Connection, HandsOnChecks, NetworkState } from "@/lib/types";
import { cn } from "@/lib/utils";
import { collapse, describe } from "./activity";
import { describeLastRun, lastFinishedRun } from "./last-run";

const VERDICT_STYLE: Record<HandsOnChecks["verdict"]["tone"], string> = {
  success: "border-emerald-600/30 bg-emerald-50 text-emerald-950 dark:bg-emerald-950/40 dark:text-emerald-100",
  warning: "border-amber-600/40 bg-amber-50 text-amber-950 dark:bg-amber-950/40 dark:text-amber-100",
  danger: "border-red-600/40 bg-red-50 text-red-950 dark:bg-red-950/40 dark:text-red-100",
  info: "border-sky-600/30 bg-sky-50 text-sky-950 dark:bg-sky-950/40 dark:text-sky-100",
  neutral: "border-border bg-muted/40 text-foreground",
};

function Step({ n, title, description, children }: { n: number; title: string; description?: string; children: React.ReactNode }) {
  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2 text-base">
          <span className="flex size-6 items-center justify-center rounded-full bg-primary text-xs text-primary-foreground" aria-hidden>
            {n}
          </span>
          {title}
        </CardTitle>
        {description ? <CardDescription>{description}</CardDescription> : null}
      </CardHeader>
      <CardContent className="space-y-3">{children}</CardContent>
    </Card>
  );
}

function Choice<T extends string>({ label, value, options, onChange, disabled }: { label: string; value: T; options: { value: T; label: string }[]; onChange: (v: T) => void; disabled?: boolean }) {
  return (
    <div className="flex flex-wrap items-center gap-2" role="group" aria-label={label}>
      <span className="w-44 text-sm text-muted-foreground">{label}</span>
      <div className="inline-flex overflow-hidden rounded-md border">
        {options.map((o) => (
          <button
            key={o.value}
            type="button"
            disabled={disabled}
            aria-pressed={value === o.value}
            onClick={() => onChange(o.value)}
            className={cn(
              "px-3 py-1.5 text-sm transition-colors disabled:opacity-50",
              value === o.value ? "bg-primary text-primary-foreground" : "hover:bg-accent",
            )}
          >
            {o.label}
          </button>
        ))}
      </div>
    </div>
  );
}

export function TryPage() {
  const checks = useQuery({ queryKey: qk.checks, queryFn: api.checks, refetchInterval: 1500 });
  const runs = useQuery({ queryKey: qk.runs, queryFn: api.runs, refetchInterval: 1500 });
  const live = useLive();
  const data = checks.data;
  const [chosen, setChosen] = useState<NetworkState>({ sap: "up", source: "up", slowMotion: true });
  const [picked, setPicked] = useState("");

  const reset = useLabAction({ name: "Fresh start", run: () => api.resetWorkspace({ dataset: "Workbook" }), confirm: () => "The lab now holds the client's 120 requisitions. Nothing has been sent to SAP yet." });
  const applyNetwork = useLabAction<NetworkState>({ name: "Connections", run: (n) => api.setNetwork(n), confirm: (_r, n) => describeNetwork(n) });

  // Once known, the lab's connections win (someone may have reset it elsewhere). Speed is this page's own choice.
  const network: NetworkState = data && !applyNetwork.pending ? { ...chosen, sap: data.network.sap, source: data.network.source } : chosen;
  const start = useLabAction({
    name: "Start sync",
    run: async () => {
      await api.setNetwork(network);
      return api.sync();
    },
    confirm: () => "The sync has started. Watch the numbers, and pause it or pull the plug whenever you like.",
  });
  const pause = useLabAction({ name: "Pause", run: () => api.pause(), confirm: () => "Paused. Requests already on their way will finish, but nothing new is sent." });
  const resume = useLabAction({ name: "Resume", run: () => api.resume(), confirm: () => "Resumed. The sync carries on from where it stopped." });
  const crash = useLabAction({
    name: "Crash",
    run: async () => {
      await api.stop();
      await api.restart();
    },
    confirm: () => "The server was killed mid-sync and started again. Anything it was sending at that moment gets checked in SAP before it is sent again. Start the sync to finish.",
  });
  const duplicate = useLabAction<"resend" | "change" | "in-sap", { message: string }>({
    name: "Duplicate test",
    run: (how) => api.duplicate(picked, how),
    confirm: (r) => r.message,
  });

  const busy = [reset, applyNetwork, start, pause, resume, crash, duplicate].some((a) => a.pending);
  const running = data?.running ?? false;
  const paused = data?.paused ?? false;
  const last = [reset, applyNetwork, start, pause, resume, crash, duplicate].filter((a) => a.phase !== "idle").sort((a, b) => (b.at ?? 0) - (a.at ?? 0))[0];

  const change = (patch: Partial<NetworkState>) => {
    const next = { ...network, ...patch };
    setChosen(next);
    void applyNetwork.execute(next);
  };

  const lastRun = lastFinishedRun(runs.data);
  const counts = data?.counts;
  const done = counts ? counts.inSap : 0;
  const total = counts ? counts.inSap + counts.toGo : 0;
  const lines = collapse(
    httpEvents(live.events)
      .map(describe)
      .filter((l) => l !== null)
      .reverse(),
  ).slice(0, 14);

  return (
    <div className="space-y-4">
      <PageHeader
        title="Try it yourself"
        description="Run the real sync on the client's data and get in its way, like real life does: pause it, cut the connection, crash the server, send duplicates. The panel on the right always shows whether anything was lost or sent twice."
      />

      <div className="grid gap-4 lg:grid-cols-[minmax(0,1fr)_22rem]">
        <div className="space-y-4">
          <Step n={1} title="Start fresh" description="Loads the client's 120 requisitions, 7 of which are already in SAP. Do this whenever you want a clean slate.">
            <Button onClick={() => reset.execute()} disabled={busy || running} data-testid="try-reset">
              <RotateCcw className="size-4" aria-hidden />
              Load the client&apos;s data
            </Button>
          </Step>

          <Step n={2} title="Run the sync" description="Slow motion gives you time to step in while it runs.">
            <Choice
              label="Speed"
              value={network.slowMotion ? "slow" : "fast"}
              options={[
                { value: "slow", label: "Slow motion" },
                { value: "fast", label: "Full speed" },
              ]}
              onChange={(v) => change({ slowMotion: v === "slow" })}
              disabled={busy}
            />
            <div className="flex flex-wrap gap-2">
              <Button onClick={() => start.execute()} disabled={busy || running} data-testid="try-start">
                <Play className="size-4" aria-hidden />
                {done > 0 ? "Run the sync again" : "Start the sync"}
              </Button>
              {paused ? (
                <Button variant="secondary" onClick={() => resume.execute()} disabled={busy} data-testid="try-resume">
                  <Play className="size-4" aria-hidden />
                  Resume
                </Button>
              ) : (
                <Button variant="secondary" onClick={() => pause.execute()} disabled={busy || !running} data-testid="try-pause">
                  <Pause className="size-4" aria-hidden />
                  Pause
                </Button>
              )}
            </div>
            <Progress done={done} total={total} running={running} paused={paused} />
            {lastRun ? (
              <p className="text-sm text-muted-foreground" data-testid="try-last-run">
                {describeLastRun(lastRun)}
              </p>
            ) : null}
          </Step>

          <Step n={3} title="Cause trouble" description="Do this at any time, even while the sync is running or paused.">
            <Choice<Connection>
              label="Connection to SAP"
              value={network.sap}
              options={[
                { value: "up", label: "Working" },
                { value: "unreliable", label: "Unreliable" },
                { value: "down", label: "Down" },
              ]}
              onChange={(v) => change({ sap: v })}
              disabled={busy}
            />
            <Choice<Connection>
              label="Connection to the source"
              value={network.source}
              options={[
                { value: "up", label: "Working" },
                { value: "down", label: "Down" },
              ]}
              onChange={(v) => change({ source: v })}
              disabled={busy}
            />
            <div className="flex flex-wrap items-center gap-2">
              <span className="w-44 text-sm text-muted-foreground">The server</span>
              <Button variant="outline" onClick={() => crash.execute()} disabled={busy} data-testid="try-crash">
                <Power className="size-4" aria-hidden />
                Crash it now
              </Button>
            </div>
            <div className="space-y-2 rounded-md border p-3">
              <div className="text-sm font-medium">Duplicates</div>
              <NativeSelect aria-label="Requisition for the duplicate test" value={picked} onChange={(e) => setPicked(e.target.value)} className="w-full sm:w-80">
                <option value="">Choose a requisition</option>
                {(data?.requisitions ?? []).map((r) => (
                  <option key={r.id} value={r.id}>
                    {r.id} ({r.state})
                  </option>
                ))}
              </NativeSelect>
              <div className="flex flex-wrap gap-2">
                <Button size="sm" variant="outline" disabled={busy || !picked} onClick={() => duplicate.execute("resend")}>
                  Send it again, unchanged
                </Button>
                <Button size="sm" variant="outline" disabled={busy || !picked} onClick={() => duplicate.execute("change")}>
                  Send it again with a change
                </Button>
                <Button size="sm" variant="outline" disabled={busy || !picked} onClick={() => duplicate.execute("in-sap")}>
                  Someone creates it in SAP by hand
                </Button>
              </div>
              <p className="text-xs text-muted-foreground">Then run the sync again and check the panel: SAP must still hold one document per requisition.</p>
            </div>
          </Step>

          {last ? <ActionStatus phase={last.phase} message={last.message} /> : null}

          <Card>
            <CardHeader className="pb-2">
              <CardTitle className="text-base">What the integration is doing</CardTitle>
              <CardDescription>Its latest messages to SAP and to the source system, newest first.</CardDescription>
            </CardHeader>
            <CardContent>
              {lines.length === 0 ? (
                <p className="text-sm text-muted-foreground">Nothing yet. Start the sync to see it work.</p>
              ) : (
                <ol className="space-y-1 text-sm" data-testid="try-activity">
                  {lines.map((l) => (
                    <li key={l.seq} className="flex gap-3">
                      <time className="shrink-0 font-mono text-xs text-muted-foreground" dateTime={l.at}>
                        {new Date(l.at).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" })}
                      </time>
                      <span className={cn(l.tone === "bad" && "text-red-700 dark:text-red-300", l.tone === "warn" && "text-amber-700 dark:text-amber-300", l.tone === "ok" && "text-emerald-700 dark:text-emerald-300")}>
                        {l.text}
                        {l.times > 1 ? <span className="text-muted-foreground"> ({l.times} times)</span> : null}
                      </span>
                    </li>
                  ))}
                </ol>
              )}
            </CardContent>
          </Card>
        </div>

        <div className="order-first lg:order-none lg:sticky lg:top-4 lg:self-start">
          <Step n={4} title="Did it work?" description="Checked against SAP's own records while the sync runs.">
            {data ? <Verdict data={data} /> : <p className="text-sm text-muted-foreground">Reading the lab...</p>}
          </Step>
        </div>
      </div>
    </div>
  );
}

function describeNetwork(n: NetworkState): string {
  const sap = n.sap === "up" ? "working" : n.sap === "down" ? "down" : "unreliable";
  const source = n.source === "up" ? "working" : "down";
  return `Connection to SAP: ${sap}. Connection to the source: ${source}. Speed: ${n.slowMotion ? "slow motion" : "full speed"}.`;
}

function Progress({ done, total, running, paused }: { done: number; total: number; running: boolean; paused: boolean }) {
  const pct = total > 0 ? Math.round((done / total) * 100) : 0;
  const label = running ? (paused ? "Paused" : "Running") : total > 0 && done === total ? "Finished" : done > 0 ? "Stopped" : "Not started";
  return (
    <div data-testid="try-progress" data-state={label}>
      <div className="mb-1 flex justify-between text-sm">
        <span className="font-medium">{label}</span>
        <span className="tabular-nums text-muted-foreground">
          {done} of {total} in SAP
        </span>
      </div>
      <div className="h-2 overflow-hidden rounded-full bg-muted" role="progressbar" aria-valuemin={0} aria-valuemax={total} aria-valuenow={done} aria-label="Requisitions in SAP">
        <div className={cn("h-full transition-all", paused ? "bg-amber-500" : "bg-primary")} style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}

function Verdict({ data }: { data: HandsOnChecks }) {
  const c = data.counts;
  const rows: [string, number, string?][] = [
    ["In SAP", c.inSap],
    ["Still to send", c.toGo, c.toGo > 0 ? "text-amber-700 dark:text-amber-300" : undefined],
    ["Being checked in SAP", c.checking],
    ["Held back (bad data)", c.quarantined],
    ["Waiting for approval", c.skipped],
    ["Duplicates in SAP", c.duplicates, c.duplicates > 0 ? "text-red-700 dark:text-red-300" : "text-emerald-700 dark:text-emerald-300"],
  ];
  const Icon = data.verdict.tone === "success" ? CheckCircle2 : data.verdict.tone === "danger" ? XCircle : CircleDashed;
  return (
    <div className="space-y-4" data-testid="try-verdict" data-tone={data.verdict.tone}>
      <div className={cn("rounded-md border px-3 py-2", VERDICT_STYLE[data.verdict.tone])} role="status" aria-live="polite">
        <div className="flex items-center gap-2 font-semibold">
          <Icon className="size-4" aria-hidden />
          {data.verdict.title}
        </div>
        <p className="mt-1 text-sm">{data.verdict.text}</p>
      </div>
      <dl className="grid grid-cols-2 gap-2 text-sm">
        {rows.map(([label, value, cls]) => (
          <div key={label} className="rounded-md border px-2 py-1.5" data-metric={label}>
            <dt className="text-xs text-muted-foreground">{label}</dt>
            <dd className={cn("text-lg font-semibold tabular-nums", cls)}>{value}</dd>
          </div>
        ))}
      </dl>
      <ul className="space-y-1.5 text-sm">
        {data.checks.map((ch) => (
          <li key={ch.label} className="flex items-start gap-2" data-ok={ch.ok}>
            {ch.ok ? <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-emerald-600" aria-label="passed" /> : <XCircle className="mt-0.5 size-4 shrink-0 text-red-600" aria-label="failed" />}
            <span>
              <span className="font-medium">{ch.label}.</span> <span className="text-muted-foreground">{ch.detail}</span>
            </span>
          </li>
        ))}
      </ul>
      <div className="flex flex-wrap gap-2">
        <Button asChild size="sm" variant="outline">
          <Link href="/requisitions">See every requisition</Link>
        </Button>
        <Button asChild size="sm" variant="outline">
          <Link href="/reconciliation">Full report</Link>
        </Button>
      </div>
    </div>
  );
}
