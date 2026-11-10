"use client";

import { useEffect, useState } from "react";
import { useFieldArray, useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { CheckCheck, KeyRound, Plus, Send, Trash2, Wand2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { ToneBadge } from "@/components/status-badge";
import { newIdempotencyKey, parseBody, validDraft, type CallFn, type Draft, type Reference } from "./assistant-lib";

const draftSchema = z.object({
  costCenter: z.string().trim().min(1, "Cost center is required"),
  plant: z.string().trim().min(1, "Plant is required"),
  neededBy: z.string().regex(/^\d{4}-\d{2}-\d{2}$/, "Needed by must be a date (yyyy-mm-dd)"),
  justification: z.string(),
  lines: z
    .array(
      z.object({
        materialCode: z.string().trim().min(1, "Material code is required"),
        quantity: z.number({ error: "Quantity must be a number" }).refine(Number.isFinite, "Quantity must be a number"),
      }),
    )
    .min(1, "Add at least one line"),
});

const EMPTY: Draft = { costCenter: "", plant: "", neededBy: "", justification: "", lines: [{ materialCode: "", quantity: 1 }] };

function canonical(d: Draft): string {
  return JSON.stringify({ costCenter: d.costCenter.trim(), plant: d.plant.trim(), neededBy: d.neededBy, justification: d.justification, lines: d.lines.map((l) => ({ materialCode: l.materialCode.trim(), quantity: l.quantity })) });
}

export function DraftBuilder({ reference, call, busy }: { reference: Reference | null; call: CallFn; busy: boolean }) {
  const form = useForm<Draft>({ resolver: zodResolver(draftSchema), defaultValues: EMPTY });
  const { register, control, handleSubmit, reset, formState, getValues } = form;
  const lines = useFieldArray({ control, name: "lines" });
  const current = useWatch({ control }) as Draft;

  const [token, setToken] = useState("");
  const [tokenFor, setTokenFor] = useState<string | null>(null);
  const [sendToken, setSendToken] = useState(true);
  const [idemKey, setIdemKey] = useState("");
  const [sendKey, setSendKey] = useState(true);

  useEffect(() => {
    if (!reference || formState.isDirty) return;
    const d = validDraft(reference);
    if (d) reset(d);
  }, [reference, reset, formState.isDirty]);

  const changedSinceValidation = tokenFor !== null && canonical(getValues()) !== tokenFor;
  void current;

  const validate = handleSubmit(async (d) => {
    const r = await call("Validate draft", { method: "POST", path: "/v1/requisition-requests:validate", body: JSON.stringify(d) });
    const p = parseBody(r.response?.body);
    if (p.confirmationToken) {
      setToken(p.confirmationToken);
      setTokenFor(canonical(d));
    }
  });

  const create = handleSubmit(async (d) => {
    const headers: Record<string, string> = {};
    if (sendKey) {
      // Kept after first use so a retry can reuse the same key on purpose.
      const key = idemKey.trim() || newIdempotencyKey();
      if (!idemKey.trim()) setIdemKey(key);
      headers["Idempotency-Key"] = key;
    }
    if (sendToken && token.trim()) headers["X-User-Confirmation"] = token.trim();
    await call(sendToken && token ? "Create (with confirmation)" : "Create (without confirmation)", {
      method: "POST",
      path: "/v1/requisition-requests",
      body: JSON.stringify(d),
      headers,
    });
  });

  const materials = reference?.materials ?? [];
  const err = formState.errors;
  return (
    <Card>
      <CardHeader>
        <CardTitle>Draft builder</CardTitle>
        <CardDescription>
          Prices are never sent: the API takes them from its catalogue. Create needs an Idempotency-Key and, after the user agreed to the confirmationSummary, the confirmationToken as X-User-Confirmation.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <form className="space-y-3" noValidate aria-label="Requisition draft" onSubmit={(e) => e.preventDefault()}>
          <div className="grid gap-3 sm:grid-cols-3">
            <div className="space-y-1">
              <Label htmlFor="draft-cc">Cost center</Label>
              <Input id="draft-cc" list="draft-cc-list" aria-invalid={!!err.costCenter} {...register("costCenter")} />
              <datalist id="draft-cc-list">
                {(reference?.costCenters ?? []).map((c) => (
                  <option key={c.code} value={c.code}>
                    {c.name} ({c.companyCode}, {c.currency})
                  </option>
                ))}
              </datalist>
              {err.costCenter ? <p className="text-xs text-destructive">{err.costCenter.message}</p> : null}
            </div>
            <div className="space-y-1">
              <Label htmlFor="draft-plant">Plant</Label>
              <Input id="draft-plant" list="draft-plant-list" aria-invalid={!!err.plant} {...register("plant")} />
              <datalist id="draft-plant-list">
                {(reference?.plants ?? []).map((p) => (
                  <option key={p.code} value={p.code}>
                    {p.name}
                  </option>
                ))}
              </datalist>
              {err.plant ? <p className="text-xs text-destructive">{err.plant.message}</p> : null}
            </div>
            <div className="space-y-1">
              <Label htmlFor="draft-needed">Needed by</Label>
              <Input id="draft-needed" type="date" aria-invalid={!!err.neededBy} {...register("neededBy")} />
              {err.neededBy ? <p className="text-xs text-destructive">{err.neededBy.message}</p> : null}
            </div>
          </div>
          <div className="space-y-1">
            <Label htmlFor="draft-justification">Justification (10-500 characters)</Label>
            <Textarea id="draft-justification" rows={2} {...register("justification")} />
          </div>
          <fieldset className="space-y-2">
            <legend className="text-xs font-medium">Lines</legend>
            <datalist id="draft-material-list">
              {materials.map((m) => (
                <option key={m.code} value={m.code}>
                  {m.description} ({m.uom}, {m.unitPrice})
                </option>
              ))}
            </datalist>
            {lines.fields.map((f, i) => (
              <div key={f.id} className="grid grid-cols-[1fr_7rem_auto] items-end gap-2">
                <div className="space-y-1">
                  <Label htmlFor={`draft-line-${i}-material`}>Material code</Label>
                  <Input id={`draft-line-${i}-material`} list="draft-material-list" className="font-mono" aria-invalid={!!err.lines?.[i]?.materialCode} {...register(`lines.${i}.materialCode`)} />
                  {err.lines?.[i]?.materialCode ? <p className="text-xs text-destructive">{err.lines[i]?.materialCode?.message}</p> : null}
                </div>
                <div className="space-y-1">
                  <Label htmlFor={`draft-line-${i}-qty`}>Quantity</Label>
                  <Input id={`draft-line-${i}-qty`} type="number" step="any" aria-invalid={!!err.lines?.[i]?.quantity} {...register(`lines.${i}.quantity`, { valueAsNumber: true })} />
                  {err.lines?.[i]?.quantity ? <p className="text-xs text-destructive">{err.lines[i]?.quantity?.message}</p> : null}
                </div>
                <Button type="button" variant="ghost" size="icon" aria-label={`Remove line ${i + 1}`} onClick={() => lines.remove(i)} disabled={lines.fields.length === 1}>
                  <Trash2 />
                </Button>
              </div>
            ))}
            <div className="flex flex-wrap gap-2">
              <Button type="button" variant="outline" size="sm" onClick={() => lines.append({ materialCode: materials[0]?.code ?? "", quantity: 1 })}>
                <Plus /> Add line
              </Button>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                disabled={!reference}
                onClick={() => {
                  const d = reference ? validDraft(reference) : null;
                  if (d) reset(d);
                }}
              >
                <Wand2 /> Fill a valid draft
              </Button>
            </div>
          </fieldset>

          <div className="grid gap-3 rounded-md border p-3 sm:grid-cols-2">
            <div className="space-y-1">
              <div className="flex items-center gap-2">
                <Checkbox id="draft-send-key" checked={sendKey} onCheckedChange={(v) => setSendKey(v === true)} />
                <Label htmlFor="draft-send-key">Send Idempotency-Key</Label>
              </div>
              <div className="flex gap-2">
                <Input id="draft-idem" aria-label="Idempotency-Key" className="font-mono text-xs" value={idemKey} onChange={(e) => setIdemKey(e.target.value)} placeholder={sendKey ? "generated on create" : "not sent: the API answers 400"} disabled={!sendKey} />
                <Button type="button" variant="outline" size="icon" aria-label="Generate a new Idempotency-Key" onClick={() => setIdemKey(newIdempotencyKey())}>
                  <KeyRound />
                </Button>
              </div>
              <p className="text-[11px] text-muted-foreground">Reuse a key only to retry the same request; the API replays the first answer. Generate a new one for a new request.</p>
            </div>
            <div className="space-y-1">
              <div className="flex items-center gap-2">
                <Checkbox id="draft-send-token" checked={sendToken} onCheckedChange={(v) => setSendToken(v === true)} />
                <Label htmlFor="draft-send-token">Send X-User-Confirmation</Label>
              </div>
              <Input aria-label="Confirmation token" className="font-mono text-xs" value={token} onChange={(e) => setToken(e.target.value)} placeholder="validate first to get a token" disabled={!sendToken} />
              {changedSinceValidation ? (
                <ToneBadge tone="warning">Draft changed since the user confirmed it: the API must refuse (CONFIRMATION_MISMATCH)</ToneBadge>
              ) : tokenFor ? (
                <ToneBadge tone="success">Token matches the current draft</ToneBadge>
              ) : null}
            </div>
          </div>

          <div className="flex flex-wrap gap-2">
            <Button type="button" variant="outline" disabled={busy} onClick={() => void validate()}>
              <CheckCheck /> Validate
            </Button>
            <Button type="button" disabled={busy} onClick={() => void create()}>
              <Send /> Create requisition request
            </Button>
          </div>
          <p className="text-xs text-muted-foreground">
            A created request is stored in the lab source as PendingApproval. Approval is a human step outside the assistant: there is no approve action here.
          </p>
        </form>
      </CardContent>
    </Card>
  );
}
