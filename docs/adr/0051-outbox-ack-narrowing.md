# ADR-0051: The stored outbox ack is narrowed per kind

- **Status:** Accepted (2026-09)

## Context

Every outbox item stored the server's response body verbatim (`serverResponse`, typed `unknown`), at acceptance and at refusal. Its readers each need a handful of fields: the result `id` and `currentRevision` (so a correction can be based before the results list refreshes), the implausibility `warnings` of a materialised race (the only place they are read), and the `error` and `detail` of a refusal.

What was stored was much wider, and the widest bodies carried student data: an entry's name, year level, house and student id; a relay's four swimmers; a materialised race's name and house per lane. [ADR-0031](0031-shared-device-hygiene.md) strips the student name from an accepted row's payload an hour after acceptance, while the same row's ack kept the same names for the full retention period ([ADR-0013](0013-outbox-retention.md)), and a conflict row is kept indefinitely. The devices are shared school iPads and staff phones that cannot be remote-wiped. The `unknown` type also meant every reader cast, so nobody could enumerate what was read.

## Decision

1. **A per-kind reducer runs before the row is written.** `app/src/sync/ackShape.ts` exports `narrowAck(kind, status, body)`. The transport calls it after classifying the response (classification still sees the full body), so the worker stores only the narrowed shape and stays kind-agnostic.
2. **What each kind keeps.** Everything else is dropped, not stored and swept later.
   - `result.submit`, `result.correct`, `result.attempt`, `result.jump`: `{ id, currentRevision }`.
   - `race.materialise`: `{ warnings }`.
   - `entry.create`, `team.create`, `race.create`, `entry.assign`, `adjustment.create`: nothing. The item id already is the created entity's id.
   - **Any refusal, whatever the kind:** `{ error, detail }`, the two error-contract fields ([ADR-0011](0011-api-error-contract.md)); the context block is dropped. Status decides before kind, so even an unknown kind's refusal stays readable.
   - **An unknown kind's 2xx ack keeps nothing** (fail closed). A test pins that every outbox kind has an entry.
3. **`serverResponse` is typed** as a union of those shapes, and the readers drop their casts.
4. **Rows already on devices were converged by a Dexie upgrade** (schema version 8) that re-runs every row's ack through `narrowAck` in place. Kind, status, payload, id and timestamps are untouched, and no row is deleted, moved or re-statused.
5. **Retention is unchanged, and the narrowed ack is not swept early.** The result `{ id, currentRevision }` is needed by the correction path all day, so it does not follow the one-hour display grace.

How the outbox stores and classifies responses: [offline outbox § acknowledgements](../architecture/offline-outbox.md#acknowledgements).

## Consequences

- No student name lives in any stored ack. Name exposure on a device is `payload.display` alone, with one documented grace window; an indefinitely kept conflict row still holds it, which is a separate open item.
- Adding an outbox kind forces a decision about what its ack is for.
- A field a future feature wants back is a map entry plus a schema version; acks narrowed before that cannot be un-narrowed (the server's audit trail is the record).
- A `result.correct` ack is now readable, so a second offline correction of the same result can base itself on the first.

## Rejected alternatives

- **Keep the full body:** leaves the name-retention residual open and the readers unenumerable.
- **One universal `{ id, currentRevision }` shape:** drops the materialise warnings, whose only reader is the outbox row.
- **Slim the server's write responses:** the full DTO is right for the online caller on the same route; this is a device-retention problem, not a wire problem.
- **Let retention age old rows out instead of migrating:** reaches accepted rows only, days later; the upgrade makes the claim true on the next app open.
