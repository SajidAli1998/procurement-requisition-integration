# ADR-0006: The transport never blindly repeats a POST

**Status:** accepted

## Context
Standard HTTP resilience handlers retry `5xx` responses and timeouts for every method. For a `POST` that creates something, a timeout or a reset means "maybe it worked". Retrying it is exactly how integrations create duplicates.

## Decision
All clients use `Microsoft.Extensions.Http.Resilience` (Polly v8): a token-bucket rate limiter, total and per-attempt timeouts, retry with exponential backoff and jitter, and a circuit breaker per downstream. The retry predicate is custom (`RetryClassifier`):
- **`429` and `503`** are retried for any method, because the server did nothing.
- **`408`, `500`, `502`, `504`, timeouts and connection errors** are retried only when repeating is harmless: GET, HEAD, PUT, DELETE, or PATCH with `If-Match`.
- **A POST with an unknown outcome** is returned to the pipeline as `Ambiguous`. The pipeline marks it `InDoubt` and checks SAP before doing anything else.
- **`Retry-After` is honoured but capped** (30s by default), so one response can't stall a run.

## Consequences
- **Retries are safe by construction.** The unit tests pin the rule down, and S04/S15 prove the outcome.
- **Some retries move from the transport to the next pipeline attempt.** That's slower, but correct.
- **Tests scale every wait by 0.001** (`Resilience:DelayScale`), so the real policies run in milliseconds instead of being mocked.

## Alternatives considered
- **Default standard handler:** retries POSTs on timeouts.
- **`DisableForUnsafeHttpMethods()`:** too blunt for the integration, because it also blocks retrying a `429` on a POST. It is used in the Assistant API's client to the source, where the API's own look-up-before-create handles the ambiguous case.
