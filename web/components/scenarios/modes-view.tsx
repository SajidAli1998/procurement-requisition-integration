"use client";

import { Play } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import type { Mode, Tally } from "@/lib/types";

const MODE_DESCRIPTIONS: Record<string, string> = {
  Baseline: "The client workbook through the real pipeline, compared row by row",
  Functional: "Happy paths and normal business behaviour",
  Negative: "Invalid data and rule violations",
  DuplicateStress: "Duplicates, idempotency and resends",
  FailureSimulation: "SAP and source failures injected",
  Recovery: "Crashes, restarts and resumption",
  Security: "Authorization, injection and isolation",
  Performance: "Volume, latency and throughput",
};

function Row({ label, tally, description, action }: { label: string; tally?: Tally; description?: string; action?: React.ReactNode }) {
  const t = tally;
  return (
    <tr className="border-b last:border-0" data-testid={`mode-row-${label.replace(/\s+/g, "-")}`}>
      <th scope="row" className="px-2 py-1.5 text-left align-top font-medium">
        <div>{label}</div>
        {description ? <div className="text-xs font-normal text-muted-foreground">{description}</div> : null}
      </th>
      <td className="px-2 py-1.5 text-right tabular-nums">{t?.total ?? 0}</td>
      <td className="px-2 py-1.5 text-right tabular-nums">{t?.executed ?? 0}</td>
      <td className="px-2 py-1.5 text-right tabular-nums text-emerald-700 dark:text-emerald-400">{t?.passed ?? 0}</td>
      <td className="px-2 py-1.5 text-right tabular-nums text-red-700 dark:text-red-400">{t?.failed ?? 0}</td>
      <td className="px-2 py-1.5 text-right tabular-nums text-amber-700 dark:text-amber-400">{t?.blocked ?? 0}</td>
      <td className="px-2 py-1.5 text-right tabular-nums">{t?.notImplemented ?? 0}</td>
      <td className="px-2 py-1.5 text-right tabular-nums">{t?.notRun ?? 0}</td>
      <td className="px-2 py-1.5 text-right">{action}</td>
    </tr>
  );
}

export function ModesView({
  modes,
  byMode,
  overall,
  byCategory,
  source,
  onRunMode,
  onRunAll,
  disabled,
}: {
  modes: Mode[];
  byMode: Record<string, Tally>;
  overall: Tally;
  byCategory: Record<string, Tally>;
  source: string;
  onRunMode: (m: Mode) => void;
  onRunAll: () => void;
  disabled: boolean;
}) {
  const head = (
    <thead className="bg-muted/40 text-xs text-muted-foreground">
      <tr className="border-b">
        <th scope="col" className="px-2 py-1.5 text-left">Name</th>
        <th scope="col" className="px-2 py-1.5 text-right">Total</th>
        <th scope="col" className="px-2 py-1.5 text-right">Executed</th>
        <th scope="col" className="px-2 py-1.5 text-right">Passed</th>
        <th scope="col" className="px-2 py-1.5 text-right">Failed</th>
        <th scope="col" className="px-2 py-1.5 text-right">Blocked</th>
        <th scope="col" className="px-2 py-1.5 text-right">Not impl.</th>
        <th scope="col" className="px-2 py-1.5 text-right">Not run</th>
        <th scope="col" className="px-2 py-1.5 text-right"><span className="sr-only">Action</span></th>
      </tr>
    </thead>
  );
  return (
    <div className="flex flex-col gap-4">
      <p className="text-xs text-muted-foreground">
        Counts from <Badge tone="neutral">{source}</Badge>. Executed = passed + failed; blocked and not implemented are never counted as passed.
      </p>
      <div className="overflow-x-auto rounded-md border">
        <table className="w-full text-sm" aria-label="Execution modes">
          {head}
          <tbody>
            {modes.map((m) => (
              <Row
                key={m}
                label={m}
                tally={byMode[m]}
                description={MODE_DESCRIPTIONS[m]}
                action={
                  <Button size="sm" variant="outline" onClick={() => onRunMode(m)} disabled={disabled} aria-label={`Run ${m} mode`}>
                    <Play /> Run
                  </Button>
                }
              />
            ))}
            <Row
              label="Full regression"
              tally={overall}
              description="Every scenario in every mode"
              action={
                <Button size="sm" onClick={onRunAll} disabled={disabled} aria-label="Run full regression">
                  <Play /> Run all
                </Button>
              }
            />
          </tbody>
        </table>
      </div>
      <div className="overflow-x-auto rounded-md border">
        <table className="w-full text-sm" aria-label="By category">
          {head}
          <tbody>
            {Object.entries(byCategory).map(([c, t]) => (
              <Row key={c} label={c} tally={t} />
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
