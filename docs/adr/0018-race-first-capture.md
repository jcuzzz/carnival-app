# ADR-0018: Race-first capture — the division selector, and combined races

- **Status:** Accepted (2026-08). Partly superseded: cross-division combined races are retired by [ADR-0019](0019-retire-cross-division-combined-races.md) (materialisation of a single division survives); the assign-lanes mode is replaced by inline lane editing ([ADR-0020](0020-flatten-lane-handling.md)); the deck shows one heat of the division at a time, every division gets a race on its first add, and a create names its heat itself ([ADR-0039](0039-the-heat-is-the-context.md)).

## Context

[ADR-0015](0015-heats-races-and-lanes.md) introduced races, and its first surface was a standalone races index and race screen beside the event page. That created two doors to the same capture rows, and forced every screen that could reach an entry to decide whether to mark or redirect a raced entry. Separately, the event page's division chips (one per age × category, so twelve buttons for a six-age carnival) were too many controls on the screen a recorder uses all day. ADR-0015's model (races, heat placing, race-scoped lanes) was right; the standalone screens were the wrong door.

## Decision

### 1. One capture workflow, framed by division

Carnival → event tile → division → capture. There is no second route to the same results: the standalone races index and race screen are removed, and a division's races are reached inside its capture view. Where lanes are not used, as in most field events, there is no lane chrome: the race is plumbing that becomes visible only where the venue makes it visible. "Next event" follows the program.

### 2. A division selector instead of chips

- A **category row** and an **age row**, derived from the divisions that exist for the event, never from an enumerated space. A single-category event shows no category row; a **tier row** appears only where both tiers exist.
- **Every chip navigates.** Tapping a category whose current age does not exist lands on the nearest existing age (ties go to the younger). A dead-end chip is worse than an approximate landing, because the landing announces itself in the chip, the header and the URL.
- Where an event has both tiers, every division-naming chip under Social carries "· Social", so a Competitive division and its Social twin never render alike.
- **Every selection is a URL naming the division, never the race.** The race may not exist yet, or may be created by another device a second later.
- A tile opens the official's assigned division of that event where they have one, otherwise the first in program order.
- Event names are data. No screen parses or reorders a name.

### 3. Races are created on demand

Nothing pre-creates a race for every division of a carnival. A race is created when marshalling needs one: today on a division's first add ([ADR-0039](0039-the-heat-is-the-context.md)) or an explicit new heat ([ADR-0037](0037-add-race.md)). A division's races are found by entry membership over the carnival's race list, which works offline.

### 4. Materialisation is one server operation, held by marshals

`POST …/races/materialise` creates a division's race and moves the division's entries that are in no race into it, unlaned, in one transaction, audited once as `race.materialise` and idempotent on a client-minted race id. A field larger than the lane count is warned, not refused: a mass start is legitimate. It sits on `carnivals:marshal`: it is placement-shaped, and without it a marshal could not lane the first swimmer of any division without an administrator beside them. Naming, scheduling and deleting races stay `carnivals:administer`.

### 5. Race membership never touches the heat

Joining a race writes race membership only (`RaceId`, lane cleared, the audit record); `Entry.Heat` is not derived, assigned or reset, so a materialised division goes on placing as one field. Heats are assigned only by the placement path. **The cohort-heat rule:** when a placement opens a new heat, that heat is written to every heatless entry of the same division in that race, in the same transaction, and the audit records it (`cohortHeatAssigned`). A placing pool moves as one and never splits on a single tap.

How it works: [data model § races, heats and lanes](../architecture/data-model.md#races-heats-and-lanes).

## Consequences

- One workflow, reached the way staff already reach events; twelve buttons become two or three short rows.
- For field events, relays and Social divisions the race is invisible.
- The scoring engine is unchanged: placing per (division, heat), and district selection and records read the division ladder.
- A division may hold several races; its progress aggregates over all of them.
- **Known residual:** an entry given an explicit heat makes later heatless placements in that race join it one at a time, which can leave part of the pool heatless. It is reachable only through the deliberate override.

## Rejected alternatives

- **Keep both surfaces and improve the race index**: two navigation models for one capture path.
- **A race row per division, created eagerly**: a backfill and a row for every division that never needs one, for no visible difference.
- **Materialisation as many queued `entry.assign` items**: many audit records for one act, and a half-completed state no record describes.
- **A race-keyed URL**: true only after the race exists.
- **Server-side auto-placement on entry creation**: rejected here; later adopted in a narrower form, where the create names the heat the marshal is standing at ([ADR-0039](0039-the-heat-is-the-context.md)).
