# ADR-0031: Shared-device hygiene

- **Status:** Accepted (2026-09)

## Context

The application layer was strong and the *device* layer thin. The typical event-day device is a school iPad on a pool deck, shared between volunteers, put down on a bench and picked up by whoever is nearest. Four gaps were one problem seen from four angles:

- **The service worker wrote the staged roster to disk.** Its API-read cache was a denylist, so the roster-import validation and reconciliation reads (names, student keys, the category [ADR-0026](0026-the-student-category-is-server-side-only.md) keeps server-side) landed in Cache Storage, and every future endpoint inherited "cacheable" by default.
- **No handover story.** The session survived restarts and ended only on an explicit sign-out; the realistic path is "put it down and walk away".
- **`/outbox` was ungated.** It lists captured student names, and Requeue / Edit-and-requeue had no session check, so a second person could alter a payload that then drained under the original official's captured actor.
- **Accepted rows kept the student's name**, though `payload.display` exists only to render the row and is never sent.

Two constraints bound every answer: the outbox is **pause-never-drop** ([ADR-0002](0002-offline-sync-design.md), [ADR-0025](0025-outbox-recovery.md), [ADR-0029](0029-a-stuck-outbox-is-loud.md)), so no hygiene control may lose a queued capture; and the cached roster can only be refilled from the server, so anything that clears it while the server is unreachable strands the deck.

## Decision

1. **The API-read cache is an allowlist.** The service worker caches only named carnival-day GET routes (carnivals, divisions, entries, results, races, schedule, progress, standings, adjustments, settings, event definitions, houses, records). A new endpoint is uncached until someone adds it on purpose. Never cached: imports (the staged roster), reports (each export is generated and audited server-side, [ADR-0004](0004-reporting-and-publishing.md)), eligible students (IndexedDB already holds them with a lifecycle), `/me` and `/version` (a stale identity is a stale role decision). Entries expire after **24 hours**, not the cookie's 12: a device primed on school wifi the afternoon before is at the venue next morning, and sign-out deletes the cache outright anyway. The predicate is serialised into `sw.js` with `toString()`, so it must stay self-contained.
2. **Idle sign-out after 60 minutes, but never while the server is unreachable.** Idle is measured from real interaction (pointer, key, touch). When it fires, the app probes `/health`, never trusting `navigator.onLine` (the venue access point can be up with the uplink dead). If the server answers, it signs out, ending the server session too. If not, it covers the screen with an opaque privacy overlay ("Tap to resume") and re-probes every 5 minutes and on `online`; the first answer completes the sign-out. Signing out clears the roster cache, which cannot be refetched while unreachable; stranding a marshalling point to hide a screen nobody is looking at is not a close trade.
3. **The outbox is untouched in every branch.** Queued captures belong to the device and their capture-time official, not the session, and the worker keeps draining behind the overlay.
4. **The outbox list needs a signed-in official.** Without a session `/outbox` shows a sign-in offer instead of the queue. The header chip and stuck-outbox banner stay visible: they are counts, and an unattended device is the one most likely to hold stranded work. Requeue and Edit-and-requeue need a session, as Discard does. An edit by a different official keeps the original capturer and adds `editedBySubject` / `editedByDisplayName` beside it.
5. **An accepted row sheds the student's name one hour after acceptance**, by the retention sweep, not at the transition: the deck's optimistic rows render from `display` until the refetch lands. Conflict, rejected, cancelled and discarded rows keep the name, because a person has to decide about them.
6. **The session cookie is fixed at 12 hours, not sliding** (see [authorisation and audit](../architecture/authorisation-and-audit.md#shared-devices)).

## Consequences

- The staged roster never reaches device storage, and the next endpoint carrying student data is uncached by default.
- A route that needs to work offline but is not on the allowlist silently does not; adding a screen means adding its read and its test.
- A device whose probes never answer again stays signed in behind the overlay until someone resumes or signs out by hand. That is the deliberate trade in §2.
- An edit by a second official is visible on the row rather than presented as the first official's capture.
- **Residual for the privacy impact assessment:** a conflict row keeps a student's name on the device until someone discards it (unfinished work is never aged out, [ADR-0013](0013-outbox-retention.md)), so discarding a parked item is a privacy step as well as queue hygiene. The accepted row's stored server response no longer carries the name ([ADR-0051](0051-outbox-ack-narrowing.md)).
- Sixty minutes is a judgement to revisit after the pilot: long enough for a marshal watching a 40-minute block, short enough that a device on a bench is signed out by pick-up.

## Rejected alternatives

- **Add `/imports/` to the denylist.** Fixes this endpoint and none of the next.
- **Cache nothing.** The service-worker cache is the only read cache a cold start at the venue has.
- **Sign out on idle regardless of connectivity.** Strands the deck (§2).
- **A short idle timeout (5–15 minutes).** A marshalling iPad is legitimately untouched for an event block; a lock that fires mid-carnival trains volunteers to defeat it.
- **Clear the outbox on idle or sign-out.** The queue is the only copy of an undelivered capture.
- **Strip `display` from conflict rows too.** Makes the recovery screen undecidable.
- **A PIN on resume.** There is no local credential to check; the overlay is a privacy cover, and the access control is the sign-out that follows.
