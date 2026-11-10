"use client";

import { useContext, useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { CheckCheck, FlaskConical, Pencil, Repeat, ShieldCheck } from "lucide-react";
import { ActionStatus } from "@/components/action-status";
import { ErrorPanel } from "@/components/error-panel";
import { JsonEditor, JsonViewer } from "@/components/monaco";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Textarea } from "@/components/ui/textarea";
import { ToneBadge } from "@/components/status-badge";
import { useLabAction } from "@/hooks/use-lab-action";
import { api, qk, WORKSPACE_KEY } from "@/lib/api";
import { getPath, mergePatchFor, parseTyped, setPath } from "@/lib/json-path";
import type { JsonObject, QuarantineItem, ValidationResult, WorkspaceRun } from "@/lib/types";
import { asText } from "@/lib/utils";
import { PinContext } from "./pin-context";

function display(v: unknown): string {
  if (v === undefined || v === null) return "";
  return typeof v === "string" ? v : JSON.stringify(v);
}

export function QuarantineRepair({ item }: { item: QuarantineItem }) {
  const id = item.sourceKey;
  const pinRow = useContext(PinContext);
  const detail = useQuery({ queryKey: qk.requisition(id), queryFn: () => api.requisition(id) });
  const source = (detail.data?.source ?? null) as JsonObject | null;
  const fields = useMemo(() => [...new Set(item.problems.map((p) => p.field).filter((f) => f && f !== "$"))], [item.problems]);
  const [values, setValues] = useState<Record<string, string>>({});
  const [patchText, setPatchText] = useState("");
  const [validation, setValidation] = useState<ValidationResult | null>(null);
  const [resolveOpen, setResolveOpen] = useState(false);
  const [note, setNote] = useState("");

  const quickPatch = useMemo(() => {
    if (!source) return null;
    let after = source;
    const touched: string[] = [];
    for (const f of fields) {
      if (values[f] === undefined) continue;
      after = setPath(after, f, parseTyped(values[f]));
      touched.push(f);
    }
    return touched.length ? mergePatchFor(source, after, touched) : null;
  }, [source, fields, values]);

  const edit = useLabAction<JsonObject, unknown>({
    name: `Edit ${id} in the lab source`,
    run: (patch) => {
      pinRow(id);
      return api.editRequisition(id, patch);
    },
    invalidate: [WORKSPACE_KEY],
    confirm: async () => {
      const fresh = await api.requisition(id);
      const v = fresh.source?.version;
      setValues({});
      return `the lab source now holds ${id} version ${asText(v)}. Re-validate, then resubmit it through the pipeline.`;
    },
  });
  const revalidate = useLabAction<void, ValidationResult>({
    name: "Re-validate",
    run: async () => {
      const fresh = await api.requisition(id);
      if (!fresh.source) throw new Error(`${id} is no longer in the lab source`);
      return api.validate(fresh.source);
    },
    invalidate: [],
    confirm: (r) => {
      setValidation(r);
      return r.valid ? (r.approved ? "the integration's rules pass: the record is eligible for SAP." : "valid, but not approved: it would be skipped.") : `${r.problems.length} rule(s) still fail.`;
    },
  });
  const resubmit = useLabAction<void, WorkspaceRun>({
    name: `Resubmit ${id}`,
    run: () => {
      pinRow(id);
      return api.replay([id]);
    },
    confirm: (r) => `replay run ${r.id} accepted by the lab. It goes through the normal pipeline, and this entry leaves quarantine only once the lab reports it resolved.`,
  });
  const resolve = useLabAction<string, void>({
    name: `Resolve ${id}`,
    run: (n) => {
      pinRow(id);
      return api.resolveQuarantine(id, n);
    },
    confirm: () => "the lab marked the entry resolved by an operator. The requisition is NOT synchronised to SAP.",
    onConfirmed: () => setResolveOpen(false),
  });

  let patchError: string | null = null;
  let parsedPatch: JsonObject | null = null;
  if (patchText.trim()) {
    try {
      const v = JSON.parse(patchText) as unknown;
      if (!v || typeof v !== "object" || Array.isArray(v)) patchError = "The patch must be a JSON object";
      else parsedPatch = v as JsonObject;
    } catch (e) {
      patchError = e instanceof Error ? e.message : "Invalid JSON";
    }
  }

  const resolved = !!item.resolvedAt;
  return (
    <div className="space-y-3 text-sm" data-testid={`repair-${id}`}>
      <Alert variant="info">
        <FlaskConical />
        <AlertDescription>Edits change only the isolated lab&apos;s mock source system. Nothing reaches a real source or SAP.</AlertDescription>
      </Alert>

      <div>
        <h4 className="mb-1 font-semibold">Problems</h4>
        <div className="overflow-x-auto rounded-md border">
          <table className="w-full text-sm">
            <thead className="bg-muted/40 text-xs text-muted-foreground">
              <tr>
                <th className="px-2 py-1 text-left">Field</th>
                <th className="px-2 py-1 text-left">Reason</th>
                <th className="px-2 py-1 text-left">Current value</th>
                <th className="px-2 py-1 text-left">Message</th>
              </tr>
            </thead>
            <tbody>
              {item.problems.length === 0 ? (
                <tr>
                  <td colSpan={4} className="px-2 py-2 text-muted-foreground">
                    No field-level problems recorded: {item.summary}
                  </td>
                </tr>
              ) : (
                item.problems.map((p, i) => (
                  <tr key={i} className="border-t">
                    <td className="px-2 py-1 font-mono text-xs">{p.field || "-"}</td>
                    <td className="px-2 py-1">{p.reason ?? item.reason}</td>
                    <td className="px-2 py-1 font-mono text-xs break-anywhere">{p.currentValue === null || p.currentValue === undefined ? <span className="text-red-600">missing</span> : display(p.currentValue)}</td>
                    <td className="px-2 py-1 break-anywhere">{p.message}</td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
        <p className="mt-1 text-xs text-muted-foreground">
          Suggested: {item.suggestedAction} · Correctable in: {item.correctableIn}
        </p>
      </div>

      {resolved ? (
        <Alert variant="success">
          <ShieldCheck />
          <AlertDescription>
            Resolved {item.resolution ? `(${item.resolution})` : ""}. Resolved entries stay here for the audit trail.
          </AlertDescription>
        </Alert>
      ) : (
        <Tabs defaultValue="quick">
          <TabsList>
            <TabsTrigger value="quick">
              <Pencil /> Fix fields
            </TabsTrigger>
            <TabsTrigger value="patch">Merge patch (JSON)</TabsTrigger>
            <TabsTrigger value="raw">Raw payload</TabsTrigger>
          </TabsList>
          <TabsContent value="quick" className="space-y-2">
            {detail.isPending ? <Skeleton className="h-16" /> : detail.isError ? <ErrorPanel error={detail.error} compact /> : null}
            {source ? (
              fields.length === 0 ? (
                <p className="text-muted-foreground">No single field to fix. Use the merge patch tab, or fix SAP and resubmit.</p>
              ) : (
                <div className="grid gap-2 sm:grid-cols-2">
                  {fields.map((f) => {
                    const current = getPath(source, f);
                    const inputId = `fix-${id}-${f}`;
                    return (
                      <div key={f} className="grid gap-1">
                        <Label htmlFor={inputId}>
                          New value for <span className="font-mono">{f}</span>
                        </Label>
                        <Input
                          id={inputId}
                          value={values[f] ?? display(current)}
                          onChange={(e) => setValues((v) => ({ ...v, [f]: e.target.value }))}
                          placeholder="empty = null"
                        />
                      </div>
                    );
                  })}
                </div>
              )
            ) : null}
            {quickPatch ? (
              <p className="text-xs text-muted-foreground">
                Patch to send: <code className="break-anywhere">{JSON.stringify(quickPatch)}</code>
              </p>
            ) : null}
            <Button size="sm" onClick={() => quickPatch && void edit.execute(quickPatch)} disabled={!quickPatch || edit.pending}>
              <Pencil /> Apply edit to lab source
            </Button>
          </TabsContent>
          <TabsContent value="patch" className="space-y-2">
            <p className="text-xs text-muted-foreground">RFC 7396 merge patch sent to POST /workspace/requisitions/{id}/edit. Example: {"{"}&quot;totalAmount&quot;: 100{"}"}</p>
            <JsonEditor value={patchText} onChange={setPatchText} height={160} label="Merge patch" />
            {patchError ? (
              <p className="text-xs text-destructive" role="alert">
                {patchError}
              </p>
            ) : null}
            <Button size="sm" onClick={() => parsedPatch && void edit.execute(parsedPatch)} disabled={!parsedPatch || edit.pending}>
              Apply patch
            </Button>
          </TabsContent>
          <TabsContent value="raw">
            <JsonViewer value={item.rawPayload} label={`Raw payload of ${id} when quarantined`} />
          </TabsContent>
        </Tabs>
      )}

      {!resolved ? (
        <div className="flex flex-wrap gap-2">
          <Button size="sm" variant="outline" onClick={() => void revalidate.execute()} disabled={revalidate.pending}>
            <CheckCheck /> Re-validate
          </Button>
          <Button size="sm" variant="outline" onClick={() => void resubmit.execute()} disabled={resubmit.pending}>
            <Repeat /> Resubmit through pipeline
          </Button>
          <Button size="sm" variant="outline" onClick={() => setResolveOpen(true)}>
            <ShieldCheck /> Resolve with note
          </Button>
        </div>
      ) : null}
      {[edit, revalidate, resubmit, resolve]
        .filter((a) => a.phase !== "idle")
        .sort((a, b) => (b.at ?? 0) - (a.at ?? 0))
        .slice(0, 1)
        .map((a, i) => (
          <ActionStatus key={i} phase={a.phase} message={a.message} />
        ))}
      {validation ? (
        <div className="rounded-md border p-2" data-testid="revalidation">
          <div className="mb-1 flex items-center gap-2">
            <ToneBadge tone={validation.valid ? (validation.approved ? "success" : "muted") : "danger"}>{validation.valid ? (validation.approved ? "Valid and eligible" : "Valid, not approved") : "Still invalid"}</ToneBadge>
          </div>
          {validation.problems.length > 0 ? (
            <ul className="list-disc pl-5 text-xs">
              {validation.problems.map((p, i) => (
                <li key={i} className="break-anywhere">
                  <span className="font-mono">{p.field}</span> = {display(p.value) || "missing"}: {p.message} ({p.reason})
                </li>
              ))}
            </ul>
          ) : null}
        </div>
      ) : null}

      <Dialog open={resolveOpen} onOpenChange={setResolveOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Resolve {id}</DialogTitle>
            <DialogDescription>Closes the quarantine entry without sending anything to SAP. The requisition stays out of SAP, and the note is kept for the audit trail.</DialogDescription>
          </DialogHeader>
          <div className="grid gap-1.5">
            <Label htmlFor={`note-${id}`}>Resolution note</Label>
            <Textarea id={`note-${id}`} value={note} onChange={(e) => setNote(e.target.value)} placeholder="For example: cancelled by the requester, handled by hand" />
          </div>
          <ActionStatus phase={resolve.phase} message={resolve.message} />
          <DialogFooter>
            <Button onClick={() => void resolve.execute(note.trim())} disabled={note.trim().length < 3 || resolve.pending}>
              Resolve
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
