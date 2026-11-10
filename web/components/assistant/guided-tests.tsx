"use client";

import { useRef, useState } from "react";
import { ChevronRight, Loader2, Play, PlayCircle } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { HttpStatusBadge, ToneBadge } from "@/components/status-badge";
import { api } from "@/lib/api";
import type { AssistantUser } from "@/lib/types";
import { cn } from "@/lib/utils";
import {
  MAX_REQUEST_AMOUNT,
  codeOf,
  newIdempotencyKey,
  overCapLine,
  parseBody,
  statusOf,
  validDraft,
  type CallFn,
  type CallRecord,
  type Draft,
  type Reference,
} from "./assistant-lib";

// The limiter is per user, so the burst test uses its own.
const BURST_USER = "E1117";
// The production limit is 60 a minute, so 65 calls must hit it.
const BURST_MAX = 65;
const BURST_CONCURRENCY = 5;

interface Expected {
  status: number;
  code?: string;
  issue?: string;
}

interface Outcome {
  status: number | null;
  code?: string;
  issues?: string[];
  note?: string;
}

interface Ctx {
  call: CallFn;
  actor: string;
  owner: () => Promise<{ user: string; id: string } | null>;
  ref: Reference;
  draft: Draft;
}

interface GuidedTest {
  id: string;
  name: string;
  description: string;
  expected: Expected;
  run: (ctx: Ctx) => Promise<Outcome>;
}

const outcomeOf = (r: CallRecord, note?: string): Outcome => {
  const p = parseBody(r.response?.body);
  return { status: statusOf(r), code: p.code, issues: [...p.invalidFields, ...p.issues].map((i) => i.code), note: note ?? r.error };
};

const create = (ctx: Ctx, label: string, draft: Draft, headers: Record<string, string>) =>
  ctx.call(label, { method: "POST", path: "/v1/requisition-requests", body: JSON.stringify(draft), headers });

async function validateForToken(ctx: Ctx, draft: Draft): Promise<{ token?: string; record: CallRecord }> {
  const record = await ctx.call("Validate (for a confirmation token)", { method: "POST", path: "/v1/requisition-requests:validate", body: JSON.stringify(draft) });
  return { token: parseBody(record.response?.body).confirmationToken, record };
}

