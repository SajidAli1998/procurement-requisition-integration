"use client";

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { Controller, useFieldArray, useForm, useWatch, type FieldErrors } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { AlertTriangle, CheckCheck, Clock, FileCode2, ListChecks, Save } from "lucide-react";
import { ActionStatus } from "@/components/action-status";
import { ErrorPanel } from "@/components/error-panel";
import { JsonDiff, JsonEditor } from "@/components/monaco";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { useLabAction } from "@/hooks/use-lab-action";
import { WORKSPACE_KEY, api, isApiError, qk } from "@/lib/api";
import {
  KNOWN_CURRENCIES,
  STATUSES,
  businessWarnings,
  buildTemplates,
  emptyForm,
  emptyLine,
  formToRecord,
  linesTotal,
  newLabId,
  recordToForm,
  requisitionFormSchema,
  type Extras,
  type RequisitionForm,
  type Template,
} from "@/lib/requisition-form";
import type { JsonObject, SourceLine, SourceRecord } from "@/lib/types";
import { CompareButton, FixturesPanel, GeneratorTools, TemplatesMenu } from "./editor-tools";
import { Field, Section, fieldAria } from "./field";
import { LineItemsEditor } from "./line-items-editor";
import { useUnsavedChangesGuard } from "./use-unsaved-guard";
import { ValidationResultPanel } from "./validation-result";

export interface EditorInitial {
  record?: SourceRecord;
  raw?: string;
  original?: SourceRecord | null;
  forceVersion?: number;
  note?: string;
}

type Mode = "form" | "json";

interface SaveVars {
  id: string;
  body: SourceRecord | string;
  version?: number;
}

const EMPTY_EXTRAS: Extras = { record: {}, lines: [] };

function withoutStoreMetadata(record: SourceRecord): SourceRecord {
  const copy: SourceRecord = { ...record };
  delete copy.version;
  delete copy.updatedAt;
  delete copy.erpSync;
  return copy;
}

function isPlainObject(v: unknown): v is Record<string, unknown> {
  return !!v && typeof v === "object" && !Array.isArray(v);
}

function flattenErrors(errors: FieldErrors<RequisitionForm>): { id: string; message: string }[] {
  const out: { id: string; message: string }[] = [];
  for (const [key, err] of Object.entries(errors)) {
    if (!err) continue;
    if (key === "lines") {
      const lines = err as unknown as Record<string, unknown> & { message?: string; root?: { message?: string } };
      if (typeof lines.message === "string") out.push({ id: "lines-0-lineNo", message: lines.message });
      if (lines.root?.message) out.push({ id: "lines-0-lineNo", message: lines.root.message });
      for (const [i, lineErr] of Object.entries(lines)) {
        if (!/^\d+$/.test(i) || !lineErr) continue;
        for (const [field, fe] of Object.entries(lineErr as Record<string, { message?: string }>)) {
          if (fe?.message) out.push({ id: `lines-${i}-${field}`, message: `Line ${Number(i) + 1}: ${fe.message}` });
        }
      }
      continue;
    }
    const message = (err as { message?: string }).message;
    if (message) out.push({ id: `f-${key}`, message });
  }
  return out;
}

