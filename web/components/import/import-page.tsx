"use client";

import { useRef, useState } from "react";
import { FileSpreadsheet, Upload } from "lucide-react";
import { ActionStatus } from "@/components/action-status";
import { ErrorPanel } from "@/components/error-panel";
import { JsonViewer } from "@/components/monaco";
import { PageHeader } from "@/components/page-header";
import { StatCard } from "@/components/stat-card";
import { ToneBadge } from "@/components/status-badge";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { useLabAction } from "@/hooks/use-lab-action";
import { api, qk, WORKSPACE_KEY } from "@/lib/api";
import type { ImportIssue, ImportResult } from "@/lib/types";

const MAX_BYTES = 20 * 1024 * 1024;

function Issues({ title, issues, tone }: { title: string; issues: ImportIssue[]; tone: "danger" | "warning" }) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          {title} <ToneBadge tone={issues.length ? tone : "success"}>{issues.length}</ToneBadge>
        </CardTitle>
      </CardHeader>
      <CardContent>
        {issues.length === 0 ? (
          <p className="text-sm text-muted-foreground">None.</p>
        ) : (
          <div className="max-h-96 overflow-auto rounded-md border">
            <table className="w-full text-sm" data-testid={`import-${tone === "danger" ? "errors" : "warnings"}`}>
              <thead className="sticky top-0 bg-muted text-xs text-muted-foreground">
                <tr>
                  <th className="px-2 py-1 text-left">Sheet</th>
                  <th className="px-2 py-1 text-right">Row</th>
                  <th className="px-2 py-1 text-left">Column</th>
                  <th className="px-2 py-1 text-left">Reason</th>
                </tr>
              </thead>
              <tbody>
                {issues.map((i, n) => (
                  <tr key={n} className="border-t align-top">
                    <td className="px-2 py-1">{i.sheet ?? "-"}</td>
                    <td className="px-2 py-1 text-right tabular-nums">{i.row ?? "-"}</td>
                    <td className="px-2 py-1">{i.column ?? "-"}</td>
                    <td className="px-2 py-1 break-anywhere">{i.reason}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

export function ImportPage() {
  const input = useRef<HTMLInputElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [fileError, setFileError] = useState<string | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);

  const check = useLabAction<File, ImportResult>({
    name: "Check file",
    run: (f) => api.importFile(f, false),
    invalidate: [],
    confirm: (r) => {
      setResult(r);
      return r.ok ? `the lab parsed ${r.requisitions} requisition(s) with ${r.lines} line(s) (nothing loaded yet).` : `the lab found ${r.errors.length} error(s); nothing can be loaded until they are fixed.`;
    },
  });
  const apply = useLabAction<File, ImportResult>({
    name: "Load into lab source",
    run: (f) => api.importFile(f, true),
    invalidate: [WORKSPACE_KEY, qk.requisitions],
    confirm: (r) => {
      setResult(r);
      return r.applied ? `the lab loaded ${r.requisitions} requisition(s) into its mock source. Run a sync to process them.` : `the lab did not load the file (${r.errors.length} error(s)).`;
    },
    onConfirmed: () => setConfirmOpen(false),
  });

  const pick = (f: File | null) => {
    setResult(null);
    check.reset();
    apply.reset();
    setFileError(null);
    if (!f) return setFile(null);
    if (!/\.(xlsx|csv)$/i.test(f.name)) {
      setFile(null);
      return setFileError("Choose an .xlsx or .csv file");
    }
    if (f.size > MAX_BYTES) {
      setFile(null);
      return setFileError("Files over 20 MB are refused");
    }
    setFile(f);
  };

  const last = [check, apply].filter((a) => a.phase !== "idle").sort((a, b) => (b.at ?? 0) - (a.at ?? 0))[0];

  return (
    <div className="space-y-4">
      <PageHeader title="Import" description="Load requisitions from a spreadsheet (the client's workbook layout or a flat one-row-per-line sheet) or CSV into the lab's mock source system." />
      <Card>
        <CardHeader>
          <CardTitle>File</CardTitle>
          <CardDescription>Check first: the lab parses the file and reports every problem with sheet, row and column. Loading only touches the isolated lab source.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-3">
          <div className="grid gap-1.5">
            <Label htmlFor="import-file">Spreadsheet or CSV (.xlsx, .csv)</Label>
            <input
              ref={input}
              id="import-file"
              type="file"
              accept=".xlsx,.csv,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,text/csv"
              onChange={(e) => pick(e.target.files?.[0] ?? null)}
              className="block w-full text-sm file:mr-3 file:rounded-md file:border file:border-input file:bg-background file:px-3 file:py-1.5 file:text-sm file:font-medium hover:file:bg-accent"
            />
            {fileError ? (
              <p className="text-xs text-destructive" role="alert">
                {fileError}
              </p>
            ) : file ? (
              <p className="text-xs text-muted-foreground">
                {file.name} · {(file.size / 1024).toFixed(1)} KB
              </p>
            ) : null}
          </div>
          <div className="flex flex-wrap gap-2">
            <Button onClick={() => file && void check.execute(file)} disabled={!file || check.pending}>
              <FileSpreadsheet /> {check.pending ? "Checking..." : "Check file"}
            </Button>
            <Button variant="destructive" onClick={() => setConfirmOpen(true)} disabled={!file || !result?.ok || apply.pending || result.applied}>
              <Upload /> Load into lab source
            </Button>
          </div>
          {last ? <ActionStatus phase={last.phase} message={last.message} /> : null}
          {last?.phase === "failed" && last.error ? <ErrorPanel error={last.error} title="The lab refused the file" /> : null}
        </CardContent>
      </Card>
      {result ? (
        <>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6" data-testid="import-summary">
            <StatCard label="File" value={<span className="text-sm break-anywhere">{result.fileName}</span>} hint={result.format} />
            <StatCard label="Sheets" value={result.sheets.length} hint={result.sheets.join(", ") || "-"} />
            <StatCard label="Requisitions" value={result.requisitions} tone="info" />
            <StatCard label="Lines" value={result.lines} />
            <StatCard label="Rows read / skipped" value={`${result.rowsRead ?? "-"} / ${result.rowsSkipped ?? "-"}`} />
            <StatCard label="Status" value={result.applied ? "Loaded" : result.ok ? "Ready" : "Errors"} tone={result.applied ? "success" : result.ok ? "info" : "danger"} hint={result.applied ? "confirmed by the lab" : "not loaded"} />
          </div>
          {!result.ok ? (
            <Alert variant="destructive">
              <AlertDescription>The file has errors, so it cannot be loaded. Fix them and check again.</AlertDescription>
            </Alert>
          ) : null}
          <div className="grid gap-4 lg:grid-cols-2">
            <Issues title="Errors" issues={result.errors} tone="danger" />
            <Issues title="Warnings" issues={result.warnings} tone="warning" />
          </div>
          {result.records && result.records.length > 0 ? (
            <Card>
              <CardHeader>
                <CardTitle>Parsed records {result.records.length < result.requisitions ? `(first ${result.records.length})` : ""}</CardTitle>
              </CardHeader>
              <CardContent>
                <JsonViewer value={result.records} maxHeight={500} label="Parsed records" />
              </CardContent>
            </Card>
          ) : null}
        </>
      ) : null}
      <Dialog open={confirmOpen} onOpenChange={(o) => (!apply.pending ? setConfirmOpen(o) : undefined)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Load {result?.requisitions ?? 0} requisition(s) into the lab source?</DialogTitle>
            <DialogDescription>Records are written to the isolated mock source (existing ids get a new version). Nothing is sent to SAP until you run a sync.</DialogDescription>
          </DialogHeader>
          <ActionStatus phase={apply.phase} message={apply.message} />
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirmOpen(false)} disabled={apply.pending}>
              Cancel
            </Button>
            <Button variant="destructive" onClick={() => file && void apply.execute(file)} disabled={!file || apply.pending}>
              {apply.pending ? "Loading..." : "Load"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
