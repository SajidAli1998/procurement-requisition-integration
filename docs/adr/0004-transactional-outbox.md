# ADR-0004: Transactional outbox, drained in-process

**Status:** accepted

## Context
A validated record must reach SAP even if the process dies between validation and sending. Writing "validated" to the database and publishing to a queue in two separate steps can lose a message or send it twice.

## Decision
The extract stage writes the record's new state **and** an outbox message in the same transaction as the page checkpoint. The dispatch stage drains due messages with bounded parallelism (`Sync:Parallelism`), sending only the newest version per requisition; older versions are marked superseded. A unique index on `(SourceId, SourceVersion, Kind)` makes staging idempotent.

## Consequences
- **Nothing validated is lost,** and a rerun never stages the same version twice.
- **Throughput is bounded by one process.** That's fine for this volume; scale-out goes through the next step.
- **Path to a broker:** an outbox relay publishes to Azure Service Bus (sessions keyed by requisition id, so ordering holds per requisition) or RabbitMQ, and competing consumers run dispatch. The dispatch logic doesn't change, because it already assumes at-least-once delivery.

## Alternatives considered
- **Publish directly to a broker from extract:** the dual-write problem.
- **Run a broker locally:** extra infrastructure for reviewers, with no change to the guarantees.
