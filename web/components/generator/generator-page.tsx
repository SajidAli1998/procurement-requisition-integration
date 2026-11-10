"use client";

import Link from "next/link";
import { useState } from "react";
import { useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import type { ColumnDef } from "@tanstack/react-table";
import { CheckCheck, Download, Eye, Upload } from "lucide-react";
import { ActionStatus } from "@/components/action-status";
import { DataTable } from "@/components/data-table";
import { ErrorPanel } from "@/components/error-panel";
import { JsonViewer } from "@/components/monaco";
import { PageHeader } from "@/components/page-header";
import { StatCard } from "@/components/stat-card";
import { ToneBadge } from "@/components/status-badge";
import { WorkspaceSwitcher } from "@/components/dashboard/workspace-switcher";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { useLabAction } from "@/hooks/use-lab-action";
import { api, qk, WORKSPACE_KEY } from "@/lib/api";
import { toCsv } from "@/lib/csv";
import { downloadJson, downloadText } from "@/lib/download";
import type { GeneratorManifestEntry, GeneratorOptions, GeneratorPreview, SourceLine, SourceRecord, ValidationResult } from "@/lib/types";

const pct = (label: string) =>
  z.coerce
    .number<string>({ error: `${label} must be a number` })
    .min(0, `${label} must be 0 or more`)
    .max(100, `${label} must be at most 100`);

const schema = z
  .object({
    count: z.coerce.number<string>({ error: "Count must be a number" }).int("Count must be a whole number").min(1, "Count must be at least 1").max(20000, "Count must be at most 20000"),
    seed: z.coerce.number<string>({ error: "Seed must be a number" }).int("Seed must be a whole number"),
    pendingPct: pct("Pending %"),
    invalidPct: pct("Invalid %"),
    duplicatePct: pct("Duplicate %"),
    existingInSapPct: pct("Existing in SAP %"),
    missingFieldsPct: pct("Missing fields %"),
    invalidCodesPct: pct("Invalid codes %"),
    approvalConflictPct: pct("Approval conflict %"),
    priceInconsistencyPct: pct("Price inconsistency %"),
    sapFailurePct: pct("SAP failure %"),
  })
  .superRefine((v, ctx) => {
    const special = v.pendingPct + v.invalidPct + v.missingFieldsPct + v.invalidCodesPct + v.approvalConflictPct + v.priceInconsistencyPct;
    if (special > 100) ctx.addIssue({ code: "custom", path: ["pendingPct"], message: `Pending + invalid + missing + codes + approval + price add up to ${special}%: at most 100%` });
  });

type Values = z.input<typeof schema>;
type Parsed = z.output<typeof schema>;

const FIELDS: { name: keyof Values; label: string; help: string }[] = [
  { name: "count", label: "Count", help: "Requisitions to generate (1 to 20000)" },
  { name: "seed", label: "Seed", help: "Same seed, same data" },
  { name: "pendingPct", label: "Pending %", help: "Not approved yet: skipped" },
  { name: "invalidPct", label: "Invalid %", help: "Broken business rules: quarantined" },
  { name: "duplicatePct", label: "Duplicate %", help: "Repeated in the feed" },
  { name: "existingInSapPct", label: "Existing in SAP %", help: "Pre-loaded in SAP: linked" },
  { name: "missingFieldsPct", label: "Missing fields %", help: "Required field missing" },
  { name: "invalidCodesPct", label: "Invalid codes %", help: "Unknown plant/unit/currency" },
  { name: "approvalConflictPct", label: "Approval conflict %", help: "Approved in the future, etc." },
  { name: "priceInconsistencyPct", label: "Price inconsistency %", help: "Total ≠ sum of lines" },
  { name: "sapFailurePct", label: "SAP failure %", help: "429/503/lost response scripted in SAP" },
];

const DEFAULTS: Values = {
  count: "100",
  seed: "2026",
  pendingPct: "5",
  invalidPct: "10",
  duplicatePct: "3",
  existingInSapPct: "5",
  missingFieldsPct: "4",
  invalidCodesPct: "3",
  approvalConflictPct: "2",
  priceInconsistencyPct: "3",
  sapFailurePct: "5",
};

function toOptions(v: Parsed): GeneratorOptions {
  return { ...v, approvedPct: Math.max(0, 100 - v.pendingPct) };
}

function flatten(records: SourceRecord[], manifest: GeneratorManifestEntry[]) {
  const expected = new Map(manifest.map((m) => [m.id, m.expected]));
  return records.flatMap((r) => {
    const lines = (Array.isArray(r.lines) ? r.lines : []) as SourceLine[];
    const base = {
      requisition_id: r.id,
      status: r.status,
      requester: r.requester,
      cost_center: r.costCenter,
      company_code: r.companyCode,
      currency: r.currency,
      needed_by: r.neededBy,
      justification: r.justification,
      total_amount: r.totalAmount,
    };
    const exp = expected.get(String(r.id)) ?? "";
    if (lines.length === 0) return [{ ...base, line_no: null, material_code: null, description: null, quantity: null, uom: null, unit_price: null, plant: null, expected: exp }];
    return lines.map((l) => ({ ...base, line_no: l.lineNo, material_code: l.materialCode, description: l.description, quantity: l.quantity, uom: l.uom, unit_price: l.unitPrice, plant: l.plant, expected: exp }));
  });
}

interface SampleCheck {
  id: string;
  expected: string;
  verdict: string;
  agrees: boolean;
  problems: string;
}

function verdictOf(v: ValidationResult): string {
  if (!v.valid) return "Quarantined";
  if (!v.approved) return "Skipped";
  return "Eligible";
}

function agrees(expected: string, verdict: string): boolean {
  const e = expected.toLowerCase();
  if (verdict === "Quarantined") return e.includes("quarantin");
  if (verdict === "Skipped") return e.includes("skip") || e.includes("ignored") || e.includes("not approved");
  return !e.includes("quarantin") && !e.includes("skip");
}

const manifestColumns: ColumnDef<GeneratorManifestEntry & { check?: SampleCheck }, unknown>[] = [
  { id: "id", accessorKey: "id", header: "ID", meta: { label: "ID" }, cell: ({ row }) => <span className="font-mono text-xs">{row.original.id}</span> },
  { id: "traits", accessorFn: (r) => r.traits.join(", "), header: "Traits", meta: { label: "Traits" } },
  { id: "expected", accessorKey: "expected", header: "Expected outcome", meta: { label: "Expected" } },
  {
    id: "check",
    accessorFn: (r) => r.check?.verdict ?? "",
    header: "Sample validation",
    meta: { label: "Sample validation" },
    cell: ({ row }) =>
      row.original.check ? (
        <span title={row.original.check.problems}>
          <ToneBadge tone={row.original.check.agrees ? "success" : "danger"}>
            {row.original.check.verdict} {row.original.check.agrees ? "(as expected)" : "(not as expected)"}
          </ToneBadge>
        </span>
      ) : (
        <span className="text-muted-foreground">-</span>
      ),
  },
];

export function GeneratorPage() {
  const form = useForm<Values, unknown, Parsed>({ resolver: zodResolver(schema), defaultValues: DEFAULTS, mode: "onChange" });
  const [preview, setPreview] = useState<{ options: GeneratorOptions; data: GeneratorPreview } | null>(null);
  const [checks, setChecks] = useState<Record<string, SampleCheck>>({});
  const [applyOpen, setApplyOpen] = useState(false);
  const errors = form.formState.errors;
  const pending = Number(useWatch({ control: form.control, name: "pendingPct" })) || 0;

  const previewAction = useLabAction<GeneratorOptions, GeneratorPreview>({
    name: "Preview",
    run: (o) => api.generatorPreview(o),
    invalidate: [],
    confirm: (d, o) => {
      setPreview({ options: o, data: d });
      setChecks({});
      return `the lab generated ${d.records.length} requisition(s) (nothing loaded yet).`;
    },
  });
  const sampleAction = useLabAction<void, SampleCheck[]>({
    name: "Validate sample",
    invalidate: [],
    run: async () => {
      if (!preview) return [];
      const manifest = new Map(preview.data.manifest.map((m) => [m.id, m]));
      const sample = preview.data.records.slice(0, 25);
      const out: SampleCheck[] = [];
      for (const r of sample) {
        const v = await api.validate(r);
        const expected = manifest.get(String(r.id))?.expected ?? "";
        const verdict = verdictOf(v);
        out.push({ id: String(r.id), expected, verdict, agrees: agrees(expected, verdict), problems: v.problems.map((p) => `${p.field}: ${p.message}`).join("; ") });
      }
      return out;
    },
    confirm: (res) => {
      setChecks(Object.fromEntries(res.map((c) => [c.id, c])));
      const bad = res.filter((c) => !c.agrees).length;
      return `the integration's validator checked ${res.length} record(s): ${res.length - bad} agree with the generator's expectation, ${bad} disagree. (SAP-side expectations such as duplicates are only proven by a sync.)`;
    },
  });
  const applyAction = useLabAction<GeneratorOptions, { loaded: number; sapPreloaded: number; faultsScripted: number }>({
    name: "Apply to lab",
    run: (o) => api.generatorApply(o),
    invalidate: [WORKSPACE_KEY, qk.batches],
    confirm: async (r) => {
      const ws = await api.workspace();
      return `the lab loaded ${r.loaded} requisition(s), pre-loaded ${r.sapPreloaded} SAP document(s) and scripted ${r.faultsScripted} SAP fault(s). Workspace dataset is now "${ws.dataset}". Run a sync from the control center.`;
    },
    onConfirmed: () => setApplyOpen(false),
  });

  const submitPreview = form.handleSubmit((v) => void previewAction.execute(toOptions(v)));
  const stamp = () => new Date().toISOString().replace(/[:.]/g, "-");
  const manifestRows = preview ? preview.data.manifest.map((m) => ({ ...m, check: checks[m.id] })) : [];
  const last = [previewAction, sampleAction, applyAction].filter((a) => a.phase !== "idle").sort((a, b) => (b.at ?? 0) - (a.at ?? 0))[0];

  return (
    <div className="space-y-4">
      <PageHeader
        title="Data generator"
        description="Synthetic requisitions with realistic relationships and a stated expected outcome for each, so the generated data is checked by the pipeline rather than assumed to pass."
        actions={
          <>
            <Button asChild variant="outline" size="sm">
              <Link href="/import">
                <Upload /> Import a file instead
              </Link>
            </Button>
            <WorkspaceSwitcher />
          </>
        }
      />
      <Card>
        <CardHeader>
          <CardTitle>Options</CardTitle>
          <CardDescription>Percentages are of the total count; the rest are valid and approved. Approved (derived): {Math.max(0, 100 - pending)}%.</CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={submitPreview} noValidate className="space-y-3" data-testid="generator-form">
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4">
              {FIELDS.map((f) => {
                const err = errors[f.name];
                return (
                  <div key={f.name} className="grid gap-1">
                    <Label htmlFor={`gen-${f.name}`}>{f.label}</Label>
                    <Input id={`gen-${f.name}`} inputMode="decimal" aria-invalid={!!err} aria-describedby={`gen-${f.name}-help`} {...form.register(f.name)} />
                    <p id={`gen-${f.name}-help`} className={err ? "text-xs text-destructive" : "text-xs text-muted-foreground"} role={err ? "alert" : undefined}>
                      {err?.message ?? f.help}
                    </p>
                  </div>
                );
              })}
            </div>
            <div className="flex flex-wrap gap-2">
              <Button type="submit" disabled={previewAction.pending}>
                <Eye /> {previewAction.pending ? "Generating..." : "Preview"}
              </Button>
              <Button type="button" variant="outline" disabled={!preview || sampleAction.pending} onClick={() => void sampleAction.execute()}>
                <CheckCheck /> Validate a sample (25)
              </Button>
              <Button type="button" variant="outline" disabled={!preview} onClick={() => preview && downloadText(toCsv(flatten(preview.data.records, preview.data.manifest)), `generated-${preview.options.seed}-${stamp()}.csv`, "text/csv")}>
                <Download /> Export CSV
              </Button>
              <Button type="button" variant="outline" disabled={!preview} onClick={() => preview && downloadJson({ options: preview.options, manifest: preview.data.manifest, records: preview.data.records }, `generated-${preview.options.seed}-${stamp()}.json`)}>
                <Download /> Export JSON
              </Button>
              <Button type="button" variant="destructive" onClick={form.handleSubmit(() => setApplyOpen(true))}>
                <Upload /> Apply to lab
              </Button>
            </div>
          </form>
        </CardContent>
      </Card>
      {last ? <ActionStatus phase={last.phase} message={last.message} /> : null}
      {previewAction.phase === "failed" && previewAction.error ? <ErrorPanel error={previewAction.error} title="Preview failed" /> : null}
      {preview ? (
        <>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4 lg:grid-cols-6">
            <StatCard label="Records" value={preview.data.records.length} />
            <StatCard label="SAP pre-loads" value={preview.data.sapPreloads ?? 0} tone="info" />
            <StatCard label="SAP faults" value={preview.data.sapFaults ?? 0} tone="warning" />
            {Object.entries(preview.data.summary).map(([k, v]) => (
              <StatCard key={k} label={`Expected: ${k}`} value={v} />
            ))}
          </div>
          <Card>
            <CardHeader>
              <CardTitle>Manifest</CardTitle>
              <CardDescription>What each generated record is and what the integration is expected to do with it.</CardDescription>
            </CardHeader>
            <CardContent>
              <DataTable testId="manifest" data={manifestRows} columns={manifestColumns} getRowId={(r) => r.id} initialPageSize={10} />
            </CardContent>
          </Card>
          <Card>
            <CardHeader>
              <CardTitle>First record</CardTitle>
            </CardHeader>
            <CardContent>
              <JsonViewer value={preview.data.records[0] ?? null} label="First generated record" />
            </CardContent>
          </Card>
        </>
      ) : null}
      <Dialog open={applyOpen} onOpenChange={(o) => (!applyAction.pending ? setApplyOpen(o) : undefined)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Replace the lab workspace with generated data?</DialogTitle>
            <DialogDescription>
              The lab is reset to an empty workspace, then the generated records are loaded into the mock source, SAP pre-loads inserted and SAP faults scripted. Run history is cleared. Only the isolated lab is affected.
            </DialogDescription>
          </DialogHeader>
          <ActionStatus phase={applyAction.phase} message={applyAction.message} />
          <DialogFooter>
            <Button variant="outline" onClick={() => setApplyOpen(false)} disabled={applyAction.pending}>
              Cancel
            </Button>
            <Button variant="destructive" disabled={applyAction.pending} onClick={form.handleSubmit((v) => void applyAction.execute(toOptions(v)))}>
              {applyAction.pending ? "Applying..." : "Apply to lab"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
