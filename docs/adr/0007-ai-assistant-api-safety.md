# ADR-0007: AI assistant API, draft-only and server-enforced

**Status:** accepted

## Context
An LLM-based assistant will call this API on behalf of users. LLM agents retry, can be prompt-injected, can make up identifiers and numbers, and act with whatever permissions they are given (OWASP LLM06, *Excessive Agency*). Safety can't depend on the model behaving.

## Decision
- **Least agency.** The assistant can only validate, create a draft (`PendingApproval`), read its user's requests and reference data, and withdraw its user's own pending drafts. Approval stays with people.
- **Enforced by the server, not the prompt:**
  - JWT scopes (`requisitions:read`, `requisitions:draft`) checked by authorization policies;
  - identity taken from the token (`sub`), with ownership checked on every read and write;
  - a spend cap per request, line and quantity limits;
  - per-user rate limiting.
- **No invented data.** Codes must exist in the reference endpoints, and prices come from the catalogue. The request schema has no price field and rejects unknown fields and quoted numbers. The published OpenAPI schema states all of this (`additionalProperties: false`, min/max).
- **Safe retries.** `Idempotency-Key` is required on create, scoped per user, and follows the IETF draft semantics. The API also looks for an existing draft with the same key in the source before creating (A08).
- **Self-correction instead of guessing.** There is a side-effect-free `validate` operation, and every error is an RFC 9457 problem with a stable `code`, `retryable`, `retryAfter`, `invalidFields[]` with suggestions, and `suggestedAction`.
- **Machine-readable intent.** Every operation has an `operationId` and `x-mcp` hints (`readOnlyHint`, `idempotentHint`, `destructiveHint`). `api/mcp-tools.json` is generated from them. Withdraw is the only destructive tool, so MCP clients ask the user to confirm it.
- **Audit:** the channel, the assistant's session reference and the requesting user are stored on each requisition.

## Consequences
- A confused or compromised assistant can at worst create drafts that a person must still approve, within spend and rate limits.
- Idempotency keys live in memory (24-hour retention). That's enough for retries, but not for a restart or several instances. A shared store (SQL or Redis) is needed before scaling out.
- Development tokens are minted locally; production needs the identity provider (e.g. Entra ID, on-behalf-of flow).

## Alternatives considered
- **Expose the integration's internal API to the assistant:** far too much agency.
- **Let the assistant send prices and approve small amounts:** convenient, but it moves a financial control into the model.
