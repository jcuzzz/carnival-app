# ADR-0002: Offline sync and idempotency design

- **Status:** Accepted (2026-07). The finalisation gate is replaced: finalisation is one act per carnival ([ADR-0017](0017-automatic-result-validation.md)) and only warns about undrained devices ([ADR-0034](0034-organiser-queue-visibility.md)).

## Context

A capture taken at the venue must survive a dropped connection, a killed app or a lapsed session; a retried delivery must never duplicate a result; a clash between two officials must be surfaced rather than overwritten; and every write must stay attributable on shared devices. Devices are normally online: the outbox exists so that a dropout never loses a capture, not to run a carnival disconnected. The research is in [offline-sync-patterns.md](../research/offline-sync-patterns.md).

## Decision

- **A queued-write outbox, not bidirectional sync.** Result capture is append-mostly; the device sends writes and reads the server's state.
- **A UUIDv7 minted at capture** is both the idempotency key and the durable id of the row the write creates. The capture is written to IndexedDB before any network attempt.
- **The server stores the key, a hash of the payload and the response in the same transaction as the write.** A replay with the same payload returns the stored response; the same key with a different payload is refused (`IDEMPOTENCY_PAYLOAD_MISMATCH`), because that is a client bug, not a retry. Only a 2xx is recorded: a refusal depends on server state, so it leaves the key usable for a later attempt that may succeed. A unique-constraint violation raised by the write itself is the handler's own refusal, not a lost race on the key.
- **A foreground sync loop delivers the queue.** The Background Sync API is not relied on; it is unreliable on iOS Safari.
- **Corrections use optimistic concurrency** on the result's revision. Last write wins is rejected for authoritative results: a clash parks for a person, and the immutable revision history records every change.
- **The outbox belongs to the device; the actor belongs to the item.** The official signed in at capture is recorded with each item and is never re-attributed by a handover or a later sign-in. Sign-in needs a connection; a `401` during delivery pauses the queue and never drops an item.
- **Finalisation does not wait for device queues to drain.** An offline device and a lost one look the same to the server, and a drain gate would give a lost device a veto over publication.
- The app requests persistent storage at start-up to reduce the risk of iOS evicting an unsynced queue.

How it works: [offline outbox and idempotency](../architecture/offline-outbox.md).

## Consequences

- A duplicate on retry is structurally impossible at the server; the client can crash and retry freely.
- Losing a device loses only its unsynced items; mitigated by draining continuously and keeping only a minimal roster cache on the device.
- A capture the server refuses for good needs a person to resolve it on the device ([ADR-0025](0025-outbox-recovery.md)), and a stuck queue must be visible during the day ([ADR-0029](0029-a-stuck-outbox-is-loud.md), [ADR-0034](0034-organiser-queue-visibility.md)).

## Rejected alternatives

- **General bidirectional sync or a sync engine**: solves conflicts this workload does not have, and hides the critical logic.
- **Last write wins on corrections**: silently discards one official's value.
- **A finalisation gate on drained device queues**: cannot tell a lost device from an offline one.
