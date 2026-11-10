"use client";

import { Wand2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { ToneBadge } from "@/components/status-badge";
import { ActionStatus } from "@/components/action-status";
import { useLabAction } from "@/hooks/use-lab-action";
import { api, qk } from "@/lib/api";
import { DEFAULT_FAULT_SETTINGS, FAULT_PRESETS, type FaultPreset } from "@/lib/faults";
import type { FaultSettings } from "@/lib/types";
import { TARGET_LABEL } from "./fault-state-panel";

export function Presets({ currentSettings }: { currentSettings: { sap: FaultSettings | null; source: FaultSettings | null } }) {
  const action = useLabAction<FaultPreset, unknown>({
    name: "Apply preset",
    run: async (p) => {
      if (p.script) return api.scriptFaults(p.target, p.script);
      const base = currentSettings[p.target] ?? DEFAULT_FAULT_SETTINGS;
      return api.putFaults(p.target, { ...base, ...p.settings });
    },
    invalidate: [qk.workspace],
    confirm: (_r, p) => `"${p.name}" applied to the ${TARGET_LABEL[p.target]} mock. Run a sync to see its effect.`,
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Presets</CardTitle>
        <CardDescription>Common production failures, as exact scripts or settings. Applying one adds to what is already queued; use Clear all to start over.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <ul className="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">
          {FAULT_PRESETS.map((p) => (
            <li key={p.id} className="flex flex-col gap-2 rounded-md border p-3">
              <div className="flex flex-wrap items-center gap-2">
                <span className="text-sm font-medium">{p.name}</span>
                <ToneBadge tone="neutral">{TARGET_LABEL[p.target]}</ToneBadge>
                <ToneBadge tone="muted">{p.script ? "script" : "settings"}</ToneBadge>
              </div>
              <p className="flex-1 text-xs text-muted-foreground">{p.description}</p>
              <Button size="sm" variant="outline" disabled={action.pending} onClick={() => void action.execute(p)} aria-label={`Apply preset ${p.name}`}>
                <Wand2 /> Apply
              </Button>
            </li>
          ))}
        </ul>
        <ActionStatus phase={action.phase} message={action.message} />
      </CardContent>
    </Card>
  );
}
