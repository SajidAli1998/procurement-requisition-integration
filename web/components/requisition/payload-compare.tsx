"use client";

import { useMemo, useState } from "react";
import { JsonDiff, JsonViewer } from "@/components/monaco";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { jsonDiff } from "@/lib/json-diff";
import type { JsonValue, RequisitionDetail } from "@/lib/types";

type PaneKey = "source" | "canonical" | "sapPayload" | "sapDocuments";

const PANES: { key: PaneKey; label: string; description: string }[] = [
  { key: "source", label: "Source JSON", description: "The record as the source system holds it now." },
  { key: "canonical", label: "Canonical", description: "What the integration parsed and stored at the last sync." },
  { key: "sapPayload", label: "SAP payload", description: "The create request the integration built from the canonical record." },
  { key: "sapDocuments", label: "SAP documents", description: "Documents in the SAP mock with this ExternalReference." },
];

function paneValue(detail: RequisitionDetail, key: PaneKey): JsonValue {
  switch (key) {
    case "source":
      return (detail.source ?? null) as JsonValue;
    case "canonical":
      return detail.canonical;
    case "sapPayload":
      return detail.sapPayload;
    case "sapDocuments":
      return detail.sapDocuments as JsonValue;
  }
}

function asText(v: JsonValue | undefined) {
  if (v === undefined) return "(absent)";
  const t = JSON.stringify(v);
  return t.length > 160 ? `${t.slice(0, 160)}...` : t;
}

export function PayloadCompare({ detail }: { detail: RequisitionDetail }) {
  const [left, setLeft] = useState<PaneKey>("source");
  const [right, setRight] = useState<PaneKey>("canonical");
  const leftValue = paneValue(detail, left);
  const rightValue = paneValue(detail, right);
  const diffs = useMemo(() => jsonDiff(leftValue ?? undefined, rightValue ?? undefined), [leftValue, rightValue]);
  const label = (k: PaneKey) => PANES.find((p) => p.key === k)?.label ?? k;

  return (
    <div className="space-y-4">
      <div className="grid gap-3 lg:grid-cols-2">
        {PANES.map((p) => {
          const v = paneValue(detail, p.key);
          const empty = v === null || (Array.isArray(v) && v.length === 0);
          return (
            <Card key={p.key}>
              <CardHeader>
                <CardTitle>{p.label}</CardTitle>
                <CardDescription>{p.description}</CardDescription>
              </CardHeader>
              <CardContent>
                {empty ? (
                  <p className="text-sm text-muted-foreground">
                    {p.key === "sapDocuments" ? "No SAP document has this reference." : p.key === "source" ? "Not in the lab source." : "Not produced: the record was never synced."}
                  </p>
                ) : (
                  <JsonViewer value={v} maxHeight={360} label={p.label} />
                )}
              </CardContent>
            </Card>
          );
        })}
      </div>

      <Card data-testid="payload-compare">
        <CardHeader>
          <CardTitle>Compare</CardTitle>
          <CardDescription>Pick any two representations. Field names differ between source and SAP, so a source-vs-SAP diff shows mapping as well as changes.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <div className="space-y-1">
              <Label htmlFor="compare-left">Left</Label>
              <NativeSelect id="compare-left" value={left} onChange={(e) => setLeft(e.target.value as PaneKey)}>
                {PANES.map((p) => (
                  <option key={p.key} value={p.key}>
                    {p.label}
                  </option>
                ))}
              </NativeSelect>
            </div>
            <div className="space-y-1">
              <Label htmlFor="compare-right">Right</Label>
              <NativeSelect id="compare-right" value={right} onChange={(e) => setRight(e.target.value as PaneKey)}>
                {PANES.map((p) => (
                  <option key={p.key} value={p.key}>
                    {p.label}
                  </option>
                ))}
              </NativeSelect>
            </div>
          </div>
          <JsonDiff original={leftValue} modified={rightValue} height={380} labels={[label(left), label(right)]} />
          <div>
            <h3 className="mb-1 text-sm font-medium">
              Field differences ({diffs.length}
              {diffs.length > 200 ? ", first 200 shown" : ""})
            </h3>
            {diffs.length === 0 ? (
              <p className="text-sm text-muted-foreground">Identical.</p>
            ) : (
              <div className="max-h-80 overflow-auto rounded-md border">
                <table className="w-full text-xs">
                  <thead className="sticky top-0 bg-muted text-muted-foreground">
                    <tr>
                      <th scope="col" className="px-2 py-1 text-left">Path</th>
                      <th scope="col" className="px-2 py-1 text-left">Change</th>
                      <th scope="col" className="px-2 py-1 text-left">{label(left)}</th>
                      <th scope="col" className="px-2 py-1 text-left">{label(right)}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {diffs.slice(0, 200).map((d) => (
                      <tr key={d.path} className="border-t align-top">
                        <td className="px-2 py-1 font-mono break-anywhere">{d.path}</td>
                        <td className="px-2 py-1">{d.kind === "added" ? `only in ${label(right)}` : d.kind === "removed" ? `only in ${label(left)}` : "changed"}</td>
                        <td className="px-2 py-1 font-mono break-anywhere">{asText(d.before)}</td>
                        <td className="px-2 py-1 font-mono break-anywhere">{asText(d.after)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </CardContent>
      </Card>
    </div>
  );
}
