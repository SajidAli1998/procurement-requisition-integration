# ADR-0005: Polling with checkpoints rather than events

**Status:** accepted

## Context
The source exposes a paged read API, not events. SAP exposes a change feed through `LastChangeDateTime`.

## Decision
- **Poll both systems:**
  - the source with an opaque keyset cursor over `(updatedAt, id)`, returned even on the last page so it works as a durable resume point;
  - SAP with a watermark, re-reading a small overlap.
- **Commit the cursor in the same transaction** as the records staged from that page.
- **Read all changes, not just approved ones,** so cancellations and un-approvals after transfer are seen.

## Consequences
- **Crash-safe resume:** a failure mid-read loses nothing and repeats at most one page (S03).
- **Records edited mid-traversal** move to the end of the order and are read again with their new version. Nothing is skipped (mock test `Records_edited_mid_traversal_are_never_skipped`).
- **Latency equals the polling interval** (`procsync serve --interval`).
- **Our own write-back re-surfaces records as changes.** Content hashing turns those into `Unchanged` without further work.

## Alternatives considered
- **Webhooks or CDC:** lower latency where available. They would feed the same outbox, so the rest of the design is unchanged.
- **Offset paging:** skips or repeats records when data changes between pages.
