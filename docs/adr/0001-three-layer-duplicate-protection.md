# ADR-0001: Three independent layers of duplicate protection

**Status:** accepted

## Context
"Reruns must never create duplicates." Duplicates come from several different failures:
- re-reading the same source record (reruns, page overlap, records changed mid-read);
- a create whose response is lost after SAP committed it;
- a retry after a timeout;
- local state being lost or rebuilt.

No single mechanism covers all of these. And the obvious mechanism, an idempotency key, is not supported by real SAP.

## Decision
Use three layers, each sufficient for a subset of failures:
1. **Local mapping.** A requisition with a SAP number is only ever amended (`PATCH` with `If-Match`), never created again.
2. **Deterministic `Idempotency-Key`** = hash of (requisition id, version). It is identical on every retry, and the mock honours it per the IETF draft (replay / `422` / `409`).
3. **Look before create.** Query SAP by `ExternalReference` before every `POST`, and again immediately after an unknown outcome. If SAP already holds the requisition with the same content, adopt it.

Before amending, content is compared rather than version numbers. The integration's own status write-back bumps the source version without changing content.

## Consequences
- **Duplicates are prevented in every failure mode tested:** lost responses (with and without key support), timeouts, crash mid-send, page overlap, reruns, and a wiped local database (S02, S04, S05, S12, S15).
- **Lookups double the SAP calls for new records** (368 vs 184 for the seed set). Batching lookups per page with `$filter=ExternalReference in (...)` removes most of that cost and is the first optimisation to make.
- **The reconciliation report independently proves** there are no duplicates by scanning SAP.

## Alternatives considered
- **Idempotency key only:** fails against real SAP, which ignores the header, and after the key expires.
- **Lookup only:** a race remains between lookup and create if two writers act at once. The lease and layer 2 close that gap.
- **Uniqueness constraint in SAP on the external reference:** not available in standard SAP, and not ours to change.
