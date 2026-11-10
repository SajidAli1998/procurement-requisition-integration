"use client";

import { CheckCircle2, CircleHelp, Loader2, XCircle } from "lucide-react";
import type { ActionPhase } from "@/hooks/use-lab-action";
import { cn } from "@/lib/utils";

const PHASE: Record<Exclude<ActionPhase, "idle">, { label: string; className: string; icon: React.ComponentType<{ className?: string }> }> = {
  sending: { label: "Pending", className: "border-sky-600/30 bg-sky-50 text-sky-900 dark:bg-sky-950/40 dark:text-sky-100", icon: Loader2 },
  confirming: { label: "Pending confirmation", className: "border-sky-600/30 bg-sky-50 text-sky-900 dark:bg-sky-950/40 dark:text-sky-100", icon: Loader2 },
  confirmed: { label: "Confirmed by lab", className: "border-emerald-600/30 bg-emerald-50 text-emerald-900 dark:bg-emerald-950/40 dark:text-emerald-100", icon: CheckCircle2 },
  unknown: { label: "Unknown outcome", className: "border-orange-600/40 bg-orange-50 text-orange-950 dark:bg-orange-950/40 dark:text-orange-100", icon: CircleHelp },
  failed: { label: "Failed", className: "border-red-600/40 bg-red-50 text-red-950 dark:bg-red-950/40 dark:text-red-100", icon: XCircle },
};

export function ActionStatus({ phase, message, className }: { phase: ActionPhase; message?: string; className?: string }) {
  if (phase === "idle") return <div aria-live="polite" className="sr-only" />;
  const p = PHASE[phase];
  const Icon = p.icon;
  return (
    <div
      aria-live="polite"
      role="status"
      data-testid="action-status"
      data-phase={phase}
      className={cn("flex items-start gap-2 rounded-md border px-3 py-2 text-sm", p.className, className)}
    >
      <Icon className={cn("mt-0.5 size-4 shrink-0", (phase === "sending" || phase === "confirming") && "animate-spin")} aria-hidden />
      <div className="min-w-0">
        <span className="font-semibold">{p.label}.</span> <span className="break-anywhere">{message}</span>
      </div>
    </div>
  );
}
