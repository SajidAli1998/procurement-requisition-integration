"use client";

import { useState } from "react";
import { Controller, useFieldArray, useForm, useWatch, type Control, type UseFormRegister, type FieldErrors } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Crosshair, Plus, Send, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { ActionStatus } from "@/components/action-status";
import { useLabAction } from "@/hooks/use-lab-action";
import { api, qk } from "@/lib/api";
import { FAULT_KINDS, type FaultKind, type ScriptedFault } from "@/lib/types";
import { TARGET_LABEL, type Target } from "./fault-state-panel";

const INT = /^\d+$/;

function isJson(text: string): boolean {
  try {
    JSON.parse(text);
    return true;
  } catch {
    return false;
  }
}

const KIND_FIELDS: Record<FaultKind, ("statusCode" | "retry" | "delayMs" | "body" | "transform")[]> = {
  Error: ["statusCode"],
  Throttle: ["retry"],
  LostResponse: [],
  Delay: ["delayMs"],
  Abort: [],
  Respond: ["statusCode", "body"],
  CommitThenRespond: ["statusCode", "body"],
  CommitThenTransform: ["transform"],
};

const KIND_HELP: Record<FaultKind, string> = {
  Error: "Answer the status code before the handler runs: nothing is changed.",
  Throttle: "Answer 429 Too Many Requests, with or without Retry-After.",
  LostResponse: "The handler commits, then the connection drops: the client never learns the outcome.",
  Delay: "Hold the request for a while, then process it normally.",
  Abort: "Drop the connection before the handler runs: nothing is changed.",
  Respond: "Answer a canned status and body without running the handler.",
  CommitThenRespond: "The handler commits, then the client gets the canned status and body instead.",
  CommitThenTransform: "The handler commits, then its JSON response is truncated or one field is changed.",
};

const rowSchema = z
  .object({
    kind: z.enum(FAULT_KINDS),
    method: z.string(),
    pathContains: z.string().max(500),
    bodyContains: z.string().max(500),
    count: z.string().trim().refine((v) => INT.test(v) && Number(v) >= 1 && Number(v) <= 10_000, "Count must be a whole number from 1 to 10000"),
    skip: z.string().trim().refine((v) => v === "" || (INT.test(v) && Number(v) <= 10_000), "Skip must be a whole number (0 or more)"),
    statusCode: z.string().trim(),
    retryAfterSeconds: z.string().trim(),
    retryAfterAsDate: z.boolean(),
    omitRetryAfter: z.boolean(),
    delayMs: z.string().trim(),
    body: z.string(),
    transform: z.enum(["Truncate", "SetField"]),
    field: z.string().trim(),
    value: z.string(),
  })
  .superRefine((r, ctx) => {
    const uses = KIND_FIELDS[r.kind];
    if (uses.includes("statusCode") && !(INT.test(r.statusCode) && Number(r.statusCode) >= 100 && Number(r.statusCode) <= 599)) {
      ctx.addIssue({ code: "custom", path: ["statusCode"], message: "Status code must be 100-599" });
    }
    if (uses.includes("retry") && !r.omitRetryAfter && !(INT.test(r.retryAfterSeconds) && Number(r.retryAfterSeconds) <= 3600)) {
      ctx.addIssue({ code: "custom", path: ["retryAfterSeconds"], message: "Retry-After must be 0-3600 seconds" });
    }
    if (uses.includes("delayMs") && !(INT.test(r.delayMs) && Number(r.delayMs) <= 120_000)) {
      ctx.addIssue({ code: "custom", path: ["delayMs"], message: "Delay must be 0-120000 ms" });
    }
    if (uses.includes("body") && r.body.trim() !== "" && !isJson(r.body)) {
      ctx.addIssue({ code: "custom", path: ["body"], message: "Body must be valid JSON (or empty)" });
    }
    if (uses.includes("transform") && r.transform === "SetField") {
      if (r.field === "") ctx.addIssue({ code: "custom", path: ["field"], message: "Field to set is required" });
      if (r.value.trim() !== "" && !isJson(r.value)) ctx.addIssue({ code: "custom", path: ["value"], message: 'Value must be a JSON literal, e.g. null, 0, "text" (empty removes the field)' });
    }
  });

