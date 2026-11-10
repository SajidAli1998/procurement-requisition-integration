import { cn } from "@/lib/utils";
import type { Tone } from "@/lib/status";

const TONE_BAR: Record<Tone, string> = {
  success: "bg-emerald-500",
  info: "bg-sky-500",
  accent: "bg-violet-500",
  danger: "bg-red-500",
  warning: "bg-amber-500",
  critical: "bg-orange-500",
  muted: "bg-slate-400",
  neutral: "bg-border",
};

export function StatCard({ label, value, hint, tone = "neutral", testId }: { label: string; value: React.ReactNode; hint?: React.ReactNode; tone?: Tone; testId?: string }) {
  return (
    <div className="relative overflow-hidden rounded-lg border bg-card p-3 shadow-sm" data-testid={testId}>
      <span aria-hidden className={cn("absolute inset-y-0 left-0 w-1", TONE_BAR[tone])} />
      <div className="pl-1.5">
        <div className="text-xs font-medium text-muted-foreground">{label}</div>
        <div className="mt-1 text-2xl font-semibold tabular-nums" data-value>
          {value}
        </div>
        {hint ? <div className="mt-0.5 text-xs text-muted-foreground">{hint}</div> : null}
      </div>
    </div>
  );
}
