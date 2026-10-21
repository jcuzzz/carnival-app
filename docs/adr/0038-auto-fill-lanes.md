# ADR-0038: Auto-fill lanes, and one lane control

- **Status:** Accepted (2026-09). Partly superseded by [ADR-0039](0039-the-heat-is-the-context.md): the deck toolbar is gone (adding a heat is in the heat picker), and server placement at entry creation now runs on every create, not only where lanes auto-fill.

## Context

Every swimmer placed cost the marshal a lane decision: an inline lane select on each row, an empty-lanes panel on every race block with per-lane Assign buttons, swaps, and a dead end once a division had two races. Many schools do not want to make that decision at all; others (an inter-school or district meet) need lanes. The answer is to **automate the decision, not remove the concept**, and then simplify what is left of the UI.

## Decision

1. **`autoFillLanes` is a carnival setting beside the lane count.** Both sit together under **Lanes** on the Manage screen. Auto-fill requires a lane count: the server refuses `autoFillLanes: true` without one (`AUTO_FILL_LANES_REQUIRES_LANE_COUNT`); no lane count is ever assumed. It applies only to Time-measured events; distance, height and count events are never laned. The New carnival form asks for the lane count (8 suggested) with auto-fill pre-ticked; a carnival created without either field has auto-fill off. After creation, only the settings endpoint changes them. The lane count cannot be lowered below a lane already in use.
2. **The server fills the lane at entry creation.** With auto-fill on, an individual or team entry created in a Time event (online or through the outbox) is placed in the same transaction: in the division's latest race that has not been swum and has a free lane, lowest free lane first; otherwise in a newly minted race, lane 1. A race is *swum* once any of its entries has any result (a DNS included): a late swimmer would re-rank a contested heat. Placement uses the ordinary marshalling path, so heat derivation is unchanged and heats form in entry order. The audit marks the placement `autoFilled: true` beside the creating person: a person created the entry, the system chose the lane.
3. **Server-side, never client-side.** Two offline devices cannot both take lane 3 when the server hands lanes out at drain time in queue order. The device shows "lane pending" until the create lands.
4. **Auto-fill never moves an existing swimmer.** It touches new entries only; a race it mints holds only the new entry (no sweep of unplaced entries); entries already unassigned when the box is ticked stay as they are.
5. **The entry is the record, the lane is a convenience.** If placement fails (another marshal took the lane, contention exhausted the retries), the create still succeeds, unlaned.
6. **One lane control per row, spanning every heat of the division.** It lists the free lanes grouped by heat, swaps ([ADR-0030](0030-lane-swap.md)) and removal. A lane in the row's own heat is one tap; a lane in another heat moves the swimmer between placing pools, so it arms a confirm. A swimmer who is in a race and has a result is offered no heat but their own; they leave it only through the confirmed removal. The per-block empty-lanes panel and its Assign buttons are deleted.
7. **A race records the division it was minted for** (`Race.CarnivalEventId`, `RaceDto.eventId`), so an empty race is attributable on every device and auto-fill can see a heat a marshal has just added. It is an attribution, not ownership: an administrator-created race may span divisions and carries none.

## Consequences

- With auto-fill on, capture loses the lane decision: add entry, type time. Lanes are still recorded, printed and swappable.
- Auto-fill only appends to the latest unswum race; a gap left in an earlier heat (a removal, a swap) is never refilled automatically.
- "Latest" is the organiser-editable race sort order, so reordering races changes which race fills.
- Two devices finding the latest race full at the same moment can mint two races, one holding a single swimmer.
- The unkeyed online create commits the entry, any mint and the placement separately; a crash between them leaves the entry unlaned. The keyed outbox path is one transaction.

## Rejected alternatives

- **A lanes-off mode keyed on an unset lane count.** Two modes to test, and it does not serve a school that wants lanes without choosing them.
- **A per-definition auto-fill checkbox.** The measure type already says which events have lanes; the switch belongs beside the lane count.
- **Client-side auto-fill.** Only the server knows lane occupancy.
- **Back-fill existing unassigned entries when the box is ticked.** A bulk placement that may mint heats in a division with results; if needed, it should be an explicit, audited action.
