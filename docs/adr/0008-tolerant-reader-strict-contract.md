# ADR-0008: Tolerant reader for the source, strict contract for the assistant

**Status:** accepted

## Context
Two inputs, two different trust positions. The source system is an established internal API that may add fields over time. The Assistant API's callers are language models that may add fields they imagine exist.

## Decision
- **Source records:** tolerant reader. Unknown fields are ignored. Each record is parsed on its own from raw JSON, collecting **every** problem with its field path (for example `lines[0].quantity must be a number, got String "ten"`), then checked by FluentValidation business rules whose error codes are `QuarantineReason` values. One bad record never fails a page. Records without an id get a stable content-hash key so they can still be quarantined and reported.
- **Assistant requests:** strict contract. Unmapped members are disallowed, numbers must be numbers, required members are enforced, and the published schema says the same.

## Consequences
- Source schema evolution doesn't break the integration, while missing or wrong-typed **required** data is still caught.
- Agents get an immediate, precise `400` naming the offending field instead of having it silently ignored.

## Alternatives considered
- **Strict deserialization of source pages:** one malformed record would fail the whole page, and new fields would break the integration.
- **Lenient Assistant API:** a made-up `unitPrice` would be silently dropped. The assistant would believe it set a price when it didn't.
