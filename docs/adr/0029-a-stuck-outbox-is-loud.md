# ADR-0029: A stuck outbox is loud on the device

- **Status:** Accepted (2026-08). The organiser-side view this ADR left out is [ADR-0034](0034-organiser-queue-visibility.md).

## Context

A queue only grows while someone is recording, so a backlog that is not caused by the network always means a fault the recorder cannot see. Three shapes are silent precisely because the app is designed to keep working:

- **A lapsed session.** A `401` pauses the queue and never drops it ([ADR-0002](0002-offline-sync-design.md)); every later capture piles up behind a session that will not come back on its own. A session can still lapse mid-day (an idle sign-out, a revoked sign-in, a day longer than the 12-hour session), so this is a real case, not a theoretical one.
- **A head-of-line stall.** The drain stops at the first item that should be retried, to keep capture order, so one refused item holds everything captured after it (`EVENT_NOT_OPEN`, `NOT_ASSIGNED_OFFICIAL`).
- **Parked items.** They are kept indefinitely because a person is meant to deal with them, and [ADR-0025](0025-outbox-recovery.md) gave that person the actions. Nothing told them to.

The header chip already reports the counts accurately, but it is a small element in a crowded header: it reports, it does not escalate. [ADR-0017](0017-automatic-result-validation.md) declined a drain gate at finalisation because an undrained device has to be caught live, while the day can still be fixed.

## Decision

### 1. Scope: the device, about its own queue

A device is loud to whoever holds it. It knows nothing about other devices; seeing which device stopped reporting is the organiser's view ([ADR-0034](0034-organiser-queue-visibility.md)).

### 2. Three causes, and only three

| Cause | Condition | Rank |
|---|---|---|
| **paused** | the queue is paused by a `401` | 1, worst |
| **stalled** | online, server reachable, not paused, and the oldest undelivered capture is **≥ 5 minutes** old | 2 |
| **parked** | a `conflict` or `rejected` item captured ≥ 5 minutes ago | 3 |

- **Paused is loud immediately**, whatever the connectivity: the server has already said the queue is stopped, and waiting buys nothing.
- **Stalled is measured by age of the head item, not queue size.** A large queue is a busy recorder; an old head item is a queue that cannot move. Five minutes clears several retries at the 60-second backoff cap, is about two heats (the swimmers are still on deck), and is long enough that transient blips stay silent. It is one named constant, to be revised from the pilot.
- **Parked escalates after the same five minutes.** At the moment of parking the deck row already shows it; five minutes later that screen is gone and the item is stranded. A fix made elsewhere (redoing a lane swap on the deck) does not clear parked items; only Requeue or Discard does.

Ages are read from the item's capture time, so a requeued old capture is "old" at once, which is true until it lands.

### 3. Offline alone never escalates

An offline queue building up is normal and stays silent: the stalled rule requires a reachable server. Offline is a supported state, the connectivity strip already says so full-width, there is no action to offer, and a banner that is up all day at a poor-coverage venue becomes wallpaper that takes the true banners down with it. Instead the connectivity strip names the size of the backlog it is holding.

### 4. One banner, one cause, one action

A full-width banner above the connectivity strip, in the document flow so it never covers content.

- Never stacked: the worst cause wins.
- Cause-specific copy naming the count.
- Exactly one action: **paused** → Sign in (returning to the current page, and resuming the queue on success); **stalled** and **parked** → Open the Outbox.
- No dismiss and no snooze. The way to remove it is to fix it, which is why it must never be wrong.

Each cause clears the moment its condition goes false, which may reveal the next cause down.

### 5. Accessibility

Paused is `role="alert"`; stalled and parked are polite status regions, since they are at least five minutes old when announced. The banner is keyed by cause, so a change of cause is re-announced. Colour is never the only signal.

### 6. A pure derivation

The rule is one pure function over counts, the pause flag, the oldest capture times, connectivity and an injected clock. The shell re-reads the clock every 15 seconds, because a queue that has stopped moving produces no database changes.

## Consequences

- The three silent shapes are loud on the device that has them, to the person who can fix them, and ADR-0025's recovery surface gains something that sends people to it.
- The banner is a credibility budget: any future cause must argue against §3, and the threshold is a judgement until the pilot.
- Detection lags the threshold by up to one tick (about 5:15 worst case).
- A device nobody is holding, or one that never regains the network, is silent here; that is the organiser view's job.
- A flapping uplink raises and drops the stalled banner; to be watched in the pilot.
- A corrupt capture time makes the rule blind to that row rather than raising a false banner.
- On a phone, banner plus connectivity strip cost noticeable deck height.

## Rejected alternatives

- **A modal** — blocks capture to report work that is already safe.
- **A toast** — passes; the requirement is prominence that survives being ignored.
- **A bigger chip** — no colour makes an element in the shared header row the first thing seen.
- **Gate on the drain's stop reason** — in-memory, lost on reload, and puts detection inside the delivery contract.
- **Escalate offline after a longer threshold** — fires daily at a poor-coverage venue; with coverage, stalled already catches it.
- **Dismissible banner** — agrees the fault does not matter on the word of a busy person.

See [offline outbox: the stuck-outbox alarm](../architecture/offline-outbox.md#the-stuck-outbox-alarm).
