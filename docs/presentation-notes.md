# Presentation notes

Speaker notes for presenting the solution and answering questions. Aim for about 15 minutes of talk and demo, then Q&A.

## 1. Opening (1 min)

- **The problem in one line:** move approved requisitions into SAP and keep their status in sync, without ever losing one or creating one twice, while both systems misbehave.
- **The approach in one line:** *at-least-once delivery with idempotent effects.* Every step can safely run again, and a reconciliation report proves the result against SAP after every run.
- **Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core, Polly v8 via `Microsoft.Extensions.Http.Resilience`, xUnit v3. SQLite by default, with SQL Server one setting away.

## 2. Architecture (3 min): show `docs/architecture.md` §1-§3

- **Three external parties:** the source API, SAP, and an AI assistant. One integration service in the middle.
- **One run:** extract (paged, checkpointed) → validate or quarantine → outbox → dispatch → status sync → write-back → reconcile.
- **The mocks are deliberately hostile:** malformed records, page overlap, `429`s, `503`s, responses lost *after* SAP has committed, ETag conflicts, a release lock.

## 3. The guarantees (4 min)

**No duplicates: three layers** (ADR-0001)
1. A record with a SAP number is only ever amended.
2. A deterministic `Idempotency-Key` per (id, version).
3. Look SAP up by external reference before every create.

Point to the test that shows **why layer 3 exists**: with key support off, a blind retry *does* duplicate.

**The POST rule** (ADR-0006): the transport never repeats a POST whose outcome is unknown. It goes `InDoubt`, and SAP is checked first.

**Nothing silently dropped:** every approved requisition ends up synced, quarantined with a reason, dismissed by an operator, or visibly pending. The report checks this every run.

**Ownership** (ADR-0002): the source owns content, SAP owns status. Statuses only move forward. Late changes and cancellations go to a person.

## 4. Demo (5 min): `scripts/demo.sh` (or `demo.ps1`)

| Step | What to say |
|---|---|
| 2-3 Chaos run | "10% 503s, 5% 429s, 3% lost responses. Watch the counters: retries, throttling, in-doubt resolved. All 184 still land, zero duplicates." |
| 4 Rerun | "Nothing new is created. The only records read are our own write-backs, recognised as unchanged by content hash." |
| 5 Quarantine | "Every failure has a reason code, the exact field, and the raw payload." |
| 6 Fix in source | "Fix the data at the source and the next run releases it automatically." |
| 7 SAP progresses | "Status flows back: SAP number, PO number and a plain-language message, written with `If-Match`." |
| 8-9 Assistant | "Validate first, actionable issues back. Create with a key. Nothing reaches SAP until a person approves." |
| 10 Report | Open `reports/latest.html`: invariants, totals per currency, failures with next steps. |

**Backup if the live demo fails:** [`reports/sample/chaos-run.md`](../reports/sample/chaos-run.md) and `dotnet test`.

## 5. AI contract (2 min): `api/README.md`

- **Draft-only.** Approval stays human, and there is no approve or price operation at all.
- **No invented data.** Prices come from the catalogue; unknown fields and quoted numbers are rejected; the schema publishes the limits.
- **Server-side controls:** scopes, ownership taken from the token, spend cap, per-user rate limit, mandatory idempotency key, plus look-before-create against the source.
- **Errors an agent can act on:** a stable `code`, `retryable`, and a `suggestedAction`.
- **MCP-ready:** `x-mcp` hints per operation, and `mcp-tools.json` generated from them. Withdraw is the only destructive tool.

## 6. What the tests caught (1 min): good stories for "how do you work?"

- **Needless SAP updates after losing local state.** Our own write-back bumps the source version without changing content, so a rebuild sent 184 identical `PATCH`es. Fixed by comparing content, not version numbers (S05).
- **A rejected request whose response was lost.** The lookup correctly finds nothing; the record is resent next run and quarantined. That's the right behaviour, so the test now asserts the guarantee rather than a fixed count (S04).
- **A dropped connection in the Assistant API** surfaced as a `500` instead of a retryable `503` (A08).
- **The OpenAPI schema advertised "number or string"** for quantity, because .NET's web defaults accept quoted numbers. Strict handling is now on and published (A04, A09).
- **A vulnerable transitive package** from the SQL Server driver was pinned to a patched version instead of suppressing the warning.

---

## Likely questions

**Why poll instead of webhooks or events?**
The source only offers a paged read API. Polling with a durable cursor is crash-safe and simple to reason about. If the source gains webhooks or CDC, they feed the same outbox and nothing downstream changes (ADR-0005).

**Is this exactly-once?**
No system over a network is. Delivery is at-least-once and every effect is idempotent, so the *observable* outcome is exactly-once. The reconciliation scan proves it per run.