const scriptSchema = z.object({ rows: z.array(rowSchema).min(1, "Add at least one fault") });

type Row = z.infer<typeof rowSchema>;
type ScriptForm = z.infer<typeof scriptSchema>;

function defaultRow(target: Target, kind: FaultKind = "Error"): Row {
  return {
    kind,
    method: target === "sap" ? "POST" : "GET",
    pathContains: target === "sap" ? "A_PurchaseRequisitionHeader" : "/api/requisitions",
    bodyContains: "",
    count: "1",
    skip: "0",
    statusCode: kind === "Respond" || kind === "CommitThenRespond" ? "500" : "503",
    retryAfterSeconds: "1",
    retryAfterAsDate: false,
    omitRetryAfter: false,
    delayMs: "2000",
    body: "",
    transform: "Truncate",
    field: "",
    value: "",
  };
}

function toScriptedFault(r: Row): ScriptedFault {
  const uses = KIND_FIELDS[r.kind];
  const f: ScriptedFault = { kind: r.kind, count: Number(r.count), skip: r.skip === "" ? 0 : Number(r.skip) };
  if (r.method) f.method = r.method;
  if (r.pathContains.trim()) f.pathContains = r.pathContains.trim();
  if (r.bodyContains.trim()) f.bodyContains = r.bodyContains.trim();
  if (uses.includes("statusCode")) f.statusCode = Number(r.statusCode);
  if (uses.includes("retry")) {
    f.omitRetryAfter = r.omitRetryAfter;
    f.retryAfterAsDate = r.retryAfterAsDate;
    if (!r.omitRetryAfter) f.retryAfterSeconds = Number(r.retryAfterSeconds);
  }
  if (uses.includes("delayMs")) f.delayMs = Number(r.delayMs);
  if (uses.includes("body") && r.body.trim() !== "") f.body = r.body;
  if (uses.includes("transform")) {
    f.transform = r.transform;
    if (r.transform === "SetField") {
      f.field = r.field;
      f.value = r.value.trim() === "" ? null : r.value.trim();
    }
  }
  return f;
}

function FieldError({ message }: { message?: string }) {
  return message ? <p className="text-xs text-destructive">{message}</p> : null;
}

