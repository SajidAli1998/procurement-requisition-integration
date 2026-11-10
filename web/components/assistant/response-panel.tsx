"use client";

import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { HttpStatusBadge, ToneBadge } from "@/components/status-badge";
import { JsonViewer } from "@/components/monaco";
import { Button } from "@/components/ui/button";
import { fmtDateTime, fmtMs } from "@/lib/format";
import { parseBody, type CallRecord } from "./assistant-lib";

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <>
      <dt className="font-medium text-muted-foreground">{label}</dt>
      <dd className="min-w-0 break-anywhere">{children}</dd>
    </>
  );
}

export function ResponsePanel({ record, onUseToken }: { record: CallRecord | null; onUseToken?: (token: string) => void }) {
  if (!record) {
    return (
      <Card>
        <CardHeader>
          <CardTitle>Response</CardTitle>
          <CardDescription>Make a call to see the assistant API&apos;s answer here.</CardDescription>
        </CardHeader>
      </Card>
    );
  }
  const res = record.response;
  const p = parseBody(res?.body);
  const req = record.request;
  return (
    <Card data-testid="assistant-response">
      <CardHeader>
        <div className="flex flex-wrap items-center gap-2">
          <CardTitle>{record.label}</CardTitle>
          {res ? <HttpStatusBadge status={res.status} /> : <ToneBadge tone="critical">no answer</ToneBadge>}
          {p.code ? (
            <ToneBadge tone="neutral" className="font-mono">
              {p.code}
            </ToneBadge>
          ) : null}
        </div>
        <CardDescription className="font-mono break-anywhere">
          {req.method} {req.path} as {req.anonymous ? "anonymous (no token)" : req.user}
          {req.scopes ? ` [${req.scopes.join(", ")}]` : ""}
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        {record.error ? <p className="text-destructive break-anywhere">{record.error}</p> : null}
        {res ? (
          <dl className="grid grid-cols-[auto_1fr] gap-x-3 gap-y-1 text-xs">
            <Row label="HTTP status">
              <span data-testid="assistant-status">{res.status}</span>
            </Row>
            {p.code ? (
              <Row label="Error code">
                <span className="font-mono" data-testid="assistant-code">
                  {p.code}
                </span>
              </Row>
            ) : null}
            {p.title ? <Row label="Title">{p.title}</Row> : null}
            {p.detail ? <Row label="Detail">{p.detail}</Row> : null}
            {p.suggestedAction ? <Row label="Suggested action">{p.suggestedAction}</Row> : null}
            {p.retryable !== undefined ? <Row label="Retryable">{p.retryable ? "yes" : "no"}</Row> : null}
            {p.retryAfter !== undefined ? <Row label="Retry after">{p.retryAfter}s</Row> : null}
            <Row label="Correlation id">
              <span className="font-mono">{res.correlationId ?? "-"}</span>
            </Row>
            <Row label="Duration">{fmtMs(res.durationMs)}</Row>
            <Row label="At">{fmtDateTime(record.at)}</Row>
          </dl>
        ) : null}

        {p.valid !== undefined ? (
          <div className="space-y-2 rounded-md border p-3">
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-xs font-semibold">Validation</span>
              <ToneBadge tone={p.valid ? "success" : "danger"}>{p.valid ? "valid" : "invalid"}</ToneBadge>
              {p.estimatedTotal !== undefined ? (
                <span className="text-xs text-muted-foreground">
                  Estimated total {p.estimatedTotal} {p.currency ?? ""}
                </span>
              ) : null}
            </div>
            {p.confirmationSummary ? (
              <div>
                <div className="text-xs font-medium text-muted-foreground">confirmationSummary (what the user must agree to)</div>
                <p className="mt-0.5 rounded bg-muted/50 p-2 text-sm" data-testid="confirmation-summary">
                  {p.confirmationSummary}
                </p>
              </div>
            ) : null}
            {p.confirmationToken ? (
              <div>
                <div className="text-xs font-medium text-muted-foreground">confirmationToken</div>
                <div className="flex flex-wrap items-center gap-2">
                  <code className="rounded bg-muted/50 px-1.5 py-0.5 text-xs break-anywhere" data-testid="confirmation-token">
                    {p.confirmationToken}
                  </code>
                  {onUseToken ? (
                    <Button size="sm" variant="outline" onClick={() => onUseToken(p.confirmationToken!)}>
                      Use as X-User-Confirmation
                    </Button>
                  ) : null}
                </div>
              </div>
            ) : null}
          </div>
        ) : null}

        {p.invalidFields.length + p.issues.length > 0 ? (
          <div className="overflow-x-auto rounded-md border">
            <table className="w-full text-xs">
              <caption className="px-2 py-1 text-left font-semibold">Field problems</caption>
              <thead className="bg-muted/40 text-left text-muted-foreground">
                <tr>
                  <th className="px-2 py-1">Field</th>
                  <th className="px-2 py-1">Code</th>
                  <th className="px-2 py-1">Message</th>
                  <th className="px-2 py-1">Suggestion</th>
                </tr>
              </thead>
              <tbody>
                {[...p.invalidFields, ...p.issues].map((f, i) => (
                  <tr key={i} className="border-t align-top">
                    <td className="px-2 py-1 font-mono">{f.field}</td>
                    <td className="px-2 py-1 font-mono">{f.code}</td>
                    <td className="px-2 py-1 break-anywhere">{f.message}</td>
                    <td className="px-2 py-1 break-anywhere">{f.suggestion ?? "-"}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}

        {res ? (
          <div className="overflow-x-auto rounded-md border">
            <table className="w-full text-xs">
              <caption className="px-2 py-1 text-left font-semibold">Response headers</caption>
              <tbody>
                {Object.entries(res.headers).map(([k, v]) => (
                  <tr key={k} className="border-t">
                    <th scope="row" className="px-2 py-1 text-left font-mono font-medium">
                      {k}
                    </th>
                    <td className="px-2 py-1 font-mono break-anywhere">{v}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}

        {Object.keys(req.headers ?? {}).length > 0 ? (
          <div className="text-xs">
            <div className="mb-1 font-medium text-muted-foreground">Request headers sent</div>
            <ul className="space-y-0.5 font-mono">
              {Object.entries(req.headers).map(([k, v]) => (
                <li key={k} className="break-anywhere">
                  {k}: {v}
                </li>
              ))}
            </ul>
          </div>
        ) : null}

        {req.body ? (
          <div>
            <div className="mb-1 text-xs font-medium text-muted-foreground">Request body</div>
            <JsonViewer value={req.body} maxHeight={220} label="Request body" />
          </div>
        ) : null}
        {res ? (
          <div>
            <div className="mb-1 text-xs font-medium text-muted-foreground">Raw response body</div>
            {res.body ? <JsonViewer value={res.body} maxHeight={320} label="Response body" /> : <p className="text-xs text-muted-foreground">(empty)</p>}
          </div>
        ) : null}
      </CardContent>
    </Card>
  );
}
