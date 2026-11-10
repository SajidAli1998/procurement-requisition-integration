"use client";

import Link from "next/link";
import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { ArrowRight, CheckCircle2, Cloud, Database, Loader2, Server, ShieldCheck, WifiOff, XCircle } from "lucide-react";
import { ActionStatus } from "@/components/action-status";
import { PageHeader } from "@/components/page-header";
import { QueryState } from "@/components/query-state";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { useLabAction } from "@/hooks/use-lab-action";
import { api, qk } from "@/lib/api";
import type { WhatIfNumber, WhatIfSituation, WhatIfStory } from "@/lib/types";
import { cn } from "@/lib/utils";

const GROUP_ICON: Record<string, React.ComponentType<{ className?: string }>> = {
  Connection: WifiOff,
  SAP: Cloud,
  Server: Server,
  Data: Database,
};

const TONE: Record<WhatIfNumber["tone"], string> = {
  success: "text-emerald-700 dark:text-emerald-300",
  warning: "text-amber-700 dark:text-amber-300",
  danger: "text-red-700 dark:text-red-300",
  neutral: "text-foreground",
};

export function storyIsSafe(story: WhatIfStory): boolean {
  return story.checks.length > 0 && story.checks.every((c) => c.ok);
}

export function WhatIfPage() {
  const situations = useQuery({ queryKey: qk.whatIf, queryFn: api.whatIfSituations });
  const [story, setStory] = useState<WhatIfStory | null>(null);
  const [running, setRunning] = useState<string | null>(null);

  const action = useLabAction<{ id: string; title: string }, WhatIfStory>({
    name: "Situation",
    run: ({ id }) => api.runWhatIf(id),
    confirm: (s) => `${s.title}: ${s.headline}`,
    onConfirmed: (s) => setStory(s),
  });

  async function start(id: string, title: string) {
    setRunning(id);
    setStory(null);
    await action.execute({ id, title });
    setRunning(null);
  }

  return (
    <div>
      <PageHeader
        title="What if"
        description="Pick a real-world problem and see what happens. Each one resets the lab to the client's data, causes the problem, runs the real integration through it and explains the result in plain words. The checks underneath show that nothing was lost or sent twice."
      />

      <QueryState query={situations} title="situations">
        {(list) => (
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3" data-testid="whatif-list">
            {list.map((s) => (
              <SituationCard key={s.id} s={s} busy={action.pending} running={running === s.id} onRun={() => start(s.id, s.title)} />
            ))}
          </div>
        )}
      </QueryState>

      <div className="mt-4">
        <ActionStatus phase={action.phase} message={action.phase === "sending" ? "Running it in the lab. Setting up, syncing and checking SAP can take up to a minute." : action.message} />
      </div>

      {story ? <StoryView story={story} busy={action.pending} onNext={(id, label) => start(id, label)} /> : null}
    </div>
  );
}

function SituationCard({ s, busy, running, onRun }: { s: WhatIfSituation; busy: boolean; running: boolean; onRun: () => void }) {
  const Icon = GROUP_ICON[s.group] ?? ShieldCheck;
  return (
    <Card data-testid={`whatif-${s.id}`} className={cn(running && "ring-2 ring-primary")}>
      <CardHeader className="pb-2">
        <div className="flex items-center gap-2 text-xs font-medium uppercase tracking-wide text-muted-foreground">
          <Icon className="size-4" aria-hidden />
          {s.group}
        </div>
        <CardTitle className="text-base">{s.title}</CardTitle>
        <CardDescription>{s.description}</CardDescription>
      </CardHeader>
      <CardContent>
        <Button onClick={onRun} disabled={busy} aria-label={`Try: ${s.title}`}>
          {running ? <Loader2 className="size-4 animate-spin" aria-hidden /> : null}
          {running ? "Running..." : "Try it"}
        </Button>
      </CardContent>
    </Card>
  );
}

