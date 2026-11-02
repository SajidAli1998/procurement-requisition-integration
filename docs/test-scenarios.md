# Test scenarios

**91 automated tests:** 46 unit tests and 45 scenario tests. They run in about 30 seconds with `dotnet test`.

The scenario tests host the real source mock, SAP mock and Assistant API in-process (`WebApplicationFactory`) and wire the **production** sync services to them. The resilience pipeline is the real one. Only waits are scaled down (`Resilience:DelayScale = 0.001`), so `Retry-After`, backoff and the circuit breaker are exercised in milliseconds rather than mocked.

```bash
dotnet test                                                                 # everything
dotnet test --project tests/Procurement.ScenarioTests                       # scenarios only
dotnet test --project tests/Procurement.ScenarioTests -- --filter-class "*AssistantApiTests"
```

## Seed data used by the scenarios

250 synthetic requisitions, generated deterministically (seed 42):

| Group | Count | Expected outcome |
|---|---:|---|
| Approved, valid | 184 | Created in SAP once |
| Approved, malformed (10 defect types) | 17 | Quarantined with the matching reason |
| Approved, valid in source but rejected by SAP (plant outside company code) | 6 | Quarantined `SapRejectedPermanent`, not retried |
| Not approved (pending, draft, rejected, cancelled) | 43 | Ignored |

`GET /_admin/seed-manifest` on the source mock lists exactly which records carry which defect, so the tests check the report against ground truth.

