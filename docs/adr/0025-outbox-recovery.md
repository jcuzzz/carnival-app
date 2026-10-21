# ADR-0025: Outbox recovery — what a person may do to a parked capture

- **Status:** Accepted (2026-08)

## Context

[ADR-0002](0002-offline-sync-design.md) promises that a capture is never silently dropped: a refusal the client cannot resolve parks the item, payload intact, for a person. [ADR-0013](0013-outbox-retention.md) keeps `conflict` rows indefinitely because a person is meant to deal with them, and some refusals (`REQUEST_INVALID`, `ENTRY_NOT_FOUND` on a placement) are deliberately classified `conflict` rather than `rejected` to keep them recoverable. Without an in-app way to act on a parked item, all of that was a promise with no route: a volunteer holding an iPad at the marshalling point could neither edit the device's database nor easily redo the act.

Three properties of the outbox make recovery safe:

1. **Requeue grants no authority.** The server authorises every send against the current session, and the item carries its captured actor. A requeued item the current user may not perform is simply refused again.
2. **Requeue restores order for free.** Items drain in UUIDv7 id order, so a requeued item re-enters at its original position: a requeued `entry.create` still drains before the placement that depends on it.
3. **The outbox is device-local and role-free.** It is reached from the header, not from behind the Manage role gate ([ADR-0024](0024-carnival-context-and-primary-navigation.md)).

## Decision

### 1. Three actions

A parked item offers **Requeue**, **Edit and requeue** and **Discard**. No bulk retry, no auto-resolution, no merge.

- **Requeue** (`conflict → queued`) is for refusals where the world changed and the payload did not: `LANE_TAKEN` once the other swimmer moved, `ENTRY_IN_OTHER_DIVISION` once the wrong entry was removed, `ENTRY_NOT_FOUND` once the create it waited on landed.
- **Edit and requeue** is for a wrong payload. It is offered only where the app has a typed editor for the correctable value — result submissions, typed corrections and non-foul attempts — never as a raw payload editor. Other kinds get Requeue and Discard; a wrong payload there is a fresh capture from the screen that made it. The edited item **keeps its own id as the idempotency key**, so a re-send of something that had in fact landed is deduplicated rather than parking again as a duplicate. The editor is recorded beside the original capturer.
- A correction parked on `CONCURRENCY_CONFLICT` is not requeued unchanged (resending the same base revision parks again): it is retyped against the current revision or discarded.
- **Discard** is for a capture that should not land at all.

### 2. Discard is a new status, not `cancelled`

Discard moves the item to a new terminal status, **`discarded`**. `cancelled` means "withdrawn in the undo window, before the server saw it"; reusing it would erase the difference between *never sent* and *sent, refused, and a person let it go*, which someone reconstructing a contested result needs. Deletion is refused on the append-only rule. Discarded items age out under the retention window like accepted ones.

Every check of "does this outbox item still speak for its row" treats `cancelled` and `discarded` alike, so a discarded result frees its row for a fresh capture and a discarded add frees its student to be re-added.

### 3. `rejected` is dismissible, never requeueable

`rejected → discarded` is allowed; `rejected → queued` is not. A definitive refusal is never retried; a person who believes it wrong redoes the act as a fresh capture with a fresh key. Without dismissal the outbox would never again read "nothing needs attention" after the first rejection.

| From | To |
|---|---|
| `conflict` | `queued`, `discarded` |
| `rejected` | `discarded` |
| `accepted`, `cancelled`, `discarded` | terminal |

### 4. No role gate; discard is confirmed and recorded

Recovery is available to whoever is signed in on the device, with no extra role check: an administrator-only gate would strand a marshal at the pool, and buys nothing because the server authorises the send regardless. Discard abandons captured work, so it takes a two-step confirm and records `resolvedBy` and `resolvedAt` on the item. A discarded capture never reaches the server, so this device row is the only evidence it existed.

### 5. Where the controls live

- The **Outbox screen** carries the full controls and is the home for items whose row cannot render (a refused add).
- On the **deck row**, the conflict badge offers Requeue in place and the row's Edit button reaches a parked result, so a marshal fixes a refused tap without leaving the page. Where a refusal cannot be cleared from the deck (for example a finalised result), the row says where it can be dealt with instead.
- **Discard is not a deck-row control**, except dismissing a refused add, which routes through discard so the record survives.

## Consequences

- "Parked for a person" is true of the code: every parked item has a route out.
- Entirely client-side: no server change.
- **A discarded capture leaves no server-side trace.** Accepted as the cost of an offline-first queue; mitigated only by the local record and its retention window.
- A recovery surface makes it cheaper to leave conflict causes unfixed. Lane swaps are the example: [ADR-0030](0030-lane-swap.md) prevents the swap from parking at all. Detecting a stuck queue is [ADR-0029](0029-a-stuck-outbox-is-loud.md).

## Rejected alternatives

- **Reuse `cancelled` for discard, or delete the row** — loses the *never sent* / *let go* distinction; deletion breaks append-only.
- **Allow `rejected → queued`** — silently retries a definitive refusal.
- **Administrator-only discard** — on shared devices the signed-in user is usually the volunteer, so discard would be unreachable at the venue.
- **A raw payload editor for every kind** — a way to corrupt an intact capture, not an editing surface.

See [offline outbox: the outbox screen](../architecture/offline-outbox.md#the-outbox-screen).
