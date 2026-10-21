# ADR-0006: Shared-device and BYO session policy

- **Status:** Accepted (2026-07). Partly superseded by [ADR-0031](0031-shared-device-hygiene.md) (idle sign-out and cache clearing) and [ADR-0034](0034-organiser-queue-visibility.md) (finalisation warns about devices; there is no "missing device" override).

## Context

Event-day devices are a mix of school iPads, Windows laptops, Chromebooks and staff's own phones (iOS and Android). Venues may have no school wifi, only a hotspot or mobile data. Every queued item and audit event records the official who captured it ([ADR-0002](0002-offline-sync-design.md)), so the policy for signing in, locking and handing over devices decides whether that record means anything.

## Decision

- **Named individual sign-in, always.** No generic or role accounts ("Pool iPad 3"): the actor on every write must be a real person.
- **One session covers a carnival day.** The server's session cookie lasts 12 hours, fixed rather than sliding, so a sign-in at school in the morning is still valid at the end of the day, and a device left signed in does not renew itself. If a session does lapse, the queue pauses and waits for a sign-in; nothing is dropped.
- **Shared iPads keep device auto-lock on**, with a short interval and a passcode held by the carnival administrator for that device pool. The app shows the signed-in official persistently.
- **Handover is sign out, then sign in.** Queued items keep the official captured at entry and continue draining under the new session. Sign-in needs a connection, so device-to-official assignments are planned before the day and an unplanned handover at the venue needs a moment of connectivity.
- **Staff BYO phones are supported without MDM enrolment**, through the same PWA installed or in the browser, under the official's own sign-in. One PWA serves every form factor: installed to the home screen on phones and tablets, in the browser on laptops.
- **A lost device is contained on the server**: revoking the official's identity-provider sessions or app role kills the device's access at its next request. What is at risk is bounded by the unsynced queue and the small cached roster.

How sessions and shared devices work today: [authorisation § shared devices](../architecture/authorisation-and-audit.md#shared-devices).

## Consequences

- Event day gets a concrete run-sheet: pre-assign devices to named officials, sign out and in on handover, report a lost device for immediate revocation.
- The UI is one responsive, touch-first layout that also works in a laptop browser.
- **BYO trade-off, accepted:** no remote wipe, no device-compliance signal and weaker storage persistence than managed devices. Control is concentrated on the server; if a school later mandates enrolment, that is policy, not an application change.
- Revoking a role also refuses anything still queued from that official, so a device should be drained before revocation where possible.
