"use client";

import { useCallback, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { BookOpen, Building2, Factory, List, Search, ShieldOff } from "lucide-react";
import { PageHeader } from "@/components/page-header";
import { ActionStatus } from "@/components/action-status";
import { ErrorPanel } from "@/components/error-panel";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { HttpStatusBadge, ToneBadge } from "@/components/status-badge";
import { ResponsePanel } from "@/components/assistant/response-panel";
import { DraftBuilder } from "@/components/assistant/draft-builder";
import { CallBuilder, type CallPreset } from "@/components/assistant/call-builder";
import { GuidedTests, pickActor } from "@/components/assistant/guided-tests";
import { SCOPES, codeOf, type CallFn, type CallRecord, type Reference } from "@/components/assistant/assistant-lib";
import type { ActionPhase } from "@/hooks/use-lab-action";
import { api, isApiError, qk } from "@/lib/api";
import { fmtTime } from "@/lib/format";
import type { AssistantCallRequest } from "@/lib/types";

const HISTORY = 50;

async function fetchReference(user: string): Promise<Reference> {
  const get = async <T,>(path: string): Promise<T[]> => {
    const r = await api.assistantCall({ user, method: "GET", path, headers: {}, anonymous: false });
    if (r.status !== 200) throw new Error(`${path} answered HTTP ${r.status}`);
    const parsed = JSON.parse(r.body) as unknown;
    return Array.isArray(parsed) ? (parsed as T[]) : [];
  };
  const [materials, costCenters, plants] = await Promise.all([
    get<Reference["materials"][number]>("/v1/reference/materials?search=&limit=50"),
    get<Reference["costCenters"][number]>("/v1/reference/cost-centers"),
    get<Reference["plants"][number]>("/v1/reference/plants"),
  ]);
  return { materials, costCenters, plants };
}

export default function AssistantPage() {
  const qc = useQueryClient();
  const users = useQuery({ queryKey: qk.assistantUsers, queryFn: api.assistantUsers, staleTime: 5 * 60_000 });
  const [user, setUser] = useState("E1002");
  const [overrideScopes, setOverrideScopes] = useState(false);
  const [scopes, setScopes] = useState<string[]>(["requisitions:read", "requisitions:draft"]);
  const [anonymous, setAnonymous] = useState(false);
  const [history, setHistory] = useState<CallRecord[]>([]);
  const [selected, setSelected] = useState<CallRecord | null>(null);
  const [phase, setPhase] = useState<{ phase: ActionPhase; message?: string }>({ phase: "idle" });
  const [reqId, setReqId] = useState("");
  const [search, setSearch] = useState("");
  const [tab, setTab] = useState("quick");
  const [preset, setPreset] = useState<{ n: number; value: CallPreset } | null>(null);
  const busyRef = useRef(false);
  const [busy, setBusy] = useState(false);

  const userList = users.data ?? [];
  const actor = pickActor(user, userList);
  const reference = useQuery({ queryKey: ["assistant-reference", actor], queryFn: () => fetchReference(actor), staleTime: 5 * 60_000, retry: 0 });
  const me = userList.find((u) => u.id === user);

  // Never throws: transport failures are recorded as an unknown outcome.
  const perform = useCallback(
    async (label: string, req: Partial<AssistantCallRequest> & Pick<AssistantCallRequest, "method" | "path">, defaults: Pick<AssistantCallRequest, "user" | "scopes" | "anonymous">): Promise<CallRecord> => {
      const request: AssistantCallRequest = {
        user: req.user ?? defaults.user,
        scopes: req.scopes !== undefined ? req.scopes : defaults.scopes,
        anonymous: req.anonymous ?? defaults.anonymous,
        method: req.method,
        path: req.path,
        headers: req.headers ?? {},
        body: req.body ?? null,
      };
      const record: CallRecord = { id: `${Date.now()}-${Math.random().toString(16).slice(2)}`, at: new Date().toISOString(), label, request };
      try {
        record.response = await api.assistantCall(request);
      } catch (e) {
        record.error = isApiError(e) && e.outcomeUnknown ? `No answer from the lab (${e.message}). If this was a create, it may or may not have been applied: list your requisitions to check.` : e instanceof Error ? e.message : String(e);
      }
      setHistory((h) => [record, ...h].slice(0, HISTORY));
      setSelected(record);
      if (request.method !== "GET") void qc.invalidateQueries({ queryKey: qk.requisitions });
      return record;
    },
    [qc],
  );

  // One at a time, so a double click cannot send twice.
  const call: CallFn = useCallback(
    async (label, req) => {
      if (busyRef.current) return { id: "ignored", at: new Date().toISOString(), label, request: { user, method: req.method, path: req.path, headers: {}, anonymous } };
      busyRef.current = true;
      setBusy(true);
      setPhase({ phase: "sending", message: `${label}: calling the assistant API...` });
      try {
        const r = await perform(label, req, { user, scopes: overrideScopes ? scopes : null, anonymous });
        if (r.response) setPhase({ phase: "confirmed", message: `${label}: the assistant API answered HTTP ${r.response.status}${codeOf(r) ? ` ${codeOf(r)}` : ""} (shown on the right).` });
        else setPhase({ phase: "unknown", message: r.error });
        return r;
      } finally {
        busyRef.current = false;
        setBusy(false);
      }
    },
    [perform, user, overrideScopes, scopes, anonymous],
  );

  const testCall: CallFn = useCallback((label, req) => perform(label, req, { user: actor, scopes: null, anonymous: false }), [perform, actor]);

  return (
    <div className="space-y-4">
      <PageHeader
        title="AI assistant playground"
        description="Call the real assistant API as a test employee, the way an AI agent would. Requests are signed with lab-only dev tokens and land in the isolated lab source. Approval is a human step: nothing here can approve a requisition."
      />
      {users.isError ? <ErrorPanel error={users.error} onRetry={() => void users.refetch()} /> : null}

      <Card>
        <CardHeader>
          <CardTitle>Identity</CardTitle>
          <CardDescription>Who the assistant acts for. The dev token carries this user&apos;s id and scopes.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4 md:grid-cols-[minmax(0,1fr)_minmax(0,1.2fr)_auto]">
          <div className="space-y-1">
            <Label htmlFor="assistant-user">Test user</Label>
            <NativeSelect id="assistant-user" value={user} onChange={(e) => setUser(e.target.value)} disabled={anonymous}>
              {userList.length === 0 ? <option value={user}>{user}</option> : null}
              {userList.map((u) => (
                <option key={u.id} value={u.id}>
                  {u.id} · {u.name} · {u.department}
                </option>
              ))}
            </NativeSelect>
            {me ? (
              <div className="flex flex-wrap gap-1.5 pt-1">
                <ToneBadge tone={me.canRaise ? "success" : "muted"}>{me.canRaise ? "can raise requests" : "cannot raise requests"}</ToneBadge>
                {me.procurementDelegate ? <ToneBadge tone="info">procurement delegate (reads all)</ToneBadge> : null}
              </div>
            ) : null}
          </div>
          <fieldset className="space-y-2">
            <legend className="sr-only">Scopes</legend>
            <div className="flex items-center gap-2">
              <Switch id="assistant-override-scopes" checked={overrideScopes} onCheckedChange={setOverrideScopes} disabled={anonymous} />
              <Label htmlFor="assistant-override-scopes">Override token scopes</Label>
            </div>
            <div className="flex flex-wrap gap-3">
              {SCOPES.map((s) => (
                <div key={s} className="flex items-center gap-1.5">
                  <Checkbox
                    id={`scope-${s}`}
                    disabled={!overrideScopes || anonymous}
                    checked={scopes.includes(s)}
                    onCheckedChange={(v) => setScopes((cur) => (v === true ? [...new Set([...cur, s])] : cur.filter((x) => x !== s)))}
                  />
                  <Label htmlFor={`scope-${s}`} className="font-mono">
                    {s}
                  </Label>
                </div>
              ))}
            </div>
            <p className="text-[11px] text-muted-foreground">Off: the scopes the directory grants this user.</p>
          </fieldset>
          <div className="flex items-start gap-2">
            <Switch id="assistant-anonymous" checked={anonymous} onCheckedChange={setAnonymous} />
            <div>
              <Label htmlFor="assistant-anonymous">Anonymous</Label>
              <p className="text-[11px] text-muted-foreground">Send no bearer token.</p>
            </div>
          </div>
        </CardContent>
      </Card>

      <ActionStatus phase={phase.phase} message={phase.message} />

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1.15fr)_minmax(0,1fr)]">
        <Tabs value={tab} onValueChange={setTab} className="min-w-0">
          <TabsList>
            <TabsTrigger value="quick">Quick actions &amp; draft</TabsTrigger>
            <TabsTrigger value="builder">Call builder</TabsTrigger>
            <TabsTrigger value="guided">Guided tests</TabsTrigger>
          </TabsList>
          <TabsContent value="quick" className="space-y-4">
            <Card>
              <CardHeader>
                <CardTitle>Quick actions</CardTitle>
                <CardDescription>Read-only calls an assistant makes before drafting.</CardDescription>
              </CardHeader>
              <CardContent className="space-y-3">
                <div className="flex flex-wrap gap-2">
                  <Button variant="outline" size="sm" disabled={busy} onClick={() => void call("List my requisitions", { method: "GET", path: "/v1/requisition-requests" })}>
                    <List /> List my requisitions
                  </Button>
                  <Button variant="outline" size="sm" disabled={busy} onClick={() => void call("Cost centers", { method: "GET", path: "/v1/reference/cost-centers" })}>
                    <Building2 /> Cost centers
                  </Button>
                  <Button variant="outline" size="sm" disabled={busy} onClick={() => void call("Plants", { method: "GET", path: "/v1/reference/plants" })}>
                    <Factory /> Plants
                  </Button>
                </div>
                <form
                  className="flex flex-wrap items-end gap-2"
                  onSubmit={(e) => {
                    e.preventDefault();
                    void call(`Search materials "${search}"`, { method: "GET", path: `/v1/reference/materials?search=${encodeURIComponent(search)}` });
                  }}
                >
                  <div className="min-w-48 flex-1 space-y-1">
                    <Label htmlFor="assistant-material-search">Material search</Label>
                    <Input id="assistant-material-search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="e.g. tools" />
                  </div>
                  <Button type="submit" variant="outline" size="sm" disabled={busy}>
                    <Search /> Search materials
                  </Button>
                </form>
                <form
                  className="flex flex-wrap items-end gap-2"
                  onSubmit={(e) => {
                    e.preventDefault();
                    if (reqId.trim()) void call(`Get ${reqId.trim()}`, { method: "GET", path: `/v1/requisition-requests/${encodeURIComponent(reqId.trim())}` });
                  }}
                >
                  <div className="min-w-48 flex-1 space-y-1">
                    <Label htmlFor="assistant-req-id">Requisition id</Label>
                    <Input id="assistant-req-id" className="font-mono" value={reqId} onChange={(e) => setReqId(e.target.value)} placeholder="REQ-7002" />
                  </div>
                  <Button type="submit" variant="outline" size="sm" disabled={busy || !reqId.trim()}>
                    <BookOpen /> Get one
                  </Button>
                </form>
              </CardContent>
            </Card>
            {reference.isError ? (
              <ErrorPanel compact title="Reference data unavailable" error={reference.error} onRetry={() => void reference.refetch()} />
            ) : null}
            <DraftBuilder reference={reference.data ?? null} call={call} busy={busy} />
          </TabsContent>
          <TabsContent value="builder">
            <CallBuilder key={preset?.n ?? 0} call={call} busy={busy} preset={preset?.value ?? null} />
          </TabsContent>
          <TabsContent value="guided">
            <GuidedTests selectedUser={user} users={userList} reference={reference.data ?? null} call={testCall} onSelect={setSelected} />
          </TabsContent>
        </Tabs>

        <div className="min-w-0 space-y-4">
          <ResponsePanel
            record={selected}
            onUseToken={(token) => {
              const body = selected?.request.body ?? "";
              setPreset({ n: Date.now(), value: { method: "POST", path: "/v1/requisition-requests", body, headers: { "Idempotency-Key": crypto.randomUUID(), "X-User-Confirmation": token } } });
              setTab("builder");
            }}
          />
          <Card>
            <CardHeader>
              <CardTitle>Call history</CardTitle>
              <CardDescription>The last {HISTORY} calls from this page. Select one to inspect it.</CardDescription>
            </CardHeader>
            <CardContent>
              {history.length === 0 ? (
                <p className="text-sm text-muted-foreground">No calls yet.</p>
              ) : (
                <ol className="space-y-1">
                  {history.map((r) => (
                    <li key={r.id}>
                      <button
                        type="button"
                        aria-current={selected?.id === r.id ? "true" : undefined}
                        className="flex w-full flex-wrap items-center gap-2 rounded px-2 py-1 text-left text-xs hover:bg-muted/50 aria-[current=true]:bg-primary/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                        onClick={() => setSelected(r)}
                      >
                        <span className="tabular-nums text-muted-foreground">{fmtTime(r.at)}</span>
                        {r.response ? <HttpStatusBadge status={r.response.status} /> : <ToneBadge tone="critical">unknown</ToneBadge>}
                        <span className="font-mono">{codeOf(r) ?? ""}</span>
                        <span className="min-w-0 flex-1 truncate">{r.label}</span>
                        <span className="text-muted-foreground">{r.request.anonymous ? "anonymous" : r.request.user}</span>
                      </button>
                    </li>
                  ))}
                </ol>
              )}
            </CardContent>
          </Card>
          <p className="flex items-start gap-2 text-xs text-muted-foreground">
            <ShieldOff className="mt-0.5 size-3.5 shrink-0" aria-hidden />
            There is deliberately no approve action: the assistant API has no approval endpoint, and approval is never delegated to an AI agent.
          </p>
        </div>
      </div>
    </div>
  );
}