function RowEditor({
  index,
  control,
  register,
  errors,
  onRemove,
  target,
  setBodyContains,
}: {
  index: number;
  control: Control<ScriptForm>;
  register: UseFormRegister<ScriptForm>;
  errors: FieldErrors<ScriptForm>;
  onRemove: () => void;
  target: Target;
  setBodyContains: (v: string) => void;
}) {
  const kind = useWatch({ control, name: `rows.${index}.kind` }) as FaultKind;
  const transform = useWatch({ control, name: `rows.${index}.transform` });
  const omit = useWatch({ control, name: `rows.${index}.omitRetryAfter` });
  const [reqId, setReqId] = useState("");
  const e = errors.rows?.[index];
  const uses = KIND_FIELDS[kind] ?? [];
  const id = (n: string) => `script-${target}-${index}-${n}`;

  return (
    <fieldset className="space-y-3 rounded-md border p-3" data-testid="script-row">
      <legend className="px-1 text-xs font-semibold">Fault {index + 1}</legend>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <div className="space-y-1">
          <Label htmlFor={id("kind")}>Kind</Label>
          <NativeSelect id={id("kind")} {...register(`rows.${index}.kind`)}>
            {FAULT_KINDS.map((k) => (
              <option key={k} value={k}>
                {k}
              </option>
            ))}
          </NativeSelect>
        </div>
        <div className="space-y-1">
          <Label htmlFor={id("method")}>Method</Label>
          <NativeSelect id={id("method")} {...register(`rows.${index}.method`)}>
            <option value="">Any</option>
            {["GET", "POST", "PUT", "PATCH", "DELETE"].map((m) => (
              <option key={m} value={m}>
                {m}
              </option>
            ))}
          </NativeSelect>
        </div>
        <div className="space-y-1 sm:col-span-2">
          <Label htmlFor={id("path")}>Path contains</Label>
          <Input id={id("path")} placeholder="any path" className="font-mono" {...register(`rows.${index}.pathContains`)} />
        </div>
        <div className="space-y-1">
          <Label htmlFor={id("count")}>Count</Label>
          <Input id={id("count")} inputMode="numeric" aria-invalid={!!e?.count} {...register(`rows.${index}.count`)} />
          <FieldError message={e?.count?.message} />
        </div>
        <div className="space-y-1">
          <Label htmlFor={id("skip")}>Skip first</Label>
          <Input id={id("skip")} inputMode="numeric" aria-invalid={!!e?.skip} {...register(`rows.${index}.skip`)} />
          <FieldError message={e?.skip?.message} />
        </div>
        <div className="space-y-1 sm:col-span-2">
          <Label htmlFor={id("body-contains")}>Body contains (writes only)</Label>
          <Input id={id("body-contains")} className="font-mono" placeholder='e.g. "REQ-7001"' {...register(`rows.${index}.bodyContains`)} />
          <div className="flex gap-2">
            <Input aria-label={`Target one requisition (fault ${index + 1})`} placeholder="REQ-7001" value={reqId} onChange={(ev) => setReqId(ev.target.value)} className="h-8 text-xs" />
            <Button type="button" variant="outline" size="sm" disabled={!reqId.trim()} onClick={() => setBodyContains(`"${reqId.trim()}"`)}>
              <Crosshair /> Target one requisition
            </Button>
          </div>
          <p className="text-[11px] text-muted-foreground">Matches only POST/PUT/PATCH bodies; lookups (GET) have no body.</p>
        </div>
      </div>
      <p className="text-xs text-muted-foreground">{KIND_HELP[kind]}</p>
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        {uses.includes("statusCode") ? (
          <div className="space-y-1">
            <Label htmlFor={id("status")}>Status code</Label>
            <Input id={id("status")} inputMode="numeric" aria-invalid={!!e?.statusCode} {...register(`rows.${index}.statusCode`)} />
            <FieldError message={e?.statusCode?.message} />
          </div>
        ) : null}
        {uses.includes("retry") ? (
          <>
            <div className="space-y-1">
              <Label htmlFor={id("retry")}>Retry-After (s)</Label>
              <Input id={id("retry")} inputMode="numeric" disabled={omit} aria-invalid={!!e?.retryAfterSeconds} {...register(`rows.${index}.retryAfterSeconds`)} />
              <FieldError message={e?.retryAfterSeconds?.message} />
            </div>
            <Controller
              control={control}
              name={`rows.${index}.retryAfterAsDate`}
              render={({ field }) => (
                <div className="flex items-center gap-2 pt-5">
                  <Checkbox id={id("as-date")} checked={field.value} onCheckedChange={(v) => field.onChange(v === true)} disabled={omit} />
                  <Label htmlFor={id("as-date")}>Send as HTTP date</Label>
                </div>
              )}
            />
            <Controller
              control={control}
              name={`rows.${index}.omitRetryAfter`}
              render={({ field }) => (
                <div className="flex items-center gap-2 pt-5">
                  <Checkbox id={id("omit")} checked={field.value} onCheckedChange={(v) => field.onChange(v === true)} />
                  <Label htmlFor={id("omit")}>Omit Retry-After</Label>
                </div>
              )}
            />
          </>
        ) : null}
        {uses.includes("delayMs") ? (
          <div className="space-y-1">
            <Label htmlFor={id("delay")}>Delay (ms)</Label>
            <Input id={id("delay")} inputMode="numeric" aria-invalid={!!e?.delayMs} {...register(`rows.${index}.delayMs`)} />
            <FieldError message={e?.delayMs?.message} />
          </div>
        ) : null}
        {uses.includes("transform") ? (
          <>
            <div className="space-y-1">
              <Label htmlFor={id("transform")}>Transform</Label>
              <NativeSelect id={id("transform")} {...register(`rows.${index}.transform`)}>
                <option value="Truncate">Truncate (invalid JSON)</option>
                <option value="SetField">Set field</option>
              </NativeSelect>
            </div>
            {transform === "SetField" ? (
              <>
                <div className="space-y-1">
                  <Label htmlFor={id("field")}>Field</Label>
                  <Input id={id("field")} className="font-mono" placeholder="PurchaseRequisition" aria-invalid={!!e?.field} {...register(`rows.${index}.field`)} />
                  <FieldError message={e?.field?.message} />
                </div>
                <div className="space-y-1">
                  <Label htmlFor={id("value")}>Value (JSON literal; empty removes)</Label>
                  <Input id={id("value")} className="font-mono" placeholder="null" aria-invalid={!!e?.value} {...register(`rows.${index}.value`)} />
                  <FieldError message={e?.value?.message} />
                </div>
              </>
            ) : null}
          </>
        ) : null}
      </div>
      {uses.includes("body") ? (
        <div className="space-y-1">
          <Label htmlFor={id("body")}>Response body (JSON, empty for none)</Label>
          <Textarea id={id("body")} className="font-mono text-xs" rows={3} aria-invalid={!!e?.body} {...register(`rows.${index}.body`)} />
          <FieldError message={e?.body?.message} />
        </div>
      ) : null}
      <div className="flex justify-end">
        <Button type="button" variant="ghost" size="sm" onClick={onRemove} aria-label={`Remove fault ${index + 1}`}>
          <Trash2 /> Remove
        </Button>
      </div>
    </fieldset>
  );
}

