# ADR-0039: The heat is the context

- **Status:** Accepted (2026-09). Partly superseded by [ADR-0042](0042-condensed-deck-header.md): the heat chip row is now a heat picker in the deck's selector row, with "+ New heat" as its last item. Non-Time events show no heat picker ([ADR-0053](0053-field-attempts.md)).

## Context

The division page stacked every race of the division as a block, kept a "Not yet marshalled" tail for entries in no race, and put the mint control on a toolbar above it all. A marshal read three kinds of chrome to answer one question, *which heat is this swimmer in?*, and adding a swimmer put them nowhere in particular until a lane tap or auto-fill sorted it out. Specifically:

- **An entry in no heat is a state the venue never has.** The tail existed only because the model let creation and placement be two acts.
- **Adding was context-free.** The add panel did not know which heat the marshal was standing at, so the client guessed (the sole race only, [ADR-0018](0018-race-first-capture.md)) and auto-fill guessed differently (the latest race, [ADR-0038](0038-auto-fill-lanes.md)).
- **Heats were not navigable**, though every other division axis was a one-tap selection.

The scoring model does not move: the heat is still the placing unit ([ADR-0015](0015-heats-races-and-lanes.md)) and a race still backs each heat.

## Decision

1. **The heat is a selection axis.** Each heat is one race of the division, labelled by its ordinal **within the division** ("Heat 2"), not the carnival-wide race number. The selected heat is URL state (`?heat=<raceId>`), defaulting to the **last** heat, the one being filled. A division with no race yet shows a prospective Heat 1. Heats never reorder as they fill.
2. **The server numbers heats by the same ordinal.** When a placement opens a new field, its heat number is the race's position among the division's races, which is what the deck shows. Only where that number is already another race's heat in this division (legacy data, or an API caller's explicit heat) does it fall back to the next unused number, because taking it would merge two placing pools. Existing heat numbers are never renumbered: that would move results between pools.
3. **The deck shows one heat, and adds into it.** Individual and relay creates carry the selected heat's `raceId`, and the server places the new entry in that race in the create's transaction: under auto-fill, the lowest free lane of that race (fullness and swum state are the marshal's call: they chose the heat); otherwise as a member with no lane. A `raceId` that names no race of this division is refused with `422 RACE_NOT_IN_EVENT` before the entry exists, so a refused create writes nothing. Where no race exists yet the device queues the mint first, and the create waits in the outbox until the mint is accepted. The sole-race guess of ADR-0018 is retired.
4. **No create is raceless.** A create with no `raceId` (an API caller, or an add made before the device has read the division's races) is placed by the server's own rule: the latest race of the division that has not been swum (and has a free lane, where lanes auto-fill), otherwise a new race minted for that entry alone. If the named heat cannot be joined, the server falls back to that rule; only if both give up does the entry stay raceless, and the create response says where the entry actually is. Legacy raceless entries show under the first heat until a placement sweeps them into a race.
5. **Moving a swimmer between heats stays on the row's lane control** (ADR-0038), its groups labelled by heat.
6. **Every division gets a race on its first add**, Social divisions included: a race is a generic container, and a per-tier exception would be a second deck shape.
7. **Discarding a refused mint on the device re-points the creates queued behind it to no heat**, so the server's rule places them rather than losing them; queued lane placements into that heat are cancelled.

## Consequences

- One heat at a time and one place to add: the deck is a selector and a list.
- `entry.create` and `team.create` carry an optional `raceId`; no migration.
- Heat labels match what an announcer calls; the carnival-wide race number remains on the Outbox screen and in the audit trail.
- A marshal who picks a full or swum heat gets a swimmer in that heat, possibly unlaned. Nothing refuses.
- A placement that gave up leaves the swimmer under the first heat marked "no heat", with no explanation on the deck; the audit trail has the cause.
- A device that boots at the venue with no uplink and never reads the division's races names no heat on any add, leaving every placement to the server's rule.

## Rejected alternatives

- **Keep all heats on one page and delete the tail.** Removes the worst symptom but keeps the block stack and the context-free add.
- **Device-local heat selection.** A link to a specific heat is what a second tablet needs; URL state costs nothing.
- **Refuse a create that names no race.** Breaks API callers and the device's own fallbacks, and loses a swimmer, where placing costs nothing.
- **Default to the first heat.** The deck's job is capture, and the heat being filled is the last one.