const TESTS: GuidedTest[] = [
  {
    id: "no-confirmation",
    name: "Create without user confirmation",
    description: "A valid request with an Idempotency-Key but no X-User-Confirmation header. Nothing may be created.",
    expected: { status: 428, code: "CONFIRMATION_REQUIRED" },
    run: async (ctx) => outcomeOf(await create(ctx, "Create without X-User-Confirmation", ctx.draft, { "Idempotency-Key": newIdempotencyKey() })),
  },
  {
    id: "tampered",
    name: "Draft changed after confirmation",
    description: "Validate a draft, then send a different quantity with the token the user confirmed for the original.",
    expected: { status: 422, code: "CONFIRMATION_MISMATCH" },
    run: async (ctx) => {
      const { token, record } = await validateForToken(ctx, ctx.draft);
      if (!token) return { ...outcomeOf(record), note: "Validation returned no confirmation token, so the tampering step could not run." };
      const tampered: Draft = { ...ctx.draft, lines: ctx.draft.lines.map((l, i) => (i === 0 ? { ...l, quantity: l.quantity + 1 } : l)) };
      return outcomeOf(await create(ctx, "Create tampered draft with the original token", tampered, { "Idempotency-Key": newIdempotencyKey(), "X-User-Confirmation": token }));
    },
  },
  {
    id: "token-reuse",
    name: "Confirmation reused with a new key",
    description: "Create once with a confirmation (this raises one PendingApproval request in the lab source), then send the same token with a new Idempotency-Key.",
    expected: { status: 409, code: "CONFIRMATION_ALREADY_USED" },
    run: async (ctx) => {
      const { token, record } = await validateForToken(ctx, ctx.draft);
      if (!token) return { ...outcomeOf(record), note: "Validation returned no confirmation token." };
      const first = await create(ctx, "Create with confirmation (first use)", ctx.draft, { "Idempotency-Key": newIdempotencyKey(), "X-User-Confirmation": token });
      const second = await create(ctx, "Create again: same token, new key", ctx.draft, { "Idempotency-Key": newIdempotencyKey(), "X-User-Confirmation": token });
      return outcomeOf(second, `First use answered HTTP ${statusOf(first) ?? "none"}.`);
    },
  },
  {
    id: "cross-user",
    name: "Read another user's requisition",
    description: "Find a requisition owned by a different user, then read it as the test user.",
    expected: { status: 403, code: "NOT_YOUR_REQUISITION" },
    run: async (ctx) => {
      const owner = await ctx.owner();
      if (!owner) return { status: null, note: "No other user with a requisition was found, so the test could not run." };
      const r = await ctx.call(`Read ${owner.id} (owned by ${owner.user})`, { method: "GET", path: `/v1/requisition-requests/${encodeURIComponent(owner.id)}` });
      return outcomeOf(r, `${owner.id} belongs to ${owner.user}; read as ${ctx.actor}.`);
    },
  },
  {
    id: "spend-cap",
    name: "Over the spend cap",
    description: `A request whose catalogue total exceeds the ${MAX_REQUEST_AMOUNT.toLocaleString("en")} limit for assistant-raised requests. The create is refused before any confirmation is checked.`,
    expected: { status: 422, code: "VALIDATION_FAILED", issue: "SPEND_LIMIT_EXCEEDED" },
    run: async (ctx) => {
      const line = overCapLine(ctx.ref);
      if (!line) return { status: null, note: "No catalogue material can exceed the cap within the 1000-per-line quantity limit." };
      const draft: Draft = { ...ctx.draft, lines: [line] };
      return outcomeOf(await create(ctx, `Create ${line.quantity} × ${line.materialCode}`, draft, { "Idempotency-Key": newIdempotencyKey() }));
    },
  },
  {
    id: "unknown-material",
    name: "Unknown material",
    description: "A line with a material code that is not in the catalogue.",
    expected: { status: 422, code: "VALIDATION_FAILED", issue: "UNKNOWN_MATERIAL" },
    run: async (ctx) => {
      const draft: Draft = { ...ctx.draft, lines: [{ materialCode: "NO-SUCH-MATERIAL-404", quantity: 1 }] };
      return outcomeOf(await create(ctx, "Create with an unknown material", draft, { "Idempotency-Key": newIdempotencyKey() }));
    },
  },
  {
    id: "price-injection",
    name: "Price field injection",
    description: "The assistant tries to set its own unitPrice. Undocumented fields are refused, so prices always come from the catalogue.",
    expected: { status: 400, code: "INVALID_REQUEST_BODY" },
    run: async (ctx) => {
      const body = { ...ctx.draft, lines: ctx.draft.lines.map((l) => ({ ...l, unitPrice: 0.01 })) };
      return outcomeOf(await ctx.call("Validate with an injected unitPrice", { method: "POST", path: "/v1/requisition-requests:validate", body: JSON.stringify(body) }));
    },
  },
  {
    id: "rate-limit",
    name: "Rate limit burst",
    description: `Fires reference-data reads as ${BURST_USER} (a dedicated user: the limiter is per user, and that user stays limited for up to a minute) until the API answers 429, up to ${BURST_MAX} calls (the assistant allows 60 requests a minute per user).`,
    expected: { status: 429, code: "RATE_LIMITED" },
    run: async (ctx) => {
      const burst = { sent: 0, ok: 0, failures: 0, limited: null as CallRecord | null };
      const request = { user: BURST_USER, method: "GET" as const, path: "/v1/reference/plants", headers: {}, anonymous: false };
      const worker = async () => {
        while (!burst.limited && burst.sent < BURST_MAX && burst.failures <= 20) {
          burst.sent++;
          try {
            const res = await api.assistantCall(request);
            if (res.status === 429) {
              burst.limited ??= { id: `burst-${Date.now()}`, at: new Date().toISOString(), label: `Burst as ${BURST_USER}: first 429`, request, response: res };
            } else if (res.status >= 200 && res.status < 300) burst.ok++;
          } catch {
            burst.failures++;
          }
        }
      };
      await Promise.all(Array.from({ length: BURST_CONCURRENCY }, worker));
      if (!burst.limited) return { status: null, note: `${burst.sent} calls sent, ${burst.ok} succeeded, ${burst.failures} transport failures; no 429 seen.` };
      const after = await ctx.call(`Call after the burst as ${BURST_USER}`, { user: BURST_USER, method: "GET", path: "/v1/reference/plants" });
      return {
        ...outcomeOf(burst.limited),
        note: `${burst.ok} calls succeeded before the limiter answered 429 (${burst.sent} sent, ${BURST_CONCURRENCY} in parallel). The next call answered HTTP ${statusOf(after) ?? "none"}.`,
      };
    },
  },
  {
    id: "anonymous",
    name: "Anonymous call",
    description: "No bearer token at all.",
    expected: { status: 401, code: "UNAUTHORIZED" },
    run: async (ctx) => outcomeOf(await ctx.call("List requisitions without a token", { method: "GET", path: "/v1/requisition-requests", anonymous: true })),
  },
];