export function ScriptBuilder({ target }: { target: Target }) {
  const form = useForm<ScriptForm>({ resolver: zodResolver(scriptSchema), defaultValues: { rows: [defaultRow(target)] } });
  const { control, register, handleSubmit, formState, setValue, reset } = form;
  const rows = useFieldArray({ control, name: "rows" });

  const action = useLabAction<ScriptedFault[], unknown>({
    name: `Queue ${TARGET_LABEL[target]} script`,
    run: (script) => api.scriptFaults(target, script),
    invalidate: [qk.workspace],
    confirm: (_r, script) => `${script.length} scripted fault(s) queued on the ${TARGET_LABEL[target]} mock; see "Pending scripted faults" for what it reports.`,
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Scripted faults: {TARGET_LABEL[target]}</CardTitle>
        <CardDescription>Deterministic faults for the next matching requests, applied in order. Each is consumed after Count matches.</CardDescription>
      </CardHeader>
      <CardContent>
        <form
          className="space-y-3"
          noValidate
          aria-label={`${TARGET_LABEL[target]} scripted faults`}
          onSubmit={handleSubmit((v) => void action.execute(v.rows.map(toScriptedFault)))}
        >
          {rows.fields.map((f, i) => (
            <RowEditor
              key={f.id}
              index={i}
              control={control}
              register={register}
              errors={formState.errors}
              target={target}
              onRemove={() => rows.remove(i)}
              setBodyContains={(v) => setValue(`rows.${i}.bodyContains`, v, { shouldDirty: true })}
            />
          ))}
          {formState.errors.rows?.root?.message || formState.errors.rows?.message ? (
            <p className="text-xs text-destructive">{formState.errors.rows?.root?.message ?? formState.errors.rows?.message}</p>
          ) : null}
          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="outline" onClick={() => rows.append(defaultRow(target))}>
              <Plus /> Add fault
            </Button>
            <Button type="submit" disabled={action.pending || rows.fields.length === 0}>
              <Send /> Queue script on {TARGET_LABEL[target]}
            </Button>
            <Button type="button" variant="ghost" onClick={() => reset({ rows: [defaultRow(target)] })}>
              Reset builder
            </Button>
          </div>
          <ActionStatus phase={action.phase} message={action.message} />
        </form>
      </CardContent>
    </Card>
  );
}
