# ADR-0030: Swapping two swimmers' lanes

- **Status:** Accepted (2026-08). Amends [ADR-0020](0020-flatten-lane-handling.md) §2: the lane control also offers occupied lanes, as named swaps.

## Context

Two swimmers turn up in each other's lanes. The obvious fix — put A in lane 4, put B in lane 3 — parks both placements as `LANE_TAKEN`: each is an independent write and the lane each wants is still held by the other. A legal sequence exists (move one to no lane, move the other across, move the first back), but nothing suggested it, and [ADR-0025](0025-outbox-recovery.md)'s recovery is a cure, not prevention. Swapping lanes is a routine marshalling act and needs an affordance.

Constraints:

- **Lane uniqueness is race-scoped and server-enforced** by a filtered unique index on (race, lane) ([ADR-0015](0015-heats-races-and-lanes.md)). Nothing the client sequences can put two swimmers in one lane.
- **A null lane inside a race is an ordinary state**, outside that constraint.
- **The heat decides house points** and is derived, never typed. A sequence that moved a heat would be a scoring change.
- **Placement writes go through the outbox**, so "three writes in order" must survive a drain, a park and an hour off the air.
- **The lane control offers vacant lanes only**, so a mis-tap can never collide. The deliberate swap returns as its own labelled act.

The placement endpoint already accepts `{ raceId: R, lane: null }` (in the race, no lane), and that write keeps the entry's existing heat. So a swap needs no server change.

## Decision

No API change, no new outbox kind, no new permission: a swap is three ordinary `entry.assign` placements.

### 1. An occupied lane is offered as a swap

When a row holds a lane in a race, its lane control lists, below the vacant lanes, each other occupied lane of the same race, labelled for what picking it does ("Lane 4 — swap with *name*"). A row with no lane has nothing to exchange and gets no swap offers. Lanes above the venue's lane count are never offered as swaps in either direction, since the server refuses such lanes and the park could never clear. Swaps are within one race only.

### 2. Two taps

Picking a swap arms an inline confirm strip ("Confirm swap with *name*" / Cancel) with Remove's arming lockout, auto-disarm and focus handling, and it disarms if either row moves underneath. It is the only control where one tap moves a swimmer who is not on this row. The strip is not painted as destructive: a swap destroys nothing.

### 3. Three placements, in order

For the tapped entry A in lane `a` and the occupant B in lane `b`, both in race R:

| # | Entry | Placement | Effect |
|---|---|---|---|
| 1 | A | race R, no lane | A vacates `a`; race and heat kept |
| 2 | B | race R, lane `a` | B takes A's lane; frees `b` |
| 3 | A | race R, lane `b` | A takes B's lane |

No write carries a heat, so the server derives it and it does not change: **a swap has no scoring effect.** The tapped swimmer vacates first, so if anything goes wrong the row left without a lane is the one the marshal is looking at. Outbox items drain in UUIDv7 capture order, so any interleaving with a drain pass is safe, and each item's idempotency key makes a replay land once. The three are enqueued before a single sync nudge.

### 4. Partial failure is visible, never two-in-a-lane

The swap is not a transaction, and a parked item does not stop the drain:

- **Write 1 refused**: all three park; nothing moved.
- **Write 2 refused** (a third device took lane `a` in between): A is in the race with no lane, B is unmoved, write 3 parks too.
- **Write 3 refused alone**: A has no lane, B holds `a`.

In every case the swap can leave the tapped swimmer with no lane, which is true, shown and recoverable; it can never leave two swimmers in one lane or show a move that did not happen. The fastest recovery is on the deck (give A a free lane, or swap again); the parked items then still need **Discard** on the Outbox screen, or [ADR-0029](0029-a-stuck-outbox-is-loud.md)'s parked banner rises over a solved problem.

### 5. When the other swimmer already moved

The swap states placements; it does not assert the state it was armed against. If another device already swapped the pair, write 2 is a no-op and both converge. If B moved to another lane meanwhile, the exchange is carried out against the race as it now is. No version token or new refusal code is needed: lane uniqueness answers every lost-update case.

## Consequences

- The routine swap no longer parks anything.
- The lane control's list grows by one entry per occupied lane.
- The audit trail shows three `entry.marshal` records where a person performed one act; each is individually true.
- A failed middle write leaves two parked items rather than one, the cost of not blocking the queue.
- Exchanging swimmers between races remains two independent placements.

## Rejected alternatives

- **A server-side atomic swap endpoint** — a new route, permission check, audit action, outbox kind and transport branch to replace three writes the server already accepts; it cannot help offline either, since the item still waits in the outbox. Revisit only if partial swaps happen in practice.
- **Offer taken lanes as plain "Lane 4" and infer a swap** — the ambiguous mis-tap the vacant-only rule prevents.
- **Drag and drop** — wet hands and a stopwatch, and no accessible equivalent that is not this control.
- **A "how to fix" hint on `LANE_TAKEN`** — a cure that still leaves two parked captures per swap.
- **Block the drain behind a failed swap item** — changes the drain's contract for every kind to tidy one edge case.
