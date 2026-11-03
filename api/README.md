# API contracts

| File | What it is |
|---|---|
| [`assistant-api.openapi.json`](assistant-api.openapi.json) | **The AI-facing contract**: OpenAPI 3.1 for the Assistant API |
| [`mcp-tools.json`](mcp-tools.json) | The same operations as MCP tool definitions, generated from the OpenAPI `x-mcp` hints |
| [`source-mock.openapi.json`](source-mock.openapi.json) | The internal requisition system (mock) |
| [`sap-mock.openapi.json`](sap-mock.openapi.json) | The SAP-style purchase requisition service (mock) |

All four are exported from the running services by `scripts/export-api.sh`, so they can't drift from the code. Browse them interactively at `/scalar` on each service.

---

# Assistant API: a contract an AI can use safely

The Assistant API lets an AI procurement assistant **raise purchase requisitions and track them on behalf of a signed-in user**. It assumes the caller is a language model: one that retries, may misremember identifiers, may invent plausible-looking values, and may be manipulated by the text it reads. So every safety rule is enforced by the server, and the contract tells the agent exactly how to recover from each error.

## Ground rules (also in the OpenAPI `info.description`)

1. **Raising a request never commits spend.** It is created as `PendingApproval`, and a person approves it.
2. **Never invent codes.** Look up cost centers, plants and materials in `/v1/reference/*`. Prices come from the catalogue, and requests carry no price field.
3. **Validate before creating** and fix every issue.
4. **Show the user `confirmationSummary` and create only on their yes,** sending `confirmationToken` as the `X-User-Confirmation` header. The token fits only that exact request: change anything and you must validate and ask again.
5. **Send a new `Idempotency-Key` (UUID) per user intent.** Reuse it only to retry that same request.
6. **Branch on `problem.code`, never on titles.** If `retryable` is false, don't retry; follow `suggestedAction`.
7. **Relay `statusReason` and `nextExpectedStep` as written.**
8. **Withdrawing needs the user's explicit confirmation** and the ETag from the latest read.

## Operations

| Tool / `operationId` | HTTP | Scope | Read-only | Idempotent | Destructive |
|---|---|---|:-:|:-:|:-:|
| `searchMaterials` | `GET /v1/reference/materials?search=` | read | ✓ | ✓ | |
| `listCostCenters` | `GET /v1/reference/cost-centers` | read | ✓ | ✓ | |
| `listPlants` | `GET /v1/reference/plants?companyCode=` | read | ✓ | ✓ | |
| `validateRequisitionRequest` | `POST /v1/requisition-requests:validate` | draft | ✓ | ✓ | |
| `createRequisitionRequest` | `POST /v1/requisition-requests` + `Idempotency-Key` + `X-User-Confirmation` | draft | | ✓ (by key) | |
| `getRequisitionRequest` | `GET /v1/requisition-requests/{id}` | read | ✓ | ✓ | |
| `listRequisitionRequests` | `GET /v1/requisition-requests?cursor=&limit=` | read | ✓ | ✓ | |
| `withdrawRequisitionRequest` | `POST /v1/requisition-requests/{id}:withdraw` + `If-Match` | draft | | ✓ | **✓ (confirm with the user)** |

There is deliberately **no** approve, release, order, delete or update-price operation.

## Identity and scopes

- **Bearer token, delegated by the user.** `sub` is the user. `scope` holds:
  - `requisitions:read`: the user's own requests;
  - `requisitions:draft`: raise and withdraw;
  - `requisitions:read:all`: the delegated procurement role, which may read any request.
- **Ownership comes from the token, never from the request body.** Someone else's request returns `403 NOT_YOUR_REQUISITION`, as the client's contract tests specify ([ADR-0007](../docs/adr/0007-ai-assistant-api-safety.md) records the trade-off against `404`).
- **Locally, get a token with:**
  ```bash
  curl -s -X POST localhost:5103/dev/token -H 'content-type: application/json' -d '{"user":"E1002"}'
  ```
  Employees from the client's workbook (`E1001`-`E1120`, plus the delegate `E1900`) get the scopes [`data/employee-directory.json`](../data/employee-directory.json) gives them. Anyone else gets read and draft. This endpoint exists in the Development environment only; in production, tokens come from the organisation's identity provider via the on-behalf-of flow.
