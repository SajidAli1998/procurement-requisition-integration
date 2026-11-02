# Architecture decision records

| # | Decision |
|---|---|
| [0001](0001-three-layer-duplicate-protection.md) | Three independent layers of duplicate protection |
| [0002](0002-data-ownership-and-conflicts.md) | Source owns content, SAP owns status; forward-only status |
| [0003](0003-sqlite-default-sql-server-ready.md) | SQLite by default, SQL Server ready; `EnsureCreated` instead of migrations |
| [0004](0004-transactional-outbox.md) | Transactional outbox drained in-process; Service Bus later |
| [0005](0005-polling-with-checkpoints.md) | Polling with checkpoints rather than events |
| [0006](0006-transport-retry-rules.md) | The transport never blindly repeats a POST |
| [0007](0007-ai-assistant-api-safety.md) | AI assistant API: draft-only, server-enforced safety |
| [0008](0008-tolerant-reader-strict-contract.md) | Tolerant reader for the source, strict contract for the assistant |

Format: context, decision, consequences, alternatives considered.
