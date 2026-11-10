"use client";

import { useState } from "react";
import { KeyRound, Plus, Send, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { JsonEditor } from "@/components/monaco";
import type { AssistantCallRequest } from "@/lib/types";
import { newIdempotencyKey, type CallFn } from "./assistant-lib";

type Method = AssistantCallRequest["method"];
interface HeaderRow {
  key: string;
  value: string;
}

export interface CallPreset {
  method: Method;
  path: string;
  body?: string;
  headers?: Record<string, string>;
}

export function CallBuilder({ call, busy, preset }: { call: CallFn; busy: boolean; preset?: CallPreset | null }) {
  const [method, setMethod] = useState<Method>(preset?.method ?? "GET");
  const [path, setPath] = useState(preset?.path ?? "/v1/requisition-requests");
  const [headers, setHeaders] = useState<HeaderRow[]>(Object.entries(preset?.headers ?? {}).map(([key, value]) => ({ key, value })));
  const [body, setBody] = useState(preset?.body ?? "");
  const [touched, setTouched] = useState(false);

  const pathError = !path.startsWith("/v1/") ? "Path must start with /v1/ (the lab only forwards assistant endpoints)" : null;
  const hasBody = method !== "GET" && method !== "DELETE";
  const headerError = headers.some((h) => h.key.trim() !== "" && !/^[A-Za-z0-9-]+$/.test(h.key.trim())) ? "Header names may only contain letters, digits and '-'" : null;

  const send = async () => {
    setTouched(true);
    if (pathError || headerError) return;
    const h: Record<string, string> = {};
    for (const row of headers) if (row.key.trim()) h[row.key.trim()] = row.value;
    await call(`${method} ${path}`, { method, path, headers: h, body: hasBody ? body : null });
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Call builder</CardTitle>
        <CardDescription>Send any request to the assistant API as the selected identity. Bodies are sent exactly as typed (invalid JSON included).</CardDescription>
      </CardHeader>
      <CardContent>
        <form
          className="space-y-3"
          noValidate
          aria-label="Assistant API call"
          onSubmit={(e) => {
            e.preventDefault();
            void send();
          }}
        >
          <div className="grid gap-2 sm:grid-cols-[8rem_1fr]">
            <div className="space-y-1">
              <Label htmlFor="call-method">Method</Label>
              <NativeSelect id="call-method" value={method} onChange={(e) => setMethod(e.target.value as Method)}>
                {(["GET", "POST", "PUT", "PATCH", "DELETE"] as Method[]).map((m) => (
                  <option key={m}>{m}</option>
                ))}
              </NativeSelect>
            </div>
            <div className="space-y-1">
              <Label htmlFor="call-path">Path</Label>
              <Input id="call-path" className="font-mono" value={path} onChange={(e) => setPath(e.target.value)} aria-invalid={touched && !!pathError} />
              {touched && pathError ? <p className="text-xs text-destructive">{pathError}</p> : null}
            </div>
          </div>
          <fieldset className="space-y-2">
            <legend className="text-xs font-medium">Headers</legend>
            {headers.map((h, i) => (
              <div key={i} className="grid grid-cols-[1fr_1.4fr_auto] gap-2">
                <Input aria-label={`Header ${i + 1} name`} placeholder="Header-Name" className="font-mono text-xs" value={h.key} onChange={(e) => setHeaders((rows) => rows.map((r, j) => (j === i ? { ...r, key: e.target.value } : r)))} />
                <Input aria-label={`Header ${i + 1} value`} placeholder="value" className="font-mono text-xs" value={h.value} onChange={(e) => setHeaders((rows) => rows.map((r, j) => (j === i ? { ...r, value: e.target.value } : r)))} />
                <Button type="button" variant="ghost" size="icon" aria-label={`Remove header ${i + 1}`} onClick={() => setHeaders((rows) => rows.filter((_, j) => j !== i))}>
                  <Trash2 />
                </Button>
              </div>
            ))}
            {touched && headerError ? <p className="text-xs text-destructive">{headerError}</p> : null}
            <div className="flex flex-wrap gap-2">
              <Button type="button" size="sm" variant="outline" onClick={() => setHeaders((rows) => [...rows, { key: "", value: "" }])}>
                <Plus /> Add header
              </Button>
              <Button type="button" size="sm" variant="outline" onClick={() => setHeaders((rows) => [...rows.filter((r) => r.key !== "Idempotency-Key"), { key: "Idempotency-Key", value: newIdempotencyKey() }])}>
                <KeyRound /> Add Idempotency-Key
              </Button>
              <Button type="button" size="sm" variant="outline" onClick={() => setHeaders((rows) => [...rows.filter((r) => r.key !== "X-User-Confirmation"), { key: "X-User-Confirmation", value: "" }])}>
                <Plus /> Add X-User-Confirmation
              </Button>
            </div>
          </fieldset>
          {hasBody ? (
            <div className="space-y-1">
              <span className="text-xs font-medium">Body</span>
              <JsonEditor value={body} onChange={setBody} height={220} label="Request body editor" />
            </div>
          ) : null}
          <Button type="submit" disabled={busy}>
            <Send /> Send
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}
