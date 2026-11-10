"use client";

import Link from "next/link";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Copy, Pencil, RotateCw, ShieldAlert } from "lucide-react";
import { ActionStatus } from "@/components/action-status";
import { EmptyState } from "@/components/empty-state";
import { ErrorPanel } from "@/components/error-panel";
import { HttpExchangeList } from "@/components/http-exchange-list";
import { JsonViewer } from "@/components/monaco";
import { PageHeader } from "@/components/page-header";
import { OutcomeBadge, ToneBadge } from "@/components/status-badge";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useLabAction } from "@/hooks/use-lab-action";
import { WORKSPACE_KEY, api, isApiError, qk } from "@/lib/api";
import { fmtAmount, fmtDateOnly, fmtDateTime } from "@/lib/format";
import { outcomeStyle } from "@/lib/status";
import type { JsonValue, RequisitionDetail, RequisitionRow, SourceLine, WorkspaceRun } from "@/lib/types";
import { asNumber, asText } from "@/lib/utils";
import { DuplicateDecisionPanel } from "./duplicate-decision";
import { deriveOutcome } from "./outcome";
import { PayloadCompare } from "./payload-compare";
import { ValidationResultPanel } from "./validation-result";

function Fact({ label, value, mono, wide }: { label: string; value: React.ReactNode; mono?: boolean; wide?: boolean }) {
  return (
    <div className={wide ? "col-span-full min-w-0" : "min-w-0"}>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className={mono ? "font-mono text-sm break-anywhere" : "text-sm break-anywhere"}>{value === "" || value === null || value === undefined ? <span className="text-muted-foreground">-</span> : value}</dd>
    </div>
  );
}

function text(v: JsonValue | unknown): string {
  return asText(v);
}

