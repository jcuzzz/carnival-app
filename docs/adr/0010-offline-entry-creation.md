# ADR-0010: Offline entry creation (client-supplied entry ids)

- **Status:** Accepted (2026-07)

## Context

Results queue to the device outbox with a capture-time UUIDv7 and drain when the API answers ([ADR-0002](0002-offline-sync-design.md)). Adding an entry was an online round trip returning a server-minted id, and the moment a late swimmer walks up to the blocks is when the hotspot is most likely to drop. The one online-only step sat on the critical path of the day.

## Decision

Entry creation goes through the same outbox as results, keyed by a client-minted entry id.

- The device mints the entry id (UUIDv7) at capture and queues an `entry.create` item. The item id is the entry id and the `Idempotency-Key`.
- The entry appears at once, built from the cached eligible-student list, and a result can be captured against it immediately. The sync worker holds that result until the entry is accepted.
- **Idempotency is optional on the create endpoint.** With a key, the server adopts it as the entry id and deduplicates retries; without one (an ordinary online create) it mints the id. A key that is present but not a UUID is refused, never treated as "no key". Team entries, races, placements and adjustments follow the same pattern.
- **The server's eligibility rules do not change** with who minted the id.
- **A refused add is parked for a person**, not dropped. Only 2xx responses are recorded against a key, so the key stays usable; the official can requeue, edit or discard the item from the outbox screen ([ADR-0025](0025-outbox-recovery.md)). Discarding a create parks any results captured against it with a note, rather than dropping a swum time.
- **The entry records the official who captured it**, not the session that delivered it, like a result.

How it works: [offline outbox § offline entry creation](../architecture/offline-outbox.md#offline-entry-creation).

## Consequences

- Add a competitor, record a time and sync now works with no connection.
- The create contract accepts an optional header; online creation is unchanged.
- The worker must track dependencies between queued items (a result on a queued entry, a placement on a queued race).
