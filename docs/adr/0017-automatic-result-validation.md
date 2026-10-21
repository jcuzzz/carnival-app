# ADR-0017: Results are validated automatically, not verified by a person

- **Status:** Accepted (2026-08). Replaces [ADR-0002](0002-offline-sync-design.md)'s per-event finalisation gate with one act per carnival.

## Context

The original lifecycle (draft → provisional → verified → final → ratified record) made verification of each result, finalisation of each event and ratification of each record human acts held by a verifier. A swimming carnival runs about 150 events, many split into heats back to back. A human gate on every result and every event will be batched at the end of the day and clicked through, and a rubber-stamped check is worse than an automated one, because it looks like assurance and is not. The platform also replaces a live shared spreadsheet, whose main virtue is speed of update.

## Decision

### 1. Verification is an automatic legality check at capture

A result that passes the server's checks is stored `Verified`, with the rule version that passed it recorded on its audit event. No person verifies. The state keeps its name.

### 2. Incoherent is refused; implausible is never refused

- **Incoherent, refused:** wrong under any reading (a value on a DNS/DNF/DQ, no value on an `Ok`, a non-positive or unparseable value, an entry not in this division, a lane outside the venue's lane count). Refusals carry machine codes ([ADR-0011](0011-api-error-contract.md)) and park the capture for a person.
- **Implausible, warned at most:** a very fast time is probably a typo but might be the school's best swimmer, and refusing it would lose exactly that result. The person who can settle it is at the lane.
- **State-dependent refusals are retryable** (`CARNIVAL_NOT_OPEN`, `EVENT_NOT_OPEN`): *not valid yet* is not *never valid*, and the same capture succeeds once an administrator opens the carnival or division.
- **A second capture for the same entry** is refused and parked: two officials disagreeing is the one case that needs a person.

Every rule runs on the server, which is the authority.

### 3. Plausibility bands wait for data

Per-division plausible ranges need history the school has never collected; the pilot will produce it. None exist yet.

### 4. No per-division finalisation; one carnival-level act

- **Division completion is derived** (every entry has a result, or a Social division has entries), shown as a readout and never a status. It gates nothing: entries are created all day, so a late entry simply makes the division incomplete again. A completion that closed the division would make the next capture draw `EVENT_NOT_OPEN` and stall that device's queue. Division status (`Scheduled`, `Open`, `Closed`) stays the administrator's control, and nothing moves a division to `Finalised`.
- **Carnival finalisation stays a human act, for publication.** At the end of the day someone says "the results are the results": ribbons, the district list and the record book are derived from them. Finalisation makes every result `Final`, mints new school records and freezes house points. Standings are live all day and labelled provisional until then.

### 5. School records are detected, not ratified

No person ratifies a record. During the day a result that beats the standing record is badged, and the dashboard lists the day's record candidates (new or equalled); at finalisation a result that strictly beats the record becomes the new record ([ADR-0028](0028-records-and-district-selection.md)).

### 6. A confirmation on record-beating captures is deferred

A warn-and-confirm when a capture would beat a school record is the one plausibility check affordable without history, but with a fresh record book every first result beats nothing. It is not built.

How it works: [result lifecycle and corrections](../architecture/result-lifecycle.md).

## Consequences

- Results are live within seconds; nobody is asked to perform a check they cannot do well, and no staff member's job is clicking "verify".
- The Verifier role has nothing left and is merged away ([ADR-0016](0016-role-model-simplification.md)).
- **Residual risk:** with no human gate, a mistyped fast time can become a school record at finalisation, and nothing warns about it. What stands between a typo and the record book is correction with immutable revision history, record voiding, and audit of every capture and correction. What is lost is a second pair of eyes on every result, which a school with 150 events back to back never had.

## Rejected alternatives

- **Keep manual verification**: the workload will not be paid; the check becomes a rubber stamp.
- **Refuse implausible results**: a data-loss mechanism that fires on the fastest swimmer.
- **Drop finalisation entirely**: the record book and the district list need a point where provisional ends.
- **Rename `Verified` to `Validated`**: accurate, but a data migration and a breaking change for one word.
- **Gate finalisation on drained device queues**: cannot tell a lost device from an offline one; a stuck queue is made loud during the day instead.
- **Sample-based human verification**: more machinery, less coverage, and the same missing person.
