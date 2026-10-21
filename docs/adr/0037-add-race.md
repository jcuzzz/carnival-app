# ADR-0037: Add race

- **Status:** Partly superseded. The control is now **"+ New heat"** in the deck's heat picker ([ADR-0039](0039-the-heat-is-the-context.md), [ADR-0042](0042-condensed-deck-header.md)); the "Add race" button, its place on a race block and its "Race N" vocabulary are retired. The rule that adding a heat creates an empty one, ungated on fullness, stands.

## Context

A marshal could not find how to make a second heat for a division. The capability existed, but its control appeared only when every lane was full *and* swimmers were waiting, and its label was a twelve-word sentence. So a deliberate split (twelve swimmers at an eight-lane pool run 6+6) was impossible: the only reachable split was 8+4. The control also moved every waiting swimmer into the new race unlaned, so the marshal still had to lane each one, and the side effect made the control impossible to name simply.

Heats are never typed: a heat is derived from race membership when a swimmer is placed ([ADR-0015](0015-heats-races-and-lanes.md)), which keeps a placing pool from splitting on a tap.

## Decision

1. **Adding a heat is one plain, always-available control**, not gated on lanes being full or swimmers waiting. It is unavailable only while the division's races read is unresolved (a mint off an unresolved read could duplicate an existing race, and the server has no duplicate guard), while this device already has a mint for the division queued or refused, or while the division has no entries (an empty heat cannot be removed from the deck). Each unavailable state says why.
2. **It creates an empty heat; the client moves nobody.** The marshal places swimmers into it like anyone else. The server's materialise still sweeps any entry of the division that is in *no* race into the race it creates, so legacy raceless entries can join it.
3. **No server change**: the mint is the existing `race.materialise` outbox kind, and placement is `entry.assign`.

## Consequences

- A division can run as deliberately smaller heats (6+6, 4+4+4).
- A mistaken extra heat cannot be removed from the app. Race deletion exists only on the API (`carnivals:administer`); there is no UI for it.
- Two devices adding a heat at the same moment make two races: the queued-mint gate is per device, and each mint carries its own client id.
- Bulk race membership is gone; it had no scoring meaning.

## Rejected alternatives

- **Keep the mass-move under a short label.** A two-word control that silently relocates every waiting division-mate, with no confirm, for no saved taps.
- **A typed heat number on the row.** Reintroduces the split-pool hazard heat derivation exists to prevent.
- **Pre-planned heat counts** ("two heats of six"). Seeding by another name; heats are assigned live at marshalling.
- **Gate on swimmers waiting.** Still a conditional appearance for a control that has to be findable.