| Defect | Quarantine reason |
|---|---|
| `missing_lines` | `MissingLines` |
| `negative_quantity` | `InvalidQuantity` (reported before the resulting `TotalMismatch`) |
| `unknown_currency` | `UnknownCurrency` |
| `unknown_uom` | `UnknownUnitOfMeasure` |
| `past_needed_by` | `NeededByInPast` |
| `quantity_not_number` | `SchemaInvalid` (`lines[0].quantity must be a number, got String "ten"`) |
| `total_mismatch` | `TotalMismatch` |
| `missing_requester` | `MissingRequiredField` |
| `invalid_date` | `SchemaInvalid` |
| `missing_id` | `MissingRequiredField` (keyed `NOID-<hash>`) |
| `plant_company_mismatch` | `SapRejectedPermanent` (SAP's own message kept) |

## Pipeline scenarios (`PipelineScenarioTests`, `StatusAndReportScenarioTests`)

| # | Scenario | Requirement | Expected |
|---|---|---|---|
| S01 | Happy path | Read, transfer, malformed records, quarantine | 184 created, 43 ignored, 23 quarantined with the right reason and raw payload; 0 retries for permanent errors; 0 duplicates |
| S02 | Immediate rerun | Reruns never duplicate | 0 created, 0 amended; only our own write-backs are re-read, as `Unchanged` |
| S03 | Source fails after page 3, then recovers | Pagination, intermittent failures | Run 1 keeps 150 extracted and dispatches them; run 2 resumes from the checkpoint; 184 in SAP, 0 duplicates |
| S04 | 5 lost responses, with **and** without SAP honouring `Idempotency-Key` | Reruns never duplicate, intermittent failures | Each one resolved by lookup (or quarantined if SAP had rejected it); 184 in SAP, 0 duplicates |
| S05 | Local database wiped, rerun | Reruns never duplicate | 0 created, 0 amended, 184 adopted as `AlreadyInSap` |
| S06 | 8 scripted `429`s with `Retry-After: 2` | Rate limits | Throttling recorded, wait time > 0, everything synced |
| S07 | 20% random `503`s | Intermittent failures | Retries recorded; settles to 184 with 0 duplicates |
| S08 | SAP down for the whole run | Intermittent failures | Breaker opens, run ends cleanly with 190 pending; next run completes |
| S09 | SAP advances lifecycle | Status sync | Status changes applied, written back to the source; PO number captured; history kept |
| S10 | A quarantined record is fixed in the source | Quarantine | Released automatically, created in SAP, entry resolved |
| S11 | Content amended while SAP PR is open | Version conflicts | `PATCH` with `If-Match`; SAP holds the new version |
| S12 | Source pages overlap by one record | Pagination | 4 repeats detected as `Unchanged`; 184 created |
| S13 | Content amended after SAP release | Version conflicts | Quarantined `VersionConflictUnresolved`; SAP keeps the earlier version; status `Released` captured |
| S14 | Requisition cancelled after transfer | Version conflicts | Quarantined `SourceChangedAfterTransfer` for a person to act on |
| S15 | A create times out (SAP never processed it) | Intermittent failures | `InDoubt`, re-checked, not re-sent blindly; created on the next run; 0 duplicates |
| S16 | SAP status goes backwards | Status sync | Ignored and logged; source keeps `Released` |
| S17 | A second runner starts while the lease is held | Reruns never duplicate | Skipped |
| S18 | Status write-back hits `412` three times | Version conflicts | Re-reads the source and retries; all written back |
| S19 | Report content | Reconciliation report | 6 invariants pass, totals match per currency, 23 failures each with a suggested action, JSON/MD/HTML written |
| S20 | Someone creates a duplicate directly in SAP | Reconciliation report | "No duplicate requisitions in SAP" **fails**, naming the reference; unlinked record warned |
| S21 | Retries exhausted, SAP recovers, operator replays | Quarantine | Quarantined `RetryExhausted`, then replay creates it and resolves the entry |
| S22 | Operator resolves an entry handled elsewhere | Quarantine, reconciliation | Record `Dismissed`; "accounted for" invariant still passes |

## Assistant API scenarios (`AssistantApiTests`)

| # | Scenario | Expected |
|---|---|---|
| A01 | Validate a request with five mistakes | `valid: false`, one issue per mistake with field path, code and suggestion |
| A02 | Request above the spend cap | `422 VALIDATION_FAILED` with `SPEND_LIMIT_EXCEEDED`, `retryable: false` |
| A03 | Create without a key, retry with the same key, reuse the key with a changed body | `400 IDEMPOTENCY_KEY_REQUIRED`; replay of the same response (`Idempotent-Replayed`); `422 IDEMPOTENCY_KEY_REUSED`; one draft, priced from the catalogue |
| A04 | Unknown field (`unitPrice`), or a number sent as a string | `400 INVALID_REQUEST_BODY` naming the field |
| A05 | No token; read-only token creating; another user reading | `401`; `403 FORBIDDEN`; `404` (existence not revealed) |
| A06 | Withdraw without `If-Match`, with a stale one, valid, and after approval | `428`; `412`; `Withdrawn`; `409 NOT_WITHDRAWABLE` |
| A07 | Full journey | `PendingApproval` (nothing transferred before approval) → approved → `TransferredToErp` with SAP number → `PurchaseOrderCreated` with PO and timeline |
| A08 | The source loses the create response | `503`, `retryable: true`; the retry finds the draft and returns it; exactly one draft exists |
| A09 | OpenAPI contract | Every operation has `operationId`, `x-mcp` and security; only withdraw is destructive; strict request schemas published |

## Mock contract tests (`SourceApiTests`, `SapApiTests`)

The mocks are tested as well, so a scenario failure points at the integration, not at its test double:
- **Paging:** full traversal returns every record once; the final cursor is a durable resume point; edits mid-traversal are never skipped; page overlap works.
- **Write-back:** requires a current ETag.
- **Seed manifest:** lists all 23 defects.
- **SAP idempotency:** replay and `422` mismatch; a lost response commits in SAP; **without key support, a blind retry duplicates** (the reason for lookup-before-create).
- **SAP business rules:** rejects a plant outside the company code with the SAP error format; `PATCH` needs the current ETag and is refused after release.
- **SAP feeds:** the change feed pages via `@odata.nextLink`; lifecycle advance assigns a PO.

## Unit tests (`Procurement.Sync.UnitTests`)

| Area | What is pinned down |
|---|---|
| `RetryClassifier` | 429/503 retried for POST; 500/504/timeout/reset on POST never retried; reads and conditional PATCH retried |
| Parser and validator | Each defect type maps to its reason; field paths; tolerant of unknown fields; stable key for records without id; rule order |
| Mapping | SAP status codes → `ErpStatus`; canonical → SAP items; content hash ignores version; idempotency key deterministic per version |
| `ErpStatus` | Forward-only transitions, including `Rejected` and terminal states |
| State store | Enums stored as text, UTC round-trip, concurrency token detects lost updates, outbox uniqueness, lease exclusivity and expiry |

## Writing assertions for parallel work

Dispatch runs requests in parallel, so *which* request a scripted fault hits can vary between runs. Where that matters (S04, S15), the tests assert the **guarantee** rather than a fixed split. For example: "every in-doubt request ends in SAP exactly once or is quarantined, and SAP holds 184 with no duplicates". Tests that need one specific request to fail (S21) set `Parallelism = 1`. The suite was run repeatedly without failures.

## Fault injection

Both mocks expose runtime controls (not part of any contract):

```bash
# probabilistic chaos (seeded, reproducible)
curl -X PUT localhost:5102/_admin/faults -H 'content-type: application/json' \
     -d '{"TransientErrorRate":0.1,"ThrottleRate":0.05,"LostResponseRate":0.03,"Seed":7}'

# exact faults: the next N matching requests, optionally after letting some through
curl -X POST localhost:5101/_admin/faults/script -H 'content-type: application/json' \
     -d '[{"kind":"Error","method":"GET","pathContains":"/api/requisitions","skip":3,"count":1000,"statusCode":503}]'

curl -X DELETE localhost:5102/_admin/faults     # clear
```

| Fault | Effect |
|---|---|
| `Error` | Responds with the given status (default `503`) before the handler runs |
| `Throttle` | `429` with `Retry-After` |
| `LostResponse` | The handler **commits**, then the connection is aborted, so the client never learns the outcome |
| `Delay` | Adds latency (use it to trigger client timeouts) |
| `pageOverlap` (source) | Each page repeats the previous page's last record |
| `HonorIdempotencyKey: false` (SAP) | Behaves like a real SAP endpoint that ignores the header |

Other admin endpoints:
- `/_admin/reset`, `/_admin/seed-manifest` and `/_admin/requisitions/{id}/edit` (source);
- `/_admin/advance`, `/_admin/requisitions/{n}/lifecycle`, `/_admin/requisitions/{n}/touch` and `/_admin/stats` (SAP).

Both mocks also enforce a real token-bucket rate limit (20/s by default), which returns `429` with `Retry-After` and `RateLimit-Policy` headers.