**Real SAP doesn't support `Idempotency-Key`. Then what?**
That's why layer 3 exists: look up by external reference before create, and again after any unknown outcome. The mock can switch key support off, and S04 passes both ways.

**Why not just retry everything with Polly?**
Because retrying a POST after a timeout is how integrations create duplicates. The classifier allows retries only when the server certainly did nothing (`429`/`503`) or when repeating is harmless (GET, PUT, conditional PATCH).

**What happens if the process crashes mid-run?**
- Each source page and its checkpoint commit together, so at most one page is re-read.
- A record is saved as `Sending` *before* the SAP call, so after a crash it becomes `InDoubt` and SAP is checked before anything is resent.
- The lease expires, so the next run can start.

**How would this scale to a million requisitions or several workers?**
- Partition by company code.
- Move the outbox to Azure Service Bus with sessions keyed by requisition id (ordering per requisition, competing consumers).
- Move the store to SQL Server with `READPAST`/`UPDLOCK` work queues.
- Batch the SAP lookups per page.

The dispatch logic already assumes at-least-once delivery, so it doesn't change.

**How do you handle load balancing and high availability?**
- **APIs:** stateless instances behind APIM or YARP, once the idempotency keys move to a shared store.
- **Worker:** one active runner per partition through the lease; another node takes over when the lease expires.
- **Health:** probes on `/health`.

**What about the SAP rate limit across several workers?**
The client token bucket is per process. With several workers you'd either share a distributed limiter or split the SAP quota per partition. Server `429`s are still honoured with `Retry-After` either way.

**What would change for real SAP?**
- **API and protocol:** OData V4 `API_PURCHASEREQUISITION_2` (or V2 `API_PURCHASEREQ_PROCESS_SRV`), with the `x-csrf-token` handshake and `$batch`.
- **Authentication:** OAuth 2.0 or X.509 through SAP BTP.
- **Data:** proper account assignment and texts.
- **Alternatives:** SAP Integration Suite or the Azure Logic Apps SAP connector.

The pipeline, outbox, quarantine and reconciliation stay the same.

**Why SQLite?**
Reviewers need only the .NET SDK. The model is provider-neutral and SQL Server is a configuration switch. In production, use migration bundles instead of `EnsureCreated` (ADR-0003).

**How do you stop the AI from doing damage?**
- **Least agency:** drafts only, a person approves.
- **Controls the server enforces:** scopes, ownership, spend cap, rate limit, strict schema, catalogue pricing.
- **Retry safety:** idempotency keys plus look-before-create.
- **Confirmation:** destructive operations are flagged so the MCP client asks the user.
- **Audit:** channel and session reference stored.

That follows OWASP LLM06 *Excessive Agency*.

**What if the assistant is prompt-injected into creating many requests?**
- Each one is still only a draft that a person must approve.
- The per-user rate limit caps volume, and each request is capped in value.
- Everything is attributed to the user and the assistant session.

**How are version conflicts resolved?**
See ADR-0002.
- The source owns content and SAP owns status.
- On `412`: re-read, re-decide, re-apply only our own fields.
- After SAP release, content can't change, so the record goes to quarantine with instructions.
- Statuses never move backwards.

**How does someone operate this day to day?**
- Read `reports/latest.html`.
- Use `procsync quarantine list/show/replay/resolve` and `procsync status <id>`.
- Alert on breaker openings, growth in open quarantine, pending records above a threshold, and run duration (OpenTelemetry to Application Insights).

**How does this relate to Dynamics 365 or SharePoint?**
The same shape applies: a typed client for the Dataverse Web API or Microsoft Graph, the same resilience handler (both throttle with `429` + `Retry-After`), ETag concurrency (Dataverse supports `If-Match`), the outbox, quarantine and reconciliation.

**What would you do next?**
1. Batch the SAP lookups.
2. Add a SQL Server CI job.
3. Ship migration bundles.
4. Add OpenTelemetry export.
5. Use a shared idempotency store for the Assistant API.
6. Add an outbox relay to Service Bus.
7. Containerise and deploy to Azure Container Apps.

## Numbers to remember

- **Seed data:** 250 requisitions. 207 approved, of which 184 are valid, 17 malformed (10 defect types) and 6 rejected by SAP; 43 not approved.
- **Clean run:** 184 created, 23 quarantined, 0 duplicates. A rerun creates nothing.
- **Chaos sample:** 69 retries, 23 throttled, 4 lost responses resolved, every invariant passing.
- **Tests:** 91 in total (46 unit, 45 scenario), about 30 seconds.
- **Assistant API:** 8 operations; 1 destructive (withdraw); spend cap 25,000; 60 requests per minute per user.