- **Rate limit:** 60 requests per minute per user, with `429` + `Retry-After`.

## Creating a request

The examples use the client's workbook data: cost centres `CC-01`…`CC-08`, items `TEST-…`, QAR, plant `3100`.

**1. Validate.** If the request is valid, the response carries what to show the user and the token to send once they agree:

```http
POST /v1/requisition-requests:validate
Authorization: Bearer …            (E1002)

{
  "costCenter": "CC-02",
  "plant": "3100",
  "neededBy": "2026-12-03",
  "justification": "Two route control displays for the depot",
  "lines": [ { "materialCode": "TEST-002-01", "quantity": 2 } ],
  "assistantSessionRef": "conv-42"
}
```
```json
{
  "valid": true, "estimatedTotal": 774.00, "currency": "QAR", "issues": [],
  "confirmationSummary": "Raise a requisition for approval: 2 EA Route control displays item 1 (TEST-002-01) at 387.00. Total 774.00 QAR, charged to CC-02 (Fleet Maintenance), delivered to plant 3100, needed by 2026-12-03.",
  "confirmationToken": "uc1.…"
}
```

**2. Ask the user. On their yes, create** with the same body:

```http
POST /v1/requisition-requests
Authorization: Bearer …
Idempotency-Key: 6f1c2a7e-3b0d-4c55-9a3e-1e2f3a4b5c6d
X-User-Confirmation: uc1.…
```

The token is an HMAC of the user and the exact request:
- without it, create returns `428 CONFIRMATION_REQUIRED`;
- with a request changed after the user agreed (another quantity, material or cost centre), it returns `422 CONFIRMATION_MISMATCH`;
- another user cannot use it.

An invalid request still gets `422 VALIDATION_FAILED` first, so the agent can fix it before asking the user anything.