function LineItemsTable({ lines, currency }: { lines: SourceLine[]; currency?: string | null }) {
  if (lines.length === 0) return <p className="text-sm text-muted-foreground">No line items.</p>;
  return (
    <div className="overflow-x-auto rounded-md border">
      <table className="w-full text-sm" data-testid="line-items">
        <caption className="sr-only">Line items</caption>
        <thead className="bg-muted/40 text-xs text-muted-foreground">
          <tr>
            {["Line", "Material", "Description", "Group", "Quantity", "Unit", "Unit price", "Line total", "Plant", "Tax"].map((h) => (
              <th key={h} scope="col" className="px-2 py-1.5 text-left font-semibold whitespace-nowrap">
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {lines.map((l, i) => {
            const q = asNumber(l.quantity);
            const p = asNumber(l.unitPrice);
            return (
              <tr key={i} className="border-t align-top">
                <td className="px-2 py-1.5 tabular-nums">{text(l.lineNo)}</td>
                <td className="px-2 py-1.5 font-mono text-xs break-anywhere">{text(l.materialCode) || <span className="text-red-600">missing</span>}</td>
                <td className="max-w-[18rem] px-2 py-1.5 break-anywhere">{text(l.description)}</td>
                <td className="px-2 py-1.5">{text(l.materialGroup)}</td>
                <td className="px-2 py-1.5 tabular-nums">{q !== null && q <= 0 ? <span className="text-red-600">{q}</span> : text(l.quantity)}</td>
                <td className="px-2 py-1.5">{text(l.uom)}</td>
                <td className="px-2 py-1.5 tabular-nums">{p !== null ? fmtAmount(p, currency) : text(l.unitPrice)}</td>
                <td className="px-2 py-1.5 tabular-nums">{asNumber(l.lineTotal) !== null ? fmtAmount(asNumber(l.lineTotal), currency) : q !== null && p !== null ? fmtAmount(q * p, currency) : "-"}</td>
                <td className="px-2 py-1.5">{text(l.plant)}</td>
                <td className="px-2 py-1.5">{text(l.taxCode)}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function SimpleTable({ caption, headers, rows, empty, testId }: { caption: string; headers: string[]; rows: React.ReactNode[][]; empty: string; testId?: string }) {
  if (rows.length === 0) return <p className="text-sm text-muted-foreground">{empty}</p>;
  return (
    <div className="overflow-x-auto rounded-md border">
      <table className="w-full text-sm" data-testid={testId}>
        <caption className="sr-only">{caption}</caption>
        <thead className="bg-muted/40 text-xs text-muted-foreground">
          <tr>
            {headers.map((h) => (
              <th key={h} scope="col" className="px-2 py-1.5 text-left font-semibold whitespace-nowrap">
                {h}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i} className="border-t align-top">
              {r.map((c, j) => (
                <td key={j} className="px-2 py-1.5 break-anywhere">
                  {c}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function DetailSkeleton() {
  return (
    <div role="status" aria-busy="true" aria-label="Loading requisition" data-testid="loading" className="space-y-3">
      <Skeleton className="h-8 w-64" />
      <Skeleton className="h-24 w-full" />
      <Skeleton className="h-64 w-full" />
    </div>
  );
}

export function RequisitionDetailView({ id }: { id: string }) {
  const qc = useQueryClient();
  const query = useQuery({ queryKey: qk.requisition(id), queryFn: () => api.requisition(id) });
  const rows = qc.getQueryData<RequisitionRow[]>(qk.requisitions);
  const row = rows?.find((r) => r.id === id);

  const replay = useLabAction<void, WorkspaceRun>({
    name: `Replay ${id}`,
    run: () => api.replay([id]),
    invalidate: [WORKSPACE_KEY],
    confirm: (run) => `Run ${run.id} (${run.kind}) accepted, state ${run.state}. The outcome below updates when the lab reports the run finished.`,
  });

  if (query.isPending) return <DetailSkeleton />;
  if (query.isError) {
    if (isApiError(query.error) && query.error.kind === "not-found") {
      return (
        <EmptyState
          title={`Requisition ${id} not found`}
          description="Neither the lab source nor the integration database has a requisition with this ID. It may have been removed by a workspace reset."
          action={
            <Button asChild variant="outline" size="sm">
              <Link href="/requisitions">Back to requisitions</Link>
            </Button>
          }
        />
      );
    }
    return <ErrorPanel error={query.error} title={`Could not load ${id}`} onRetry={() => void query.refetch()} />;
  }

  const d: RequisitionDetail = query.data;
  const outcome = row?.outcome ?? deriveOutcome(d);
  const style = outcomeStyle(outcome);
  const source = d.source;
  const lines = Array.isArray(source?.lines) ? (source.lines as SourceLine[]) : [];
  const currency = typeof source?.currency === "string" ? source.currency : null;
  const openQuarantine = d.quarantine.filter((q) => !q.resolvedAt);
  const conflict = replay.phase === "failed" && isApiError(replay.error) && replay.error.kind === "conflict";

  return (
    <div className="space-y-4">
      <PageHeader
        title={`Requisition ${id}`}
        description={
          <span className="flex flex-wrap items-center gap-2">
            <OutcomeBadge outcome={outcome} />
            {style.synchronised ? (
              <ToneBadge tone="success">Synchronised with SAP</ToneBadge>
            ) : (
              <ToneBadge tone="muted" title={style.description}>
                Not synchronised
              </ToneBadge>
            )}
            {d.record?.sapNumber ? (
              <span className="font-mono text-xs">
                SAP {d.record.sapNumber}
                {d.record.erpStatus ? ` · ${d.record.erpStatus}` : ""}
              </span>
            ) : null}
          </span>
        }
        actions={
          <>
            <Button asChild variant="ghost" size="sm">
              <Link href="/requisitions">
                <ArrowLeft /> Back to list
              </Link>
            </Button>
            <Button asChild variant="outline" size="sm">
              <Link href={`/requisitions/${encodeURIComponent(id)}/edit`}>
                <Pencil /> Edit
              </Link>
            </Button>
            <Button asChild variant="outline" size="sm">
              <Link href={`/requisitions/new?clone=${encodeURIComponent(id)}`}>
                <Copy /> Clone
              </Link>
            </Button>
            <Button size="sm" onClick={() => void replay.execute()} disabled={replay.pending} aria-busy={replay.pending}>
              <RotateCw className={replay.pending ? "animate-spin" : undefined} /> Replay through pipeline
            </Button>
          </>
        }
      />

      <ActionStatus phase={replay.phase} message={replay.message} />
      {conflict ? (
        <Alert variant="warning" data-testid="run-conflict">
          <AlertTitle>A run is already in progress (HTTP 409)</AlertTitle>
          <AlertDescription>
            The integration runs one sync at a time. Nothing was replayed. Wait for the current run to finish (see the{" "}
            <Link className="underline" href="/control">
              control center
            </Link>
            ) and replay again.
          </AlertDescription>
        </Alert>
      ) : null}

      {!style.synchronised && (style.category === "quarantined" || style.category === "skipped") ? (
        <Alert variant={style.category === "quarantined" ? "destructive" : "warning"} data-testid="never-synchronised">
          <ShieldAlert />
          <AlertTitle>{style.category === "quarantined" ? "Quarantined: never synchronised" : "Skipped: never synchronised"}</AlertTitle>
          <AlertDescription>
            {style.category === "quarantined"
              ? `This requisition has not been sent to SAP. ${openQuarantine[0]?.summary ?? ""} Correct it in the source (Edit) and replay, or resolve it in Quarantine.`
              : "This requisition is not approved, so the integration does not send it to SAP. It is accounted for, not synchronised."}
          </AlertDescription>
        </Alert>
      ) : null}

      <Tabs defaultValue="overview">
        <TabsList aria-label="Requisition detail sections">
          <TabsTrigger value="overview">Overview</TabsTrigger>
          <TabsTrigger value="payloads">Payloads</TabsTrigger>
          <TabsTrigger value="timeline">Timeline ({d.timeline.length})</TabsTrigger>
          <TabsTrigger value="history">Status history ({d.statusHistory.length})</TabsTrigger>
          <TabsTrigger value="quarantine">Quarantine ({d.quarantine.length})</TabsTrigger>
          <TabsTrigger value="http">HTTP ({d.http.length})</TabsTrigger>
        </TabsList>

        <TabsContent value="overview" className="space-y-4">
          <div className="grid gap-4 lg:grid-cols-2">
            <Card>
              <CardHeader>
                <CardTitle>Source record</CardTitle>
                <CardDescription>{source ? `Version ${text(source.version)} in the lab source` : "Not present in the lab source"}</CardDescription>
              </CardHeader>
              <CardContent>
                <dl className="grid grid-cols-2 gap-3 sm:grid-cols-3">
                  <Fact label="Status" value={text(source?.status)} />
                  <Fact label="Requester" value={text(source?.requester)} />
                  <Fact label="Department" value={text(source?.department)} />
                  <Fact label="Company code" value={text(source?.companyCode)} mono />
                  <Fact label="Cost center" value={text(source?.costCenter)} mono />
                  <Fact label="Currency" value={text(source?.currency)} mono />
                  <Fact label="Total amount" value={asNumber(source?.totalAmount) !== null ? fmtAmount(asNumber(source?.totalAmount), currency) : text(source?.totalAmount)} />
                  <Fact label="Needed by" value={typeof source?.neededBy === "string" ? fmtDateOnly(source.neededBy) : text(source?.neededBy)} />
                  <Fact label="Approved at" value={typeof source?.approvedAt === "string" ? fmtDateTime(source.approvedAt) : text(source?.approvedAt)} />
                </dl>
                <dl className="mt-3">
                  <dt className="text-xs text-muted-foreground">Justification</dt>
                  <dd className="text-sm whitespace-pre-wrap break-anywhere">{text(source?.justification) || "-"}</dd>
                </dl>
              </CardContent>
            </Card>
            <Card>
              <CardHeader>
                <CardTitle>Integration record</CardTitle>
                <CardDescription>{d.record ? "State held by the integration database" : "No run has processed this requisition yet"}</CardDescription>
              </CardHeader>
              <CardContent>
                {d.record ? (
                  <dl className="grid grid-cols-2 gap-3 sm:grid-cols-3">
                    <Fact label="State" value={d.record.state} />
                    <Fact label="SAP number" value={d.record.sapNumber} mono />
                    <Fact label="ERP status" value={d.record.erpStatus} />
                    <Fact label="Source version" value={d.record.sourceVersion} />
                    <Fact label="Synced version" value={d.record.syncedSourceVersion} />
                    <Fact label="Attempts" value={d.record.attempts} />
                    <Fact label="Next attempt" value={d.record.nextAttemptAt ? fmtDateTime(d.record.nextAttemptAt) : null} />
                    <Fact label="Write-back pending" value={d.record.writebackPending ? "Yes" : "No"} />
                    <Fact label="Updated" value={fmtDateTime(d.record.updatedAt)} />
                    <Fact label="Last error" value={d.record.lastError} wide />
                  </dl>
                ) : (
                  <p className="text-sm text-muted-foreground">Not processed. Run a sync from the control center to send it through the pipeline.</p>
                )}
              </CardContent>
            </Card>
          </div>

          <DuplicateDecisionPanel decision={d.duplicateDecision} />

          <Card>
            <CardHeader>
              <CardTitle>Validation (current source)</CardTitle>
              <CardDescription>The integration&apos;s rules applied now to the source record, field by field.</CardDescription>
            </CardHeader>
            <CardContent>
              {d.validation ? <ValidationResultPanel result={d.validation} showPayload={false} /> : <p className="text-sm text-muted-foreground">No source record to validate.</p>}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Line items ({lines.length})</CardTitle>
            </CardHeader>
            <CardContent>
              <LineItemsTable lines={lines} currency={currency} />
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="payloads">
          <PayloadCompare detail={d} />
        </TabsContent>

        <TabsContent value="timeline">
          <SimpleTable
            caption="Run events"
            testId="timeline"
            empty="No run events for this requisition yet."
            headers={["At", "Run", "Event", "Detail", "SAP responses"]}
            rows={d.timeline.map((e) => [
              <span key="at" className="whitespace-nowrap tabular-nums">{fmtDateTime(e.at)}</span>,
              <span key="run" className="font-mono text-xs">{e.run}</span>,
              <ToneBadge key="k" tone={e.kind === "Quarantined" ? "danger" : e.kind === "Created" ? "success" : e.kind === "AlreadyInSap" ? "info" : e.kind.includes("Retry") ? "warning" : "neutral"}>
                {e.kind}
              </ToneBadge>,
              e.detail ?? "",
              <span key="s" className="font-mono text-xs">{e.sapResponses ?? ""}</span>,
            ])}
          />
        </TabsContent>

        <TabsContent value="history">
          <SimpleTable
            caption="SAP status history"
            testId="status-history"
            empty="No SAP status recorded: the requisition has no SAP document linked."
            headers={["Changed in SAP", "Recorded", "SAP number", "From", "To", "Run"]}
            rows={d.statusHistory.map((h) => [
              fmtDateTime(h.changedAt),
              fmtDateTime(h.recordedAt),
              <span key="n" className="font-mono text-xs">{h.sapNumber ?? ""}</span>,
              h.fromStatus ?? "(none)",
              h.toStatus ?? "",
              <span key="r" className="font-mono text-xs">{h.runId ?? ""}</span>,
            ])}
          />
        </TabsContent>

        <TabsContent value="quarantine" className="space-y-3">
          {d.quarantine.length === 0 ? (
            <p className="text-sm text-muted-foreground">Never quarantined.</p>
          ) : (
            d.quarantine.map((q) => (
              <Card key={q.id} data-testid="quarantine-entry">
                <CardHeader>
                  <CardTitle className="flex flex-wrap items-center gap-2">
                    <ToneBadge tone={q.resolvedAt ? "muted" : "danger"}>{q.resolvedAt ? "Resolved" : "Open"}</ToneBadge>
                    <span className="font-mono">{q.reason}</span>
                    <span className="text-xs font-normal text-muted-foreground">stage {q.stage} · v{q.sourceVersion} · {q.occurrences} occurrence(s)</span>
                  </CardTitle>
                  <CardDescription className="break-anywhere">{q.summary}</CardDescription>
                </CardHeader>
                <CardContent className="space-y-2 text-sm">
                  <dl className="grid grid-cols-2 gap-3 sm:grid-cols-4">
                    <Fact label="First seen" value={fmtDateTime(q.firstSeenAt)} />
                    <Fact label="Last seen" value={fmtDateTime(q.lastSeenAt)} />
                    <Fact label="Resolved" value={q.resolvedAt ? fmtDateTime(q.resolvedAt) : null} />
                    <Fact label="Resolution" value={q.resolution} />
                  </dl>
                  {q.details ? <JsonViewer value={q.details} maxHeight={220} label="Quarantine details" /> : null}
                </CardContent>
              </Card>
            ))
          )}
          {openQuarantine.length > 0 ? (
            <Button asChild variant="outline" size="sm">
              <Link href="/quarantine">Open in quarantine</Link>
            </Button>
          ) : null}
        </TabsContent>

        <TabsContent value="http">
          <p className="mb-2 text-xs text-muted-foreground">Exchanges whose URL or request body mention {id} (latest 200), in order.</p>
          <HttpExchangeList exchanges={d.http} empty="No HTTP exchange mentions this requisition yet." />
        </TabsContent>
      </Tabs>
    </div>
  );
}
