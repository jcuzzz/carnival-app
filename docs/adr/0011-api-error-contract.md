# ADR-0011: API error contract (machine-code error bodies)

- **Status:** Accepted (2026-07)

## Context

The API answered errors in four shapes: a prose `error` sentence, RFC 7807 problem details, a machine code in one place, and bare bodyless responses. The offline outbox must decide for every non-2xx it drains whether the item is retryable (leave it queued), a conflict for a person, or a definitive refusal. Two `422`s cannot be told apart by status, and prose is not a branchable contract: it gets reworded and localised, and matching on it would silently break the queue.

## Decision

**Every non-2xx JSON response under `/api/v1` has the body:**

```json
{ "error": "MACHINE_CODE", "detail": "A human sentence explaining what went wrong." }
```

- **`error` is a stable machine code**: `UPPER_SNAKE_CASE`, never localised, never reworded once shipped. Changing a code is a breaking change; adding one is not. Codes are server-side constants, so a typo is a compile error.
- **`detail` is for people** and may be reworded at any time; nothing branches on it. The app shows its own copy for known codes and falls back to `detail`.
- Extra fields may accompany a code that needs structured context (for example the current revision on a concurrency conflict). Clients ignore fields they do not know.
- **Clients branch on `error`**, with the HTTP status as the coarse fallback. This is what lets the outbox tell a transient business refusal from a terminal one.
- **No fault becomes a misleading retry.** An unhandled exception is `500 INTERNAL_ERROR` with a non-leaking detail. A database serialisation conflict is `409 CONCURRENCY_CONFLICT`. Any other refused database write is `409 WRITE_REFUSED`, and an over-long field is refused up front (`VALIDATION_FAILED`): a payload that can never succeed must not look retryable, or it would halt a device's queue forever. Enum values must be names, never integers (`REQUEST_INVALID`).
- **Authentication and authorisation failures under `/api` are JSON** (`AUTHENTICATION_REQUIRED`, `FORBIDDEN`), never a redirect.
- The one exception: a request that matches no route answers `404` with no body.

The code catalogue is `Http/ErrorCodes.cs`, published through the OpenAPI document; how the outbox classifies codes is in [offline outbox § classifying the answer](../architecture/offline-outbox.md#classifying-the-answer).

## Consequences

- The outbox has a real basis for retry versus park versus reject, which is the difference between a stranded capture and a recoverable one.
- Codes must be chosen deliberately, not derived from messages.
- Teacher-facing wording lives in the client, not in endpoint handlers.
- Tests assert on codes, which leaves the copy free to change.

## Rejected alternatives

- **RFC 7807 problem details**: suited to a public multi-consumer API, but gives the one consumer (our own PWA) a URI to branch on instead of a short code, and a second shape to parse.