**The request schema is strict.** Unknown fields are rejected (`additionalProperties: false`), numbers must be JSON numbers, and the limits are published: 1-20 lines, quantity 0-1000 (whole numbers for `EA`/`BOX`), justification 10-500 characters, `neededBy` from tomorrow to one year ahead. On top of that there is a **spend cap** of 25,000 (in the cost centre's currency) per request; above it, the user is asked to raise the request with procurement directly.

**Idempotency**, following the IETF `Idempotency-Key` draft:

| Case | Result |
|---|---|
| Same key, same body | The first response is replayed, with header `Idempotent-Replayed: true` |
| Same key, different body | `422 IDEMPOTENCY_KEY_REUSED` |
| Same key while the first request is still running | `409 REQUEST_IN_PROGRESS`, retryable |
| Validation failure or upstream error | Nothing is created and the key is released, so the same key may be reused |

## Errors

Every error is RFC 9457 `application/problem+json` with extensions:

```json
{
  "type": "urn:procurement:error:validation_failed",
  "title": "The request has invalid fields",
  "status": 422,
  "code": "VALIDATION_FAILED",
  "retryable": false,
  "suggestedAction": "Fix each field in invalidFields (see each suggestion) and retry. Nothing was created, so the same key may be reused.",
  "invalidFields": [
    { "field": "plant", "code": "PLANT_NOT_IN_COMPANY",
      "message": "Plant 2100 belongs to company 2000, but cost center CC-1000-IT belongs to company 1000.",
      "suggestion": "Use one of: 1100, 1200." }
  ]
}
```

| `code` | Status | Retryable | Meaning |
|---|---|:-:|---|
| `INVALID_REQUEST_BODY` | 400 | | Not the documented shape (unknown field, wrong type) |
| `IDEMPOTENCY_KEY_REQUIRED` | 400 | | Missing `Idempotency-Key` on create |
| `UNAUTHORIZED` | 401 | | Missing or invalid token |
| `FORBIDDEN` | 403 | | Token lacks the scope; tell the user to do it themselves |
| `NOT_YOUR_REQUISITION` | 403 | | The request belongs to someone else, and the user has no delegated procurement role |
| `NOT_FOUND` | 404 | | No such request |
| `REQUEST_IN_PROGRESS` | 409 | ✓ | Same key still processing |
| `NOT_WITHDRAWABLE` | 409 | | Already past approval |
| `VERSION_CONFLICT` | 412 | | Changed since read: re-read and re-confirm |
| `IDEMPOTENCY_KEY_REUSED` | 422 | | Key used for a different request |
| `VALIDATION_FAILED` | 422 | | See `invalidFields` |
| `CONFIRMATION_MISMATCH` | 422 | | The request differs from what the user confirmed: validate again and ask again |
| `CONFIRMATION_REQUIRED` | 428 | | `X-User-Confirmation` missing on create: show the user `confirmationSummary` first |
| `PRECONDITION_REQUIRED` | 428 | | `If-Match` missing on withdraw |
| `RATE_LIMITED` | 429 | ✓ | Wait `retryAfter` seconds |
| `UPSTREAM_UNAVAILABLE` | 503 | ✓ | Retry with the **same** key; no duplicate will be created |

Validation issue codes include:
- `UNKNOWN_COST_CENTER`, `UNKNOWN_PLANT`, `PLANT_NOT_IN_COMPANY`;
- `UNKNOWN_MATERIAL`, `INVALID_QUANTITY`, `DUPLICATE_LINE`, `LINES_COUNT`;
- `NEEDED_BY_OUT_OF_RANGE`, `JUSTIFICATION_LENGTH`;
- `SPEND_LIMIT_EXCEEDED`.

## Status model

`status` is a closed set. Each response also carries a plain-language `statusReason` and `nextExpectedStep`, safe to show the user.

| `status` | Meaning |
|---|---|
| `PendingApproval` | Waiting for an approver; can be withdrawn |
| `Approved` | Approved, waiting to be transferred to SAP |
| `TransferredToErp` | Created in SAP (`erpReference` set) |
| `ReleasedInErp` | Released by purchasing |
| `PurchaseOrderCreated` | PO raised (`purchaseOrder` set) |
| `Closed` | Fully processed |
| `RejectedByApprover` / `RejectedInErp` | Rejected before or after transfer |
| `Withdrawn` | Withdrawn by the user before approval |
| `NeedsAttention` | The integration couldn't process it; procurement has been notified |

A `timeline` lists submission, approval and each ERP status change.

## Example: a well-behaved assistant turn

> E1002 (Fleet Maintenance): "Order two route control displays for the depot, needed in a month."

1. `listCostCenters` → picks `CC-02` (Fleet Maintenance, QAR).
2. `listPlants?companyCode=3000` → `3100`.
3. `searchMaterials?search=route control` → `TEST-002-01`, 387.00 QAR each.
4. `validateRequisitionRequest` → `valid: true`, `estimatedTotal: 774`, plus `confirmationSummary` and `confirmationToken`.
5. Shows the user the summary: *"Two route control displays, 774.00 QAR, charged to Fleet Maintenance, needed by 3 Dec. Submit for approval?"*
6. On "yes": `createRequisitionRequest` with a fresh `Idempotency-Key` and the token → `PendingApproval`. If the call times out, it retries with the **same** key and token.
7. Later, `getRequisitionRequest` → relays *"Approved and created in SAP as purchase requisition MOCK-SAP-60003. Purchasing releases it in SAP; a purchase order follows."*

> E1001: "What's the status of REQ-7001?"

`getRequisitionRequest` → `403 NOT_YOUR_REQUISITION`. The assistant tells the user the requisition belongs to someone else, and does not retry.

## MCP mapping

[`mcp-tools.json`](mcp-tools.json) turns each operation into an MCP tool:
- `inputSchema` combines the parameters and request body, with the published limits and `additionalProperties: false`;
- `annotations` carry `readOnlyHint`, `destructiveHint`, `idempotentHint` and `openWorldHint`;
- `_http` records the REST mapping and required scopes for whoever writes the MCP server.

Headers become inputs: `idempotencyKey` and `userConfirmation` on create, and `ifMatch` on withdraw. Regenerate the file with:

```bash
python3 scripts/generate_mcp_tools.py api/assistant-api.openapi.json > api/mcp-tools.json
```