export function RequisitionEditor({ mode, initial, lockedId }: { mode: "new" | "edit"; initial?: EditorInitial; lockedId?: string }) {
  const qc = useQueryClient();
  // Computed once on mount; later prop changes do not reset an open form.
  const [start] = useState(() => {
    const initialState = (() => {
      if (initial?.record) return recordToForm(initial.record);
      if (initial?.raw) {
        try {
          const parsed: unknown = JSON.parse(initial.raw);
          if (isPlainObject(parsed)) return recordToForm(parsed as SourceRecord);
        } catch {
          // Fall back to an empty form; the raw text stays in the JSON tab.
        }
      }
      return { form: { ...emptyForm(), id: lockedId ?? "" }, extras: EMPTY_EXTRAS };
    })();
    // Start with the computed total so that opening a blank form does not count as a change.
    if (!initialState.form.overrideTotal) initialState.form = { ...initialState.form, totalAmount: String(linesTotal(initialState.form.lines)) };
    return initialState;
  });

  const form = useForm<RequisitionForm>({ resolver: zodResolver(requisitionFormSchema), defaultValues: start.form, mode: "onTouched" });
  const { fields, append, remove } = useFieldArray({ control: form.control, name: "lines", keyName: "key" });
  const [extras, setExtras] = useState<Extras>(start.extras);
  const [tab, setTab] = useState<Mode>(initial?.raw ? "json" : "form");
  const [source, setSource] = useState<Mode>(initial?.raw ? "json" : "form");
  const [jsonText, setJsonText] = useState<string>(() => initial?.raw ?? JSON.stringify(formToRecord(start.form, start.extras), null, 2));
  const [jsonError, setJsonError] = useState<string | null>(null);
  const [jsonDirty, setJsonDirty] = useState(!!initial?.raw && mode === "new");
  const [original, setOriginal] = useState<SourceRecord | null>(initial?.original ?? initial?.record ?? null);
  const [forceVersion, setForceVersion] = useState(initial?.forceVersion !== undefined ? String(initial.forceVersion) : "");
  const [forceError, setForceError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(initial?.note ?? null);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [loading, setLoading] = useState(false);
  const [showCompare, setShowCompare] = useState(false);
  const [showSummary, setShowSummary] = useState(false);
  const [savedId, setSavedId] = useState<string | null>(null);
  const [templates] = useState<Template[]>(() => buildTemplates(lockedId ?? newLabId()));

  const values = useWatch({ control: form.control, compute: (all: RequisitionForm) => all });
  const override = values.overrideTotal;
  const computedTotal = linesTotal(values.lines ?? []);
  const warnings = useMemo(() => businessWarnings(values), [values]);
  const errors = form.formState.errors;

  // The operator can override the total to create an inconsistent record on purpose.
  useEffect(() => {
    if (!override && form.getValues("totalAmount") !== String(computedTotal)) form.setValue("totalAmount", String(computedTotal));
  }, [override, computedTotal, form]);

  useEffect(() => {
    return form.subscribe({
      formState: { values: true },
      callback: ({ type }) => {
        if (type === "change") setSource("form");
      },
    });
  }, [form]);

  const dirty = form.formState.isDirty || jsonDirty;
  useUnsavedChangesGuard(dirty);

  const currentRecord = (): SourceRecord => formToRecord(form.getValues(), extras);
  const currentText = (): string => (source === "json" ? jsonText : JSON.stringify(currentRecord(), null, 2));

  const validate = useMutation({ mutationFn: (body: SourceRecord | string) => api.validate(body) });

  const save = useLabAction<SaveVars, JsonObject>({
    name: "Save to lab source",
    run: (v) => api.putRequisition(v.id, v.body, v.version),
    invalidate: [WORKSPACE_KEY],
    confirm: async (_r, v) => {
      // The stored record is what counts, not what we sent.
      const detail = await qc.fetchQuery({ queryKey: qk.requisition(v.id), queryFn: () => api.requisition(v.id), staleTime: 0 });
      if (!detail.source) throw new Error(`The lab answered, but ${v.id} is not in the source on re-read`);
      return `The lab source now holds ${v.id} at version ${String(detail.source.version ?? "?")} (re-read from the lab). The next sync run picks it up.`;
    },
    onConfirmed: (_r, v) => {
      setSavedId(v.id);
      form.reset(form.getValues());
      setJsonDirty(false);
    },
  });

  const loadRecord = (record: SourceRecord, opts: { original?: SourceRecord | null; forceVersion?: string; note?: string } = {}) => {
    const r = lockedId ? { ...record, id: lockedId } : record;
    const { form: f, extras: e } = recordToForm(r);
    form.reset(f, { keepDefaultValues: true });
    setExtras(e);
    setJsonText(JSON.stringify(r, null, 2));
    setSource("form");
    setJsonError(null);
    setOriginal(opts.original === undefined ? r : opts.original);
    setForceVersion(opts.forceVersion ?? "");
    setNote(opts.note ?? null);
    setShowSummary(false);
    setSavedId(null);
    validate.reset();
    save.reset();
  };

  const applyJsonText = (text: string, markDirty = true) => {
    setJsonText(text);
    setSource("json");
    if (markDirty) setJsonDirty(true);
    try {
      const parsed: unknown = JSON.parse(text);
      if (!isPlainObject(parsed)) {
        setJsonError("The JSON must be an object ({ ... }). The form keeps its last valid values.");
        return;
      }
      const { form: f, extras: e } = recordToForm(parsed as SourceRecord);
      form.reset(f, { keepDefaultValues: true });
      setExtras(e);
      setJsonError(null);
    } catch (err) {
      setJsonError(`Invalid JSON: ${err instanceof Error ? err.message : String(err)}. The form keeps its last valid values.`);
    }
  };

  const fetchSource = async (id: string): Promise<SourceRecord | null> => {
    setLoading(true);
    setLoadError(null);
    try {
      const detail = await qc.fetchQuery({ queryKey: qk.requisition(id), queryFn: () => api.requisition(id), staleTime: 0 });
      if (!detail.source) {
        setLoadError(new Error(`${id} has no record in the lab source`));
        return null;
      }
      return detail.source;
    } catch (e) {
      setLoadError(e);
      return null;
    } finally {
      setLoading(false);
    }
  };

  const onClone = async (id: string) => {
    const src = await fetchSource(id);
    if (!src) return;
    const newId = newLabId();
    loadRecord({ ...withoutStoreMetadata(src), id: newId }, { original: src, note: `Cloned from ${id} as ${newId}. Saving creates a new requisition.` });
  };

  const onDuplicate = async (id: string) => {
    const src = await fetchSource(id);
    if (!src) return;
    const copy: SourceRecord = { ...src };
    delete copy.version;
    delete copy.updatedAt;
    loadRecord(copy, {
      original: src,
      note: `Duplicate ID: ${id} loaded unchanged. Saving resends the same requisition; the integration must link or update it, never create a second SAP document.`,
    });
  };

  const onConflict = async (id: string) => {
    const src = await fetchSource(id);
    if (!src) return;
    const version = typeof src.version === "number" ? src.version : Number(src.version ?? 1);
    const lines = (Array.isArray(src.lines) ? (src.lines as SourceLine[]) : []).map((l) => ({ ...l }));
    const [first] = lines;
    if (first) {
      const price = Number(first.unitPrice ?? 0);
      const qty = Number(first.quantity ?? 1);
      const newPrice = Math.round((price + 10) * 100) / 100;
      first.unitPrice = newPrice;
      first.lineTotal = Math.round(qty * newPrice * 100) / 100;
    }
    const total = Math.round(lines.reduce((s, l) => s + Number(l.quantity ?? 0) * Number(l.unitPrice ?? 0), 0) * 100) / 100;
    const conflicting: SourceRecord = { ...src, lines, totalAmount: lines.length > 0 ? total : Number(src.totalAmount ?? 0) + 10 };
    delete conflicting.updatedAt;
    loadRecord(conflicting, {
      original: src,
      forceVersion: String(Number.isFinite(version) ? version : 1),
      note: `Conflicting record: ${id} keeps version ${version} with a different amount (${String(src.totalAmount)} → ${String(conflicting.totalAmount)}). Save stores it with ?version=${version}.`,
    });
  };

  const onTemplate = (t: Template) => {
    if (t.raw) {
      applyJsonText(lockedId ? t.raw.replace(/"id":\s*"[^"]*"/, `"id": ${JSON.stringify(lockedId)}`) : t.raw);
      setOriginal(null);
      setForceVersion("");
      setNote(`Template "${t.name}" loaded as raw JSON: it is sent exactly as written.`);
      setTab("json");
      validate.reset();
      save.reset();
      return;
    }
    if (t.record) loadRecord(t.record, { note: `Template "${t.name}": ${t.description}` });
    setTab("form");
  };

  const onTabChange = (next: string) => {
    const m = next as Mode;
    if (m === "json" && source === "form") setJsonText(JSON.stringify(currentRecord(), null, 2));
    setTab(m);
  };

  const onInvalid = () => {
    setShowSummary(true);
    setTab("form");
  };

  const runValidate = async () => {
    if (source === "json") {
      setShowSummary(false);
      validate.mutate(jsonText);
      return;
    }
    await form.handleSubmit((v) => {
      setShowSummary(false);
      validate.mutate(formToRecord(v, extras));
    }, onInvalid)();
  };

  const runSave = async () => {
    setForceError(null);
    const fv = forceVersion.trim();
    const version = fv === "" ? undefined : Number(fv);
    if (version !== undefined && (!Number.isInteger(version) || version < 1)) {
      setForceError("Force version must be a whole number of 1 or more, or empty");
      document.getElementById("force-version")?.focus();
      return;
    }
    if (source === "json") {
      let id: unknown;
      try {
        id = (JSON.parse(jsonText) as Record<string, unknown>)?.id;
      } catch (err) {
        setJsonError(`Invalid JSON: ${err instanceof Error ? err.message : String(err)}. Fix it before saving.`);
        setTab("json");
        return;
      }
      if (typeof id !== "string" || id.trim() === "") {
        setJsonError('The JSON needs an "id" string: it decides which record is stored.');
        setTab("json");
        return;
      }
      setShowSummary(false);
      await save.execute({ id: id.trim(), body: jsonText, version });
      return;
    }
    await form.handleSubmit(async (v) => {
      setShowSummary(false);
      await save.execute({ id: v.id, body: formToRecord(v, extras), version });
    }, onInvalid)();
  };

  const onAddLine = () => {
    const nos = form.getValues("lines").map((l) => Number(l.lineNo)).filter(Number.isFinite);
    append(emptyLine((nos.length ? Math.max(...nos) : 0) + 10));
    setExtras((e) => ({ ...e, lines: [...e.lines, {}] }));
    setSource("form");
  };

  const onRemoveLine = (i: number) => {
    remove(i);
    setExtras((e) => ({ ...e, lines: e.lines.filter((_, j) => j !== i) }));
    setSource("form");
  };

  const submitErrors = showSummary ? flattenErrors(errors) : [];
  const serverError = save.phase === "failed" && isApiError(save.error) && save.error.status >= 500;
  const err = (name: keyof RequisitionForm) => errors[name]?.message as string | undefined;

  return (
    <div className="space-y-4" data-testid="requisition-form">
      <div className="flex flex-wrap items-center gap-2">
        <TemplatesMenu templates={templates} onPick={onTemplate} />
        <CompareButton enabled={original !== null} open={showCompare} onToggle={() => setShowCompare((v) => !v)} />
        <span className="ml-auto text-xs text-muted-foreground" data-testid="dirty-state">
          {dirty ? "Unsaved changes" : "No unsaved changes"}
        </span>
      </div>

      <Alert variant="info">
        <AlertTitle>Edits only touch the isolated lab source</AlertTitle>
        <AlertDescription>
          Saving stores the record in the in-memory source mock. Nothing is sent to SAP until a sync run picks it up through the normal pipeline, with the same validation and
          duplicate protections as production. Business rules below are warnings, so deliberately bad records can be saved and watched.
        </AlertDescription>
      </Alert>

      {note ? (
        <p className="rounded-md border bg-muted/40 px-3 py-2 text-sm break-anywhere" role="status" data-testid="editor-note">
          {note}
        </p>
      ) : null}
      {loading ? <p className="text-sm text-muted-foreground" role="status">Loading from the lab source...</p> : null}
      {loadError ? <ErrorPanel error={loadError} title="Could not load the record" compact /> : null}

      {submitErrors.length > 0 ? (
        <Alert variant="destructive" data-testid="form-errors">
          <AlertTriangle />
          <AlertTitle>Fix {submitErrors.length} field{submitErrors.length === 1 ? "" : "s"} before continuing</AlertTitle>
          <AlertDescription>
            <ul className="list-disc space-y-0.5 pl-4">
              {submitErrors.map((e, i) => (
                <li key={`${e.id}-${i}`}>
                  <button type="button" className="text-left underline underline-offset-2 break-anywhere" onClick={() => document.getElementById(e.id)?.focus()}>
                    {e.message}
                  </button>
                </li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      ) : null}

      {source === "json" ? (
        <Alert variant="warning" data-testid="raw-mode">
          <FileCode2 />
          <AlertTitle>Raw JSON mode</AlertTitle>
          <AlertDescription>Validate and Save send the Custom JSON text exactly as typed (duplicate keys and extra fields included). Edit the form to switch back.</AlertDescription>
        </Alert>
      ) : null}

      <Tabs value={tab} onValueChange={onTabChange}>
        <TabsList aria-label="Editor mode">
          <TabsTrigger value="form">
            <ListChecks /> Form
          </TabsTrigger>
          <TabsTrigger value="json">
            <FileCode2 /> Custom JSON
          </TabsTrigger>
        </TabsList>

        <TabsContent value="form" forceMount className="data-[state=inactive]:hidden">
          <form
            noValidate
            onSubmit={(e) => {
              e.preventDefault();
              void runSave();
            }}
            className="grid gap-4 xl:grid-cols-2"
          >
            <Section title="Identity">
              <Field id="f-id" label="Requisition ID" error={err("id")} hint={lockedId ? "The ID of an existing record cannot change here; use Clone for a new one." : "Letters, digits, '.', '-' and '_'."}>
                <Input {...fieldAria("f-id", err("id"), true)} readOnly={!!lockedId} autoComplete="off" {...form.register("id")} />
              </Field>
            </Section>

            <Section title="Requester">
              <div className="grid gap-3 sm:grid-cols-2">
                <Field id="f-requester" label="Requester" error={err("requester")}>
                  <Input {...fieldAria("f-requester", err("requester"))} autoComplete="off" {...form.register("requester")} />
                </Field>
                <Field id="f-department" label="Department" error={err("department")}>
                  <Input {...fieldAria("f-department", err("department"))} autoComplete="off" {...form.register("department")} />
                </Field>
              </div>
            </Section>

            <Section title="Approval">
              <div className="grid gap-3 sm:grid-cols-2">
                <Field id="f-status" label="Status" error={err("status")}>
                  <NativeSelect {...fieldAria("f-status", err("status"))} {...form.register("status")}>
                    <option value="">(empty)</option>
                    {STATUSES.map((s) => (
                      <option key={s} value={s}>
                        {s}
                      </option>
                    ))}
                    {values.status && !(STATUSES as readonly string[]).includes(values.status) ? <option value={values.status}>{values.status}</option> : null}
                  </NativeSelect>
                </Field>
                <Field id="f-approvedAt" label="Approved at" error={err("approvedAt")} hint="ISO date and time, e.g. 2026-11-09T10:00:00Z">
                  <div className="flex gap-2">
                    <Input {...fieldAria("f-approvedAt", err("approvedAt"), true)} autoComplete="off" placeholder="2026-11-09T10:00:00Z" {...form.register("approvedAt")} />
                    <Button
                      variant="outline"
                      size="icon"
                      aria-label="Fill with the current time" title="Use the current time"
                      onClick={() => form.setValue("approvedAt", new Date().toISOString(), { shouldDirty: true, shouldValidate: true })}
                    >
                      <Clock />
                    </Button>
                  </div>
                </Field>
              </div>
            </Section>

            <Section title="Company and cost center">
              <div className="grid gap-3 sm:grid-cols-3">
                <Field id="f-companyCode" label="Company code" error={err("companyCode")}>
                  <Input {...fieldAria("f-companyCode", err("companyCode"))} autoComplete="off" {...form.register("companyCode")} />
                </Field>
                <Field id="f-costCenter" label="Cost center" error={err("costCenter")}>
                  <Input {...fieldAria("f-costCenter", err("costCenter"))} autoComplete="off" {...form.register("costCenter")} />
                </Field>
                <Field id="f-currency" label="Currency" error={err("currency")}>
                  <Input {...fieldAria("f-currency", err("currency"))} autoComplete="off" list="testlab-currencies" {...form.register("currency")} />
                </Field>
                <datalist id="testlab-currencies">
                  {KNOWN_CURRENCIES.map((c) => (
                    <option key={c} value={c} />
                  ))}
                </datalist>
              </div>
            </Section>

            <Section title="Delivery">
              <Field id="f-neededBy" label="Needed by" error={err("neededBy")} hint="yyyy-mm-dd (a calendar date, no timezone)">
                <Input {...fieldAria("f-neededBy", err("neededBy"), true)} autoComplete="off" placeholder="2026-12-31" inputMode="numeric" {...form.register("neededBy")} />
              </Field>
            </Section>

            <Section title="Justification">
              <Field id="f-justification" label="Justification" error={err("justification")}>
                <Textarea {...fieldAria("f-justification", err("justification"))} rows={3} {...form.register("justification")} />
              </Field>
            </Section>

            <div className="xl:col-span-2">
              <Section title="Line items" description="Quantity × unit price per line. The integration requires at least one line.">
                <LineItemsEditor form={form} fields={fields} onAdd={onAddLine} onRemove={onRemoveLine} currency={values.currency ?? ""} />
              </Section>
            </div>

            <div className="xl:col-span-2">
              <Section title="Total">
                <div className="flex flex-wrap items-end gap-4">
                  <Field id="f-totalAmount" label="Total amount" error={err("totalAmount")} hint={override ? `Sum of the lines is ${computedTotal}` : "Calculated from the lines"} className="w-48">
                    <Input {...fieldAria("f-totalAmount", err("totalAmount"), true)} readOnly={!override} inputMode="decimal" autoComplete="off" {...form.register("totalAmount")} />
                  </Field>
                  <div className="flex items-center gap-2 pb-6">
                    <Controller
                      control={form.control}
                      name="overrideTotal"
                      render={({ field }) => <Switch id="f-overrideTotal" checked={field.value} onCheckedChange={(v) => field.onChange(!!v)} />}
                    />
                    <Label htmlFor="f-overrideTotal">Override total (create an inconsistent record)</Label>
                  </div>
                </div>
              </Section>
            </div>
          </form>
        </TabsContent>

        <TabsContent value="json" forceMount className="data-[state=inactive]:hidden">
          <div className="space-y-2">
            <p className="text-xs text-muted-foreground">
              The raw source record. Valid object JSON updates the form as you type; fields the form does not show are kept. Duplicate keys survive only when sent from here.
            </p>
            {tab === "json" ? <JsonEditor value={jsonText} onChange={(t) => applyJsonText(t)} height={460} label="Custom JSON" /> : null}
            {jsonError ? (
              <p role="alert" className="text-sm text-red-700 break-anywhere dark:text-red-300" data-testid="json-error">
                {jsonError}
              </p>
            ) : null}
          </div>
        </TabsContent>
      </Tabs>

      {warnings.length > 0 ? (
        <Alert variant="warning" data-testid="business-warnings">
          <AlertTriangle />
          <AlertTitle>
            {warnings.length} business rule warning{warnings.length === 1 ? "" : "s"} (saving is still allowed)
          </AlertTitle>
          <AlertDescription>
            <ul className="list-disc space-y-0.5 pl-4">
              {warnings.map((w, i) => (
                <li key={`${w.field}-${i}`} className="break-anywhere">
                  <span className="font-mono text-xs">{w.field}</span>: {w.message}
                </li>
              ))}
            </ul>
          </AlertDescription>
        </Alert>
      ) : (
        <p className="flex items-center gap-1 text-sm text-emerald-700 dark:text-emerald-300" data-testid="business-ok">
          <CheckCheck className="size-4" aria-hidden /> No business rule warnings in the browser check. Validate asks the integration itself.
        </p>
      )}

      <div className="sticky bottom-0 z-10 -mx-4 space-y-2 border-t bg-background/95 px-4 py-3 backdrop-blur sm:-mx-6 sm:px-6">
        <div className="flex flex-wrap items-end gap-2">
          <Button variant="outline" onClick={() => void runValidate()} disabled={validate.isPending} aria-busy={validate.isPending}>
            <ListChecks /> Validate
          </Button>
          <Button onClick={() => void runSave()} disabled={save.pending} aria-busy={save.pending}>
            <Save /> Save to lab source
          </Button>
          <div className="flex flex-col gap-1">
            <Label htmlFor="force-version">Force version</Label>
            <Input
              id="force-version"
              className="h-9 w-32"
              inputMode="numeric"
              placeholder="(next)"
              value={forceVersion}
              onChange={(e) => setForceVersion(e.target.value)}
              aria-invalid={forceError ? true : undefined}
              aria-describedby={forceError ? "force-version-error" : "force-version-hint"}
            />
          </div>
          <p id="force-version-hint" className="max-w-sm text-xs text-muted-foreground">
            Empty: the source assigns the next version. A number stores this content under that version (same version, different content).
          </p>
        </div>
        {forceError ? (
          <p id="force-version-error" role="alert" className="text-xs text-red-700 dark:text-red-300">
            {forceError}
          </p>
        ) : null}
        <ActionStatus phase={save.phase} message={save.message} />
        {serverError ? (
          <Alert variant="warning">
            <AlertTitle>A server error does not prove nothing was stored</AlertTitle>
            <AlertDescription>
              The Test Lab answered with HTTP {isApiError(save.error) ? save.error.status : ""}. Check the requisition in the explorer before retrying.
            </AlertDescription>
          </Alert>
        ) : null}
        {savedId && save.phase === "confirmed" ? (
          <Button asChild variant="link" className="h-auto p-0">
            <Link href={`/requisitions/${encodeURIComponent(savedId)}`}>Open {savedId} in the explorer</Link>
          </Button>
        ) : null}
      </div>

      {validate.isError ? <ErrorPanel error={validate.error} title="Validation request failed" /> : null}
      {validate.data ? <ValidationResultPanel result={validate.data} /> : null}

      {showCompare && original ? (
        <section aria-label="Compare corrected vs original" className="space-y-2" data-testid="compare-original">
          <h2 className="text-sm font-semibold">Corrected vs original</h2>
          <JsonDiff original={original} modified={currentText()} height={420} labels={["Original", "Current editor content"]} />
        </section>
      ) : null}

      <div className="grid gap-4 lg:grid-cols-2">
        <GeneratorTools busy={loading} onClone={(id) => void onClone(id)} onDuplicate={(id) => void onDuplicate(id)} onConflict={(id) => void onConflict(id)} lockedId={mode === "edit" ? lockedId : undefined} />
        <FixturesPanel
          currentName={values.id ?? ""}
          currentText={currentText}
          onLoad={(f) => {
            applyJsonText(lockedId ? f.raw.replace(/"id":\s*"[^"]*"/, `"id": ${JSON.stringify(lockedId)}`) : f.raw);
            setNote(`Fixture "${f.name}" loaded as raw JSON.`);
            setOriginal(null);
            setTab("json");
          }}
        />
      </div>
    </div>
  );
}
