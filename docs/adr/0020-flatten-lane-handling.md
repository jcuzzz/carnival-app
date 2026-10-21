# ADR-0020: Flatten lane handling — inline lane editing, a multi-lane filter, no assign mode

- **Status:** Accepted (2026-08). §1 (the multi-lane filter) is superseded by [ADR-0021](0021-remove-the-lane-filter.md): the filter is removed. The lane control's options are amended by [ADR-0030](0030-lane-swap.md) (swap with an occupied lane); §3's per-race blocks are replaced: the deck shows one heat at a time with "+ New heat" in its heat picker ([ADR-0037](0037-add-race.md), [ADR-0039](0039-the-heat-is-the-context.md), [ADR-0042](0042-condensed-deck-header.md)).

## Context

The deck (result entry) and an "assign lanes" mode were two views of the same division, toggled, so every marshalling act paid a context switch. The mode had been designed when combined races needed a sibling surface; with combining retired ([ADR-0019](0019-retire-cross-division-combined-races.md)) it was a full-screen answer to a smaller question. Separately, a recorder often covers three lanes, and the lane filter could hold only one.

What makes a lane more than a plain value is unchanged and server-enforced: the first assignment in a division materialises its race, an assignment can open the heat that decides house points (the cohort-heat rule, [ADR-0018](0018-race-first-capture.md)), and lanes are unique per race and bounded by the venue's lane count. The question was only which door the writes go through.

## Decision

The change is to the app only: no server contract, permission or scoring rule moves, and every write is the existing `race.materialise` or `entry.assign` outbox path.

### 1. The recorder filter holds a set of lanes

Superseded by [ADR-0021](0021-remove-the-lane-filter.md).

### 2. The lane is edited where it is displayed

Each deck row carries an inline lane control on its metadata line, away from the time-entry controls. Its options are the race's lanes; picking one queues the same placement the mode did. For an entry in no race, the first pick materialises the division's race. **Remove from race keeps a two-step confirm**, because it clears the heat. Team entries occupy lanes like anyone and use the same control.

A control that can mint a race offers the mint only when a successful read shows the division has no race; while the race list is unresolved the control is disabled. The server has no duplicate-race guard, so this is what stops one tap minting a second race and moving a swimmer's heat.

### 3. The assign-lanes mode is retired

The toggle and the separate sheet go. Everything the mode held (placing unplaced or late swimmers in a lane, starting a further race) moves onto the deck, so there is one surface for capture and marshalling.

## Consequences

- The marshal's flow loses a mode switch.
- Deck rows carry one more control; placing it on the metadata line keeps the row readable at phone width.
- A mis-tap on the lane control mid-race is possible where the mode boundary used to fence it. Accepted: a lane move is an idempotent placement, reversible by picking again, and the scoring-relevant Remove keeps its confirm.

## Rejected alternatives

- **The multi-lane filter alone**: fixes the three-lane case but keeps the mode toggle.
- **Free-text lane inputs**: lose the vacancy list and invite out-of-range typos.
- **A vestigial mode for empty lanes and further races**: two doors to the same writes again.
