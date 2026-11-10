"use client";

import { ArrowRight, Database, KeyRound, Search, PlusCircle } from "lucide-react";
import { cn } from "@/lib/utils";
import type { LayerKey } from "./layers";

const STEPS: { key: LayerKey; n: string; title: string; icon: React.ComponentType<{ className?: string }>; text: string }[] = [
  {
    key: "mapping",
    n: "1",
    title: "Local mapping",
    icon: Database,
    text: "The integration database already maps this requisition to an SAP number: changes go as an update, never a second create.",
  },
  {
    key: "idempotency",
    n: "2",
    title: "Idempotency-Key",
    icon: KeyRound,
    text: "Every create carries a deterministic key. If SAP already saw it, SAP replays the original answer instead of creating again.",
  },
  {
    key: "lookup",
    n: "3",
    title: "SAP lookup before create",
    icon: Search,
    text: "Before creating, the integration searches SAP by ExternalReference. A match is linked; several matches stop it (nothing linked, nothing created).",
  },
];

export function Protections({ counts, loaded, total }: { counts: Record<LayerKey, number>; loaded: number; total: number }) {
  return (
    <section aria-labelledby="protections-title" className="rounded-lg border bg-card p-4 shadow-sm">
      <h2 id="protections-title" className="text-sm font-semibold">
        Three protections against duplicate SAP requisitions
      </h2>
      <p className="mt-1 text-xs text-muted-foreground">
        Counts come from each record&apos;s duplicateDecision in the lab ({loaded} of {total} decisions loaded). A record is decided by the first protection that applies.
      </p>
      <ol className="mt-3 flex flex-col items-stretch gap-2 lg:flex-row lg:items-center">
        {STEPS.map((s, i) => {
          const Icon = s.icon;
          const n = counts[s.key] + (s.key === "lookup" ? counts.ambiguous : 0);
          return (
            <li key={s.key} className="flex flex-1 flex-col gap-2 lg:flex-row lg:items-center">
              <div className={cn("flex-1 rounded-md border p-3", n > 0 && "border-primary/50 bg-primary/5")} data-testid={`protection-${s.key}`}>
                <div className="flex items-center gap-2">
                  <span className="flex size-6 items-center justify-center rounded-full bg-primary text-xs font-bold text-primary-foreground">{s.n}</span>
                  <Icon className="size-4 text-primary" aria-hidden />
                  <span className="text-sm font-medium">{s.title}</span>
                  <span className="ml-auto text-lg font-semibold tabular-nums" aria-label={`${n} records decided by ${s.title}`}>
                    {n}
                  </span>
                </div>
                <p className="mt-1 text-xs text-muted-foreground">{s.text}</p>
                {s.key === "lookup" && counts.ambiguous > 0 ? (
                  <p className="mt-1 text-xs font-medium text-orange-700 dark:text-orange-300">{counts.ambiguous} ambiguous: several SAP documents, none linked.</p>
                ) : null}
              </div>
              {i < STEPS.length - 1 ? <ArrowRight className="mx-auto size-4 rotate-90 text-muted-foreground lg:rotate-0" aria-hidden /> : null}
            </li>
          );
        })}
        <li className="flex flex-1 flex-col gap-2 lg:flex-row lg:items-center">
          <ArrowRight className="mx-auto size-4 rotate-90 text-muted-foreground lg:rotate-0" aria-hidden />
          <div className="flex-1 rounded-md border border-dashed p-3" data-testid="protection-created">
            <div className="flex items-center gap-2">
              <PlusCircle className="size-4 text-emerald-600" aria-hidden />
              <span className="text-sm font-medium">Created once</span>
              <span className="ml-auto text-lg font-semibold tabular-nums">{counts.created}</span>
            </div>
            <p className="mt-1 text-xs text-muted-foreground">Nothing found by any protection: created with the deterministic key.</p>
          </div>
        </li>
      </ol>
    </section>
  );
}
