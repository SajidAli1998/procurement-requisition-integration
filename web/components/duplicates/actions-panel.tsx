"use client";

import { useState } from "react";
import { FileWarning, KeyRound, Play, Plus, Repeat } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { ActionStatus } from "@/components/action-status";
import { useLabAction } from "@/hooks/use-lab-action";
import { WORKSPACE_KEY, api } from "@/lib/api";
import { DEFAULT_FAULT_SETTINGS } from "@/lib/faults";
import type { FaultSettings, JsonObject, SourceLine, SourceRecord, WorkspaceRun } from "@/lib/types";
import { asNumber } from "@/lib/utils";

const LIFECYCLES = ["Created", "InRelease", "Released", "PoCreated", "Closed", "Rejected"];

function storedBody(source: SourceRecord): SourceRecord {
  const body: SourceRecord = { ...source };
  delete body.version;
  delete body.updatedAt;
  return body;
}

async function loadSource(id: string): Promise<SourceRecord> {
  const detail = await api.requisition(id);
  if (!detail.source) throw new Error(`${id} is not in the lab source (only in the integration database)`);
  return detail.source;
}

export function ActionsPanel({ ids, sapSettings }: { ids: string[]; sapSettings: FaultSettings | null }) {
  const [id, setId] = useState("");
  const [keepVersion, setKeepVersion] = useState(true);
  const [docRef, setDocRef] = useState("");
  const [docVersion, setDocVersion] = useState("1");
  const [docNumber, setDocNumber] = useState("");
  const [docLifecycle, setDocLifecycle] = useState("");
  const target = id.trim();
  const known = ids.includes(target);
  const versionValid = /^\d+$/.test(docVersion) && Number(docVersion) >= 1;

  const resend = useLabAction<{ id: string; keepVersion: boolean }, JsonObject>({
    name: "Resend same id",
    run: async ({ id: rid, keepVersion: keep }) => {
      const source = await loadSource(rid);
      const version = asNumber(source.version);
      return api.putRequisition(rid, storedBody(source), keep && version !== null ? version : undefined);
    },
    invalidate: [WORKSPACE_KEY],
    confirm: (r, v) =>
      `The lab source now holds ${v.id} at version ${String(r.version ?? "?")} with the same content. Run a sync: it must be linked or left unchanged, never created twice.`,
  });

  const conflict = useLabAction<string, JsonObject>({
    name: "Conflicting record",
    run: async (rid) => {
      const source = await loadSource(rid);
      const version = asNumber(source.version);
      if (version === null) throw new Error(`${rid} has no numeric version to reuse`);
      const body = storedBody(source);
      const lines = Array.isArray(body.lines) ? (body.lines as SourceLine[]).map((l) => ({ ...l })) : [];
      const [first] = lines;
      if (!first) throw new Error(`${rid} has no lines to change`);
      const q = asNumber(first.quantity) ?? 0;
      const p = asNumber(first.unitPrice) ?? 0;
      first.quantity = q + 1;
      first.lineTotal = Math.round((q + 1) * p * 100) / 100;
      const total = lines.reduce((s, l) => s + (asNumber(l.quantity) ?? 0) * (asNumber(l.unitPrice) ?? 0), 0);
      return api.putRequisition(rid, { ...body, lines, totalAmount: Math.round(total * 100) / 100 }, version);
    },
    invalidate: [WORKSPACE_KEY],
    confirm: (r, rid) =>
      `The lab source now holds ${rid} at the same version (${String(r.version ?? "?")}) with a different amount (total ${String(r.totalAmount ?? "?")}). The next sync must flag the conflict, not overwrite SAP silently.`,
  });

  const insert = useLabAction<{ reference: string; version: number; number?: string; lifecycle?: string }, { number: string }>({
    name: "Insert SAP document",
    run: (b) => api.insertSapDocument(b),
    invalidate: [WORKSPACE_KEY],
    confirm: (r, b) => `SAP now holds document ${r.number} for reference ${b.reference} (version ${b.version}). It was not created by the integration.`,
  });

  const evict = useLabAction<void, void>({
    name: "Evict SAP idempotency keys",
    run: () => api.evictSapKeys(),
    invalidate: [WORKSPACE_KEY],
    confirm: () => "The SAP mock forgot every Idempotency-Key it had seen. Protection 2 is gone until new creates arrive; 1 and 3 must still hold.",
  });

  const toggle = useLabAction<Partial<FaultSettings>, unknown>({
    name: "Change SAP behaviour",
    run: (patch) => api.putFaults("sap", { ...(sapSettings ?? DEFAULT_FAULT_SETTINGS), ...patch }),
    invalidate: [WORKSPACE_KEY],
    confirm: (_r, patch) => `The SAP mock now reports ${Object.entries(patch).map(([k, v]) => `${k} = ${String(v)}`).join(", ")} (switches show its reported state).`,
  });

  const sync = useLabAction<void, WorkspaceRun>({
    name: "Run sync",
    run: () => api.sync(),
    invalidate: [WORKSPACE_KEY],
    confirm: (run) => `The lab started run ${run.id}. Decisions below refresh when it finishes.`,
  });

  const honor = sapSettings?.honorIdempotencyKey ?? true;
  const lag = sapSettings?.referenceSearchLag ?? false;
  const busy = resend.pending || conflict.pending;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Provoke duplicates</CardTitle>
        <CardDescription>Each action changes only the isolated lab. Run a sync afterwards and read the decision and the SAP audit trail.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-5">
        <section className="space-y-2" aria-labelledby="resend-title">
          <h3 id="resend-title" className="text-sm font-semibold">
            Resend or conflict an existing requisition
          </h3>
          <div className="grid gap-2 sm:grid-cols-[1fr_auto] sm:items-end">
            <div className="space-y-1">
              <Label htmlFor="dup-id">Requisition id</Label>
              <Input id="dup-id" list="dup-ids" value={id} onChange={(e) => setId(e.target.value)} placeholder="REQ-7002" className="font-mono" />
              <datalist id="dup-ids">
                {ids.map((x) => (
                  <option key={x} value={x} />
                ))}
              </datalist>
            </div>
            <div className="flex items-center gap-2 pb-2">
              <Checkbox id="dup-keep-version" checked={keepVersion} onCheckedChange={(v) => setKeepVersion(v === true)} />
              <Label htmlFor="dup-keep-version">Keep the same version</Label>
            </div>
          </div>
          {target && !known ? <p className="text-xs text-amber-700 dark:text-amber-300">{target} is not in the current requisition list.</p> : null}
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" disabled={!target || busy} onClick={() => void resend.execute({ id: target, keepVersion })}>
              <Repeat /> Resend same id
            </Button>
            <Button variant="outline" disabled={!target || busy} onClick={() => void conflict.execute(target)}>
              <FileWarning /> Conflicting record (same version, different amount)
            </Button>
          </div>
          <p className="text-xs text-muted-foreground">
            The conflicting record adds 1 to the first line&apos;s quantity and recomputes the total, so it stays valid and only the content differs.
          </p>
          <ActionStatus phase={resend.phase} message={resend.message} />
          <ActionStatus phase={conflict.phase} message={conflict.message} />
        </section>

        <form
          className="space-y-2 border-t pt-4"
          aria-labelledby="insert-title"
          onSubmit={(e) => {
            e.preventDefault();
            if (!docRef.trim() || !versionValid) return;
            void insert.execute({
              reference: docRef.trim(),
              version: Number(docVersion),
              number: docNumber.trim() || undefined,
              lifecycle: docLifecycle || undefined,
            });
          }}
        >
          <h3 id="insert-title" className="text-sm font-semibold">
            Insert an existing SAP document
          </h3>
          <p className="text-xs text-muted-foreground">
            Puts a document straight into SAP, as if someone had keyed it in. A second document for the same reference makes the lookup ambiguous.
          </p>
          <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-4">
            <div className="space-y-1">
              <Label htmlFor="doc-ref">Reference (requisition id)</Label>
              <Input id="doc-ref" list="dup-ids" className="font-mono" value={docRef} onChange={(e) => setDocRef(e.target.value)} placeholder="REQ-7002" />
            </div>
            <div className="space-y-1">
              <Label htmlFor="doc-version">Version</Label>
              <Input id="doc-version" inputMode="numeric" value={docVersion} onChange={(e) => setDocVersion(e.target.value)} aria-invalid={!versionValid} />
              {!versionValid ? <p className="text-xs text-destructive">Version must be a whole number of 1 or more</p> : null}
            </div>
            <div className="space-y-1">
              <Label htmlFor="doc-number">SAP number (optional)</Label>
              <Input id="doc-number" className="font-mono" value={docNumber} onChange={(e) => setDocNumber(e.target.value)} placeholder="assigned by SAP" />
            </div>
            <div className="space-y-1">
              <Label htmlFor="doc-lifecycle">Lifecycle</Label>
              <NativeSelect id="doc-lifecycle" value={docLifecycle} onChange={(e) => setDocLifecycle(e.target.value)}>
                <option value="">Created (default)</option>
                {LIFECYCLES.filter((l) => l !== "Created").map((l) => (
                  <option key={l} value={l}>
                    {l}
                  </option>
                ))}
              </NativeSelect>
            </div>
          </div>
          <Button type="submit" variant="outline" disabled={insert.pending || !docRef.trim() || !versionValid}>
            <Plus /> Insert into SAP
          </Button>
          <ActionStatus phase={insert.phase} message={insert.message} />
        </form>

        <section className="space-y-3 border-t pt-4" aria-labelledby="sap-behaviour-title">
          <h3 id="sap-behaviour-title" className="text-sm font-semibold">
            Weaken SAP&apos;s side
          </h3>
          <div className="flex flex-col gap-3">
            <div className="flex items-start gap-2">
              <Switch id="dup-honor" checked={honor} disabled={toggle.pending || !sapSettings} onCheckedChange={(v) => void toggle.execute({ honorIdempotencyKey: v })} />
              <div>
                <Label htmlFor="dup-honor">SAP honours Idempotency-Key</Label>
                <p className="text-xs text-muted-foreground">Off: SAP ignores the key, like a real endpoint without idempotency support (protection 2 gone).</p>
              </div>
            </div>
            <div className="flex items-start gap-2">
              <Switch id="dup-lag" checked={lag} disabled={toggle.pending || !sapSettings} onCheckedChange={(v) => void toggle.execute({ referenceSearchLag: v })} />
              <div>
                <Label htmlFor="dup-lag">Reference search lag</Label>
                <p className="text-xs text-muted-foreground">On: lookups by ExternalReference return nothing, as when SAP&apos;s search index lags behind writes (protection 3 blind).</p>
              </div>
            </div>
          </div>
          <div className="flex flex-wrap gap-2">
            <Button variant="outline" disabled={evict.pending} onClick={() => void evict.execute()}>
              <KeyRound /> Evict SAP idempotency keys
            </Button>
            <Button disabled={sync.pending} onClick={() => void sync.execute()}>
              <Play /> Run sync
            </Button>
          </div>
          <ActionStatus phase={toggle.phase} message={toggle.message} />
          <ActionStatus phase={evict.phase} message={evict.message} />
          <ActionStatus phase={sync.phase} message={sync.message} />
        </section>
      </CardContent>
    </Card>
  );
}