function passes(e: Expected, o: Outcome): boolean {
  if (o.status !== e.status) return false;
  if (e.code && o.code !== e.code) return false;
  if (e.issue && !(o.issues ?? []).includes(e.issue)) return false;
  return true;
}

interface TestState {
  state: "idle" | "running" | "done" | "error";
  outcome?: Outcome;
  steps: CallRecord[];
  error?: string;
}

// Procurement delegates may read everything, so they would hide an access bug.
export function pickActor(selected: string, users: AssistantUser[]): string {
  const u = users.find((x) => x.id === selected);
  if (u && u.canRaise && !u.procurementDelegate && u.id !== "E9999" && u.id !== BURST_USER) return u.id;
  return "E1003";
}

export function GuidedTests({
  selectedUser,
  users,
  reference,
  call,
  onSelect,
}: {
  selectedUser: string;
  users: AssistantUser[];
  reference: Reference | null;
  call: CallFn;
  onSelect: (r: CallRecord) => void;
}) {
  const [states, setStates] = useState<Record<string, TestState>>({});
  const [open, setOpen] = useState<string | null>(null);
  const running = useRef(false);
  const [busy, setBusy] = useState(false);
  const actor = pickActor(selectedUser, users);
  const draft = reference ? validDraft(reference) : null;

  const owner = async (): Promise<{ user: string; id: string } | null> => {
    const candidates = users.filter((u) => u.id !== actor && u.canRaise && !u.procurementDelegate && u.id !== "E9999" && u.id !== BURST_USER).slice(0, 8);
    for (const u of candidates) {
      const r = await call(`List requisitions of ${u.id}`, { user: u.id, method: "GET", path: "/v1/requisition-requests" });
      const json = parseBody(r.response?.body).json as { items?: { id?: string }[] } | null;
      const id = json?.items?.find((i) => typeof i.id === "string")?.id;
      if (id) return { user: u.id, id };
    }
    return null;
  };

  const runOne = async (t: GuidedTest) => {
    if (!draft || !reference) return;
    const steps: CallRecord[] = [];
    setStates((s) => ({ ...s, [t.id]: { state: "running", steps } }));
    const recordingCall: CallFn = async (label, req) => {
      const r = await call(label, { user: actor, ...req });
      steps.push(r);
      setStates((s) => ({ ...s, [t.id]: { state: "running", steps: [...steps] } }));
      return r;
    };
    try {
      const outcome = await t.run({ call: recordingCall, actor, owner, ref: reference, draft });
      setStates((s) => ({ ...s, [t.id]: { state: "done", outcome, steps: [...steps] } }));
    } catch (e) {
      setStates((s) => ({ ...s, [t.id]: { state: "error", steps: [...steps], error: e instanceof Error ? e.message : String(e) } }));
    }
  };

  const run = async (tests: GuidedTest[]) => {
    if (running.current) return;
    running.current = true;
    setBusy(true);
    try {
      for (const t of tests) await runOne(t);
    } finally {
      running.current = false;
      setBusy(false);
    }
  };

  const done = TESTS.filter((t) => states[t.id]?.state === "done");
  const passed = done.filter((t) => passes(t.expected, states[t.id]!.outcome!)).length;

  return (
    <Card>
      <CardHeader>
        <div className="flex flex-wrap items-center justify-between gap-2">
          <CardTitle>Guided contract tests</CardTitle>
          <Button size="sm" disabled={busy || !draft} onClick={() => void run(TESTS)}>
            <PlayCircle /> Run all
          </Button>
        </div>
        <CardDescription>
          Each test makes real calls and compares the API&apos;s answer with the contract. They run as <span className="font-mono">{actor}</span>
          {actor !== selectedUser ? " (the selected user cannot raise requests or may read everything)" : ""}.{" "}
          {done.length > 0 ? `${passed} of ${done.length} run matched the expectation.` : ""}
          {!draft ? " Waiting for reference data to build a valid draft." : ""}
        </CardDescription>
      </CardHeader>
      <CardContent>
        <ul className="space-y-2">
          {TESTS.map((t) => {
            const s = states[t.id];
            const o = s?.outcome;
            const ok = o ? passes(t.expected, o) : null;
            const expanded = open === t.id;
            return (
              <li key={t.id} className="rounded-md border" data-testid={`guided-${t.id}`}>
                <div className="flex flex-wrap items-start gap-2 p-3">
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="text-sm font-medium">{t.name}</span>
                      {s?.state === "running" ? (
                        <ToneBadge tone="info">
                          <Loader2 className="animate-spin" aria-hidden /> running
                        </ToneBadge>
                      ) : s?.state === "error" ? (
                        <ToneBadge tone="critical">could not run</ToneBadge>
                      ) : ok === true ? (
                        <ToneBadge tone="success">Pass</ToneBadge>
                      ) : ok === false ? (
                        <ToneBadge tone="danger">Fail</ToneBadge>
                      ) : (
                        <ToneBadge tone="neutral">Not run</ToneBadge>
                      )}
                    </div>
                    <p className="mt-0.5 text-xs text-muted-foreground">{t.description}</p>
                    <dl className="mt-1 grid grid-cols-[auto_1fr] gap-x-2 gap-y-0.5 text-xs">
                      <dt className="text-muted-foreground">Expected</dt>
                      <dd className="font-mono">
                        {t.expected.status} {t.expected.code ?? ""}
                        {t.expected.issue ? ` (${t.expected.issue})` : ""}
                      </dd>
                      <dt className="text-muted-foreground">Actual</dt>
                      <dd className="font-mono break-anywhere" data-testid={`guided-${t.id}-actual`}>
                        {o ? (
                          <>
                            {o.status ?? "no answer"} {o.code ?? ""}
                            {o.issues && o.issues.length > 0 ? ` (${o.issues.join(", ")})` : ""}
                          </>
                        ) : s?.error ? (
                          s.error
                        ) : (
                          "-"
                        )}
                      </dd>
                      {o?.note ? (
                        <>
                          <dt className="text-muted-foreground">Note</dt>
                          <dd className="break-anywhere">{o.note}</dd>
                        </>
                      ) : null}
                    </dl>
                  </div>
                  <Button size="sm" variant="outline" disabled={busy || !draft} onClick={() => void run([t])} aria-label={`Run ${t.name}`}>
                    <Play /> Run
                  </Button>
                </div>
                {s && s.steps.length > 0 ? (
                  <div className="border-t px-3 py-2">
                    <button
                      type="button"
                      className="flex items-center gap-1 text-xs font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
                      aria-expanded={expanded}
                      onClick={() => setOpen(expanded ? null : t.id)}
                    >
                      <ChevronRight className={cn("size-3.5 transition-transform", expanded && "rotate-90")} aria-hidden />
                      {s.steps.length} call(s)
                    </button>
                    {expanded ? (
                      <ol className="mt-1 space-y-1">
                        {s.steps.map((r) => (
                          <li key={r.id}>
                            <button type="button" className="flex w-full flex-wrap items-center gap-2 rounded px-1 py-0.5 text-left text-xs hover:bg-muted/50" onClick={() => onSelect(r)}>
                              <HttpStatusBadge status={r.response?.status} />
                              <span className="font-mono">{codeOf(r) ?? ""}</span>
                              <span className="min-w-0 flex-1 truncate">{r.label}</span>
                            </button>
                          </li>
                        ))}
                      </ol>
                    ) : null}
                  </div>
                ) : null}
              </li>
            );
          })}
        </ul>
      </CardContent>
    </Card>
  );
}
