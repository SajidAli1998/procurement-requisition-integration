# ADR-0002: Source owns content, SAP owns status

**Status:** accepted

## Context
Both systems change the same requisition. Users edit or cancel it in the source; purchasing releases it and creates purchase orders in SAP. Without a rule, the integration has to guess who wins every time two updates conflict.

## Decision
- **Source owns content** (what is requested). Content changes flow to SAP while SAP still accepts them (`Created`, `InRelease`). Once SAP has released the requisition, the change is quarantined as `VersionConflictUnresolved` with instructions, and SAP keeps the earlier valid version.
- **SAP owns status** (release, PO, closure). Status flows back to the source, and only the ERP status fields are written.
- **Statuses only move forward.** An older or repeated status (out-of-order polls, overlap windows) is ignored and logged.
- **Cancellation after transfer is flagged, never acted on.** It is quarantined as `SourceChangedAfterTransfer` so a person can cancel the SAP requisition. Automatically cancelling procurement documents is a business decision, not an integration one.
- **Every write is guarded by `If-Match`.** On `412`, the integration re-reads, re-decides and re-applies only its own fields (at most 3 times), then defers to the next run.

## Consequences
- **No silent overwrites:** every conflict is either resolved by the rule or put in front of a person, with an explanation.
- **Some changes need a person:** late amendments and cancellations after transfer. These are exactly the cases where an automatic choice could cost money.

## Alternatives considered
- **Last writer wins:** simple, but loses data silently.
- **Automatic SAP cancellation:** too risky without business sign-off; easy to add behind a flag later.
