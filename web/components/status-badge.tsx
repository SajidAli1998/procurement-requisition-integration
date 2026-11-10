import { AlertTriangle, Ban, CheckCircle2, CircleDashed, CircleHelp, Clock, Link2, Loader2, PencilLine, PlusCircle, ShieldAlert, XCircle } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { httpStatusTone, invariantTone, outcomeStyle, scenarioStatus, type OutcomeCategory, type Tone } from "@/lib/status";
import { cn } from "@/lib/utils";

const ICONS: Record<OutcomeCategory, React.ComponentType<{ className?: string }>> = {
  created: PlusCircle,
  linked: Link2,
  updated: PencilLine,
  quarantined: ShieldAlert,
  skipped: Ban,
  pending: Clock,
  retrying: Loader2,
  unresolved: CircleHelp,
  resolved: CheckCircle2,
  "not-processed": CircleDashed,
  unknown: CircleHelp,
};

export function OutcomeBadge({ outcome, className }: { outcome: string | null | undefined; className?: string }) {
  const s = outcomeStyle(outcome);
  const Icon = ICONS[s.category];
  return (
    <Badge tone={s.tone} className={className} title={s.description} data-outcome={s.category} data-synchronised={s.synchronised}>
      <Icon aria-hidden />
      {s.label}
    </Badge>
  );
}

export function ToneBadge({ tone, children, className, title }: { tone: Tone; children: React.ReactNode; className?: string; title?: string }) {
  return (
    <Badge tone={tone} className={className} title={title}>
      {children}
    </Badge>
  );
}

export function ScenarioStatusBadge({ status, className }: { status: string | null | undefined; className?: string }) {
  const s = scenarioStatus(status);
  const Icon = s.label === "Pass" ? CheckCircle2 : s.label === "Fail" ? XCircle : s.label === "Blocked" ? AlertTriangle : CircleDashed;
  return (
    <Badge tone={s.tone} className={className} data-status={s.label}>
      <Icon aria-hidden />
      {s.label}
    </Badge>
  );
}

export function InvariantBadge({ outcome }: { outcome: string }) {
  const label = outcome === "NotApplicable" ? "n/a" : outcome;
  return <Badge tone={invariantTone(outcome)}>{label}</Badge>;
}

export function HttpStatusBadge({ status, className }: { status: number | null | undefined; className?: string }) {
  return (
    <Badge tone={httpStatusTone(status)} className={cn("font-mono", className)}>
      {status ? status : "no response"}
    </Badge>
  );
}
