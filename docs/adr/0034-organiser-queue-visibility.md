# ADR-0034: Organiser queue visibility

- **Status:** Accepted (2026-09)

## Context

[ADR-0002](0002-offline-sync-design.md) imagined a finalisation gate that waited until every device queue had drained, but the server had no way to know about device queues. [ADR-0029](0029-a-stuck-outbox-is-loud.md) made a stuck outbox loud on the device itself. That leaves out the case that ends a carnival badly: an iPad in a bag with forty captures on it, whose holder has gone to lunch. A device shouts at whoever is holding it, and the problem is that nobody is.

The device id (`X-Device-Id`) already travelled on every idempotent write, but the server learnt a device existed only when it wrote successfully, which is the one thing a stuck device never does.

## Decision

1. **A device is known from its first report. There is no registration step.** `PUT /carnivals/{id}/devices/{deviceId}/sync-status` upserts a row keyed by carnival and the client's existing device id. A step between picking up an iPad and capturing would be skipped by exactly the devices this exists to catch, and there is nothing to approve: the report grants nothing and carries no student data. Scope is per carnival: the question is whether this carnival can be finalised, not what devices the school owns.
2. **Any carnival role may report; only administrators read the fleet.** Every role can hold a device and so a queue, and a device that cannot report is the failure this prevents. Over-reporting its own counts only draws attention; under-reporting cannot suppress a capture, because nothing is gated on the report. **A device may speak only for itself**: the `X-Device-Id` header must match the route (`403 DEVICE_ID_MISMATCH`), so no caller can zero another device's row before finalisation. The fleet list names officials and what they carry, so it needs `carnivals:administer`.
3. **The report holds counts, never student data**: pending, parked, the oldest pending capture time and the app version; the server adds last seen, last drain, and the reporter's identity. This is the premise of the open reporting policy; putting a student in the report would reopen it. Reports are not audited: they arrive every minute from every device, would bury the audit table people read to reconstruct a result, and change nothing anyone could dispute.
4. **Reporting rides the sync worker; it never gets in its way.** After each pass the app reports if a carnival is selected and the device is online: at once when the pass moved something or on first contact, otherwise at most once a minute and only while work is outstanding. An idle, drained device goes quiet. The report is fire-and-forget with a timeout and its failures are silent, so telemetry can never delay delivery. A dedicated timer was rejected: a heartbeat that outlives a stopped worker looks healthier than the truth.
5. **Finalisation warns; it does not block.** The Manage screen reads the fleet beside the Finalise control and names, worst first, devices that are reporting but not draining, devices that stopped reporting with captures pending, devices with parked items, or that the list could not be read. The server finalises regardless:
   - an offline device and a lost one look identical from the server; a block would turn every silence into a veto;
   - a block would hold the record book, district list and results hostage to one iPad in a taxi, whereas a gap can be re-entered from paper;
   - a device that never reports has no row, so a gate over rows punishes only honest devices.
6. **Staleness alone is never loud, and freshness alone is never reassuring.** A drained device stops reporting by design, so "not heard for 15 minutes" with nothing pending is *quiet*, not lost. A device that keeps reporting the same backlog (typically a signed-out device whose worker is paused) is *not draining*, the loudest state. The server derives the last-drain time from the reported pending count falling. Ages are measured against the server's clock, carried on the list response, so a skewed desk clock cannot invent or hide stranded devices. The list polls once a minute, because a device going silent announces nothing. It is never cached by the service worker.

How the states and reporting rules work: [offline outbox, device sync status](../architecture/offline-outbox.md#device-sync-status).

## Consequences

- An organiser can see, before publishing, which device stopped reporting and what it holds. Publishing over a gap is an informed act.
- A small always-on write path: one request per device per minute while work is outstanding.
- Two staleness thresholds (5 minutes on the device, 15 here) measure different things: one capture not moving versus a device that checks in once a minute going silent. Both are judgements to revisit after the pilot.
- The panel names a device's holder to administrators; worth a line in the privacy impact assessment.
- **Residuals.** Counts are as of the last report, not now. A device that has never reported to this carnival is invisible, and the empty panel says so. A stolen or destroyed device's queue is unrecoverable; this only quantifies the loss before publication. The device id is client-asserted: clearing site data makes a duplicate row (an over-report, never an under-report). Counts are self-reported and cannot be checked server-side.
- Finalisation records who finalised and when in the audit trail. The app does not ask for a reason when finalising; the server records a default one.

## Rejected alternatives

- **A hard server-side finalisation block on undrained devices.** Silence is ambiguous; a block converts every ambiguity into a veto over publication.
- **Pre-registration** (pairing code, approved device list). A step the devices that matter will skip, granting nothing that needs approving.
- **A per-device "account for this device" acknowledgement.** A second act saying what finalisation already records.
- **Sending parked items to the organiser** so conflicts can be resolved from the desk. Moves student names off the device; a separate feature with its own privacy analysis.
- **Auditing every report.** Thousands of rows a day in the table people read to reconstruct a result.
