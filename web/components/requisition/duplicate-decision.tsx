import { CheckCircle2, Circle, Fingerprint, KeyRound, Search } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import type { DuplicateDecision } from "@/lib/types";
import { cn } from "@/lib/utils";
import { decidingLayer } from "./outcome";

const LAYERS = [
  { n: 1 as const, name: "Local mapping", icon: Fingerprint, text: "The integration database already links this requisition to a SAP number, so changes go as updates." },
  { n: 2 as const, name: "Idempotency-Key", icon: KeyRound, text: "A deterministic key per requisition version; SAP replays the original answer instead of creating again." },
  { n: 3 as const, name: "SAP lookup before create", icon: Search, text: "SAP is searched by ExternalReference before any create; an existing document is linked." },
];

export function DuplicateDecisionPanel({ decision }: { decision: DuplicateDecision }) {
  const deciding = decidingLayer(decision.layer);
  return (
    <Card data-testid="duplicate-decision">
      <CardHeader>
        <CardTitle>Duplicate-detection decision</CardTitle>
        <CardDescription>
          Decided by: <span className="font-medium text-foreground">{decision.layer}</span> · SAP creates by the integration:{" "}
          <span className="font-medium text-foreground tabular-nums" data-testid="created-count">
            {decision.created}
          </span>
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <p className="text-sm break-anywhere">{decision.detail}</p>
        <ol className="grid gap-2 md:grid-cols-3">
          {LAYERS.map((l) => {
            const active = deciding === l.n;
            const Icon = l.icon;
            return (
              <li
                key={l.n}
                data-active={active}
                className={cn("rounded-md border p-3 text-sm", active ? "border-primary bg-primary/5 ring-1 ring-primary" : "bg-muted/30")}
              >
                <div className="flex items-center gap-2 font-medium">
                  <Icon className="size-4 text-primary" aria-hidden />
                  {l.n}. {l.name}
                  {active ? (
                    <span className="ml-auto inline-flex items-center gap-1 text-xs text-primary">
                      <CheckCircle2 className="size-3.5" aria-hidden /> decided
                    </span>
                  ) : (
                    <Circle className="ml-auto size-3.5 text-muted-foreground" aria-hidden />
                  )}
                </div>
                <p className="mt-1 text-xs text-muted-foreground">{l.text}</p>
              </li>
            );
          })}
        </ol>
        {deciding === null ? (
          <p className="text-xs text-muted-foreground">
            No protection had to intervene for this record ({decision.layer}). A record created once shows a create count of 1; anything above 1 would be a duplicate.
          </p>
        ) : null}
        {decision.created > 1 ? (
          <p role="alert" className="text-sm font-semibold text-red-700 dark:text-red-300">
            The integration created this requisition in SAP {decision.created} times: duplicate.
          </p>
        ) : null}
      </CardContent>
    </Card>
  );
}
