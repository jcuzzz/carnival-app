# ADR-0013: Outbox retention and index-scoped live queries

- **Status:** Accepted (2026-07)

## Context

The device outbox was append-only and never pruned, and the live screens re-read and re-sorted the whole table on every change. Both got worse together: by mid-afternoon of a carnival the table held hundreds of accepted rows no screen needed, re-read on every keystroke, on a school iPad or a staff phone. [ADR-0002](0002-offline-sync-design.md) said nothing about what happens to an item after the server accepts it, which left "forever" as a default nobody chose.

## Decision

### Retention

- **The server's audit trail is the durable record.** A device's copy of an accepted item is operational state: it lets the official see their work landed.
- **Accepted items are pruned 7 days after acceptance.** Seven days covers the carnival, the following days' "did my times go through?", and a weekend, without accumulating a shadow audit log on an unmanaged phone that cannot be remotely wiped ([ADR-0006](0006-shared-device-and-byo-session-policy.md)). An accepted row also sheds the student's name after an hour ([ADR-0031](0031-shared-device-hygiene.md)).
- **Unfinished work is never pruned on a timer.** `queued` and `syncing` items are undelivered; `conflict` and `rejected` items are a person's problem and stay until someone resolves or discards them. A discarded item is pruned 7 days after it was let go.
- Pruning runs at start-up and after a clean drain, as indexed range deletes. It is not a background timer.

Delivery semantics are untouched: only rows the server has already recorded become eligible for removal.

### Live queries

- Outbox screens use index-scoped queries (by status, ordered by the capture-ordered id) and aggregate counts, instead of loading and sorting the whole table.
- A schema upgrade never deletes, re-statuses or re-orders an outbox row.

Retention in detail: [offline outbox § retention](../architecture/offline-outbox.md#retention).

## Consequences

- Outbox reads cost O(matching rows), not O(table), so the afternoon slowdown is removed structurally.
- "Where are my results from last month?" is answered by the server, not the device, and the outbox screen says so.
- The 7-day window is a judgement held in one constant, revisable if officials want a longer local trail.
- Long-lived parked items dominate an old device's outbox by design: an unresolved capture should stay visible until someone deals with it.