function StoryView({ story, busy, onNext }: { story: WhatIfStory; busy: boolean; onNext: (id: string, label: string) => void }) {
  const safe = storyIsSafe(story);
  return (
    <Card className="mt-4" data-testid="whatif-story" data-safe={safe}>
      <CardHeader>
        <div
          className={cn(
            "flex items-start gap-2 rounded-md border px-3 py-2 text-sm font-semibold",
            safe
              ? "border-emerald-600/30 bg-emerald-50 text-emerald-900 dark:bg-emerald-950/40 dark:text-emerald-100"
              : "border-red-600/40 bg-red-50 text-red-950 dark:bg-red-950/40 dark:text-red-100",
          )}
          data-testid="whatif-verdict"
        >
          {safe ? <CheckCircle2 className="mt-0.5 size-4 shrink-0" aria-hidden /> : <XCircle className="mt-0.5 size-4 shrink-0" aria-hidden />}
          {story.verdict}
        </div>
        <CardTitle className="mt-3 text-lg">{story.title}</CardTitle>
        <CardDescription data-testid="whatif-headline">{story.headline}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-5">
        <section aria-labelledby="whatif-happened">
          <h2 id="whatif-happened" className="mb-2 text-sm font-semibold">
            What happened
          </h2>
          <div className="space-y-2 text-sm leading-relaxed">
            {story.story.map((p, i) => (
              <p key={i} className="break-anywhere">
                {p}
              </p>
            ))}
          </div>
          {story.next ? (
            <Button className="mt-3" onClick={() => onNext(story.next!.id, story.next!.label)} disabled={busy} data-testid="whatif-next">
              {story.next.label}
              <ArrowRight className="size-4" aria-hidden />
            </Button>
          ) : null}
        </section>

        <section aria-labelledby="whatif-numbers">
          <h2 id="whatif-numbers" className="mb-2 text-sm font-semibold">
            The numbers
          </h2>
          <div className="overflow-x-auto">
            <table className="w-full text-sm" data-testid="whatif-numbers">
              <thead>
                <tr className="border-b text-left text-muted-foreground">
                  <th scope="col" className="py-1 pr-4 font-medium">
                    &nbsp;
                  </th>
                  <th scope="col" className="py-1 pr-4 text-right font-medium">
                    Before
                  </th>
                  <th scope="col" className="py-1 text-right font-medium">
                    After
                  </th>
                </tr>
              </thead>
              <tbody>
                {story.numbers.map((n) => (
                  <tr key={n.label} className="border-b last:border-0" data-metric={n.label}>
                    <th scope="row" className="py-1 pr-4 text-left font-normal">
                      {n.label}
                    </th>
                    <td className="py-1 pr-4 text-right tabular-nums text-muted-foreground">{n.before ?? "-"}</td>
                    <td className={cn("py-1 text-right font-semibold tabular-nums", TONE[n.tone])} data-col="after">
                      {n.after}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>

        <section aria-labelledby="whatif-checks">
          <h2 id="whatif-checks" className="mb-2 text-sm font-semibold">
            Checked against SAP and the integration&apos;s records
          </h2>
          <ul className="space-y-1.5 text-sm" data-testid="whatif-checks">
            {story.checks.map((c) => (
              <li key={c.label} className="flex items-start gap-2" data-ok={c.ok}>
                {c.ok ? (
                  <CheckCircle2 className="mt-0.5 size-4 shrink-0 text-emerald-600" aria-label="passed" />
                ) : (
                  <XCircle className="mt-0.5 size-4 shrink-0 text-red-600" aria-label="failed" />
                )}
                <span>
                  <span className="font-medium">{c.label}.</span> <span className="text-muted-foreground">{c.detail}</span>
                </span>
              </li>
            ))}
          </ul>
        </section>

        <details className="text-sm">
          <summary className="cursor-pointer font-semibold">Step by step</summary>
          <ol className="mt-2 space-y-1">
            {story.timeline.map((t, i) => (
              <li key={i} className="flex gap-3">
                <time className="shrink-0 font-mono text-xs text-muted-foreground" dateTime={t.at}>
                  {new Date(t.at).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" })}
                </time>
                <span className="break-anywhere">{t.text}</span>
              </li>
            ))}
          </ol>
        </details>

        {story.links.length > 0 ? (
          <nav aria-label="See the evidence" className="flex flex-wrap gap-2 text-sm">
            {story.links.map((l) => (
              <Button key={l.href + l.label} asChild variant="outline" size="sm">
                <Link href={l.href}>{l.label}</Link>
              </Button>
            ))}
            <Button asChild variant="outline" size="sm">
              <Link href="/">Dashboard</Link>
            </Button>
          </nav>
        ) : null}
      </CardContent>
    </Card>
  );
}
