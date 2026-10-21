# ADR-0021: Remove the lane filter

- **Status:** Accepted (2026-08). Supersedes the lane-filter sentence of [ADR-0015](0015-heats-races-and-lanes.md) §4 and [ADR-0020](0020-flatten-lane-handling.md) §1. The race blocks it leaves standing were later replaced by [ADR-0039](0039-the-heat-is-the-context.md).

## Context

[ADR-0015](0015-heats-races-and-lanes.md) gave the deck a lane filter so a recorder covering a subset of lanes could see only "their" lanes; [ADR-0020](0020-flatten-lane-handling.md) extended it to a set of lanes. Seen built, it was a feature nobody had asked for: a pool has about eight lanes, a recorder can see the other rows at a glance, and the chips row and banner sat permanently above the deck to save that glance. The need behind it, fast typing into your own rows, was never a hiding problem: the deck already renders rows in lane order with the lane on every row.

## Decision

Remove the lane filter entirely: the chips, the banner, its store and clearing rules, and the filtered-deck behaviour. Every row is visible; save-and-advance walks the full rendered order. Nothing else changes: inline lane editing, race blocks, empty-lane disclosures and every server contract stand.

## Consequences

- The deck's only permanent above-the-rows control besides the sync summary is gone.
- A recorder covering lanes 1–3 who saves lane 1 may land on a colleague's row and taps their own next. With eight visible rows this is a glance.
- The filter never guarded correctness. Two captures of one entry are still refused on the server and parked for a person ([ADR-0002](0002-offline-sync-design.md)); that was always the real collision defence.
- A refused placement shows on its row wherever the row carries a lane control; the rest surface on the outbox screen.

## Rejected alternatives

- **Keep the filter behind a disclosure**: keeps the store, rules and tests for a feature with no known user. If a large-venue sport ever needs it, this ADR and ADR-0020 §1 are the record.
