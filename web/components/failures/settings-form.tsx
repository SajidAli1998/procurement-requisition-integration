"use client";

import { useEffect } from "react";
import { Controller, useForm, type UseFormRegisterReturn } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Save } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { ActionStatus } from "@/components/action-status";
import { useLabAction } from "@/hooks/use-lab-action";
import { api, qk } from "@/lib/api";
import { DEFAULT_FAULT_SETTINGS } from "@/lib/faults";
import type { FaultSettings } from "@/lib/types";
import { TARGET_LABEL, type Target } from "./fault-state-panel";

const rate = (label: string) =>
  z.number({ error: `${label} must be a number` }).min(0, `${label} must be between 0 and 1`).max(1, `${label} must be between 0 and 1`);

const settingsSchema = z.object({
  transientErrorRate: rate("Transient error rate"),
  throttleRate: rate("Throttle rate"),
  throttleRetryAfterSeconds: z.number({ error: "Retry-After must be a number" }).int("Whole seconds").min(0).max(3600),
  lostResponseRate: rate("Lost response rate"),
  latencyMs: z.number({ error: "Latency must be a number" }).int("Whole milliseconds").min(0).max(60_000, "At most 60000 ms"),
  pageOverlap: z.boolean(),
  honorIdempotencyKey: z.boolean(),
  referenceSearchLag: z.boolean(),
  seed: z.number({ error: "Seed must be a number" }).int("Whole number"),
});

function NumberField({
  id,
  label,
  hint,
  error,
  step,
  register,
}: {
  id: string;
  label: string;
  hint?: string;
  error?: string;
  step?: string;
  register: UseFormRegisterReturn;
}) {
  return (
    <div className="space-y-1">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} type="number" inputMode="decimal" step={step} aria-invalid={!!error} aria-describedby={hint ? `${id}-hint` : undefined} {...register} />
      {hint ? (
        <p id={`${id}-hint`} className="text-[11px] text-muted-foreground">
          {hint}
        </p>
      ) : null}
      {error ? <p className="text-xs text-destructive">{error}</p> : null}
    </div>
  );
}

export function SettingsForm({ target, current }: { target: Target; current: FaultSettings | null }) {
  const form = useForm<FaultSettings>({ resolver: zodResolver(settingsSchema), defaultValues: current ?? DEFAULT_FAULT_SETTINGS });
  const { register, handleSubmit, reset, control, formState } = form;

  // Follow the lab's state unless the user is editing.
  useEffect(() => {
    if (!formState.isDirty) reset(current ?? DEFAULT_FAULT_SETTINGS);
  }, [current, target, reset, formState.isDirty]);

  const action = useLabAction<FaultSettings, unknown>({
    name: `Apply ${TARGET_LABEL[target]} settings`,
    run: (s) => api.putFaults(target, s),
    invalidate: [qk.workspace],
    confirm: () => `${TARGET_LABEL[target]} settings stored by the mock; the state panel shows what it now reports.`,
    onConfirmed: (_r, s) => reset(s),
  });

  const err = formState.errors;
  const num = { valueAsNumber: true } as const;
  return (
    <Card>
      <CardHeader>
        <CardTitle>Random faults: {TARGET_LABEL[target]}</CardTitle>
        <CardDescription>Probabilities between 0 and 1, applied per request. The seed makes a chaos run reproducible.</CardDescription>
      </CardHeader>
      <CardContent>
        <form className="space-y-3" onSubmit={handleSubmit((v) => void action.execute(v))} noValidate aria-label={`${TARGET_LABEL[target]} random fault settings`}>
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            <NumberField id={`${target}-transient`} label="Transient 503 rate" step="0.05" hint="0 = never, 1 = every request" error={err.transientErrorRate?.message} register={register("transientErrorRate", num)} />
            <NumberField id={`${target}-throttle`} label="429 throttle rate" step="0.05" error={err.throttleRate?.message} register={register("throttleRate", num)} />
            <NumberField id={`${target}-retry-after`} label="Throttle Retry-After (s)" step="1" error={err.throttleRetryAfterSeconds?.message} register={register("throttleRetryAfterSeconds", num)} />
            <NumberField id={`${target}-lost`} label="Lost response rate" step="0.05" hint="Writes commit, then the connection drops" error={err.lostResponseRate?.message} register={register("lostResponseRate", num)} />
            <NumberField id={`${target}-latency`} label="Latency (ms)" step="50" error={err.latencyMs?.message} register={register("latencyMs", num)} />
            <NumberField id={`${target}-seed`} label="Seed" step="1" error={err.seed?.message} register={register("seed", num)} />
          </div>
          <div className="flex flex-wrap gap-x-6 gap-y-3">
            {target === "source" ? (
              <Controller
                control={control}
                name="pageOverlap"
                render={({ field }) => (
                  <div className="flex items-center gap-2">
                    <Switch id="source-page-overlap" checked={field.value} onCheckedChange={field.onChange} />
                    <Label htmlFor="source-page-overlap">Page overlap (each page repeats the previous page&apos;s last record)</Label>
                  </div>
                )}
              />
            ) : (
              <>
                <Controller
                  control={control}
                  name="honorIdempotencyKey"
                  render={({ field }) => (
                    <div className="flex items-center gap-2">
                      <Switch id="sap-honor-idem" checked={field.value} onCheckedChange={field.onChange} />
                      <Label htmlFor="sap-honor-idem">Honour Idempotency-Key</Label>
                    </div>
                  )}
                />
                <Controller
                  control={control}
                  name="referenceSearchLag"
                  render={({ field }) => (
                    <div className="flex items-center gap-2">
                      <Switch id="sap-search-lag" checked={field.value} onCheckedChange={field.onChange} />
                      <Label htmlFor="sap-search-lag">Reference search lag (lookups find nothing)</Label>
                    </div>
                  )}
                />
              </>
            )}
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Button type="submit" disabled={action.pending}>
              <Save /> Apply settings
            </Button>
            <Button type="button" variant="outline" disabled={action.pending} onClick={() => reset(current ?? DEFAULT_FAULT_SETTINGS)}>
              Discard edits
            </Button>
            <Button type="button" variant="ghost" disabled={action.pending} onClick={() => reset({ ...DEFAULT_FAULT_SETTINGS }, { keepDefaultValues: true })}>
              Fill defaults
            </Button>
          </div>
          <ActionStatus phase={action.phase} message={action.message} />
        </form>
      </CardContent>
    </Card>
  );
}
