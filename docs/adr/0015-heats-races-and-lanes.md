# ADR-0015: Heats, races and lanes — the physical unit of capture

- **Status:** Accepted (2026-08). Partly superseded: capture is framed by division, not one race screen badged by event ([ADR-0018](0018-race-first-capture.md)), and shows one heat at a time ([ADR-0039](0039-the-heat-is-the-context.md)); cross-division combined races are not offered ([ADR-0019](0019-retire-cross-division-combined-races.md)); the lane filter is removed ([ADR-0021](0021-remove-the-lane-filter.md)).

## Context

Swimming and track events are larger than the venue: a 50m freestyle age group draws more swimmers than the pool has lanes, so the field is swum in several passes. The model had nullable `Heat` and `Lane` integers used only for sorting, with no defined scope, nothing stopping two entries claiming one lane, and placings computed per event.

How the school runs a carnival:

1. **Each heat is its own competition** for house points and ribbons: a 24-swimmer age group run as three heats has three winners.
2. **The event is a separate, time-only ladder.** The fastest times across all heats go to the district team and decide school records. It carries no house points.
3. **There is no seeding.** Lanes fill as students present themselves at marshalling.
4. **Concurrency is ordinary.** Small divisions may share a pool pass, and a track sprint is often recorded by several staff at once, each covering some lanes.

## Decision

### 1. The heat is a subdivision of a division, and the placing unit

A heat belongs to one division (`CarnivalEvent`). For a Time event, placings, and therefore house points, are computed within a heat; entries with no heat form their own field. Each heat keeps its own placements: removing heats from scoring was considered and refused. A Distance, Height or Count event places across the whole division, whatever its flights ([ADR-0053](0053-field-attempts.md)).

### 2. The event ladder is time-only and scores nothing

Ranking a division across all its heats serves district team selection and school records. It awards no house points.

### 3. The race is the physical grouping, and it owns the lanes

A **race** is what goes off together. It belongs to the carnival, may sit in a schedule slot, and may hold heats of more than one division. **Lane uniqueness is enforced per race.** The heat answers *whose field is this*; the race answers *what goes off together*. A race organises capture; its absence never blocks a result.

### 4. Several recorders need no locking

Each result is an independent idempotent write keyed by its entry ([ADR-0002](0002-offline-sync-design.md)), so recorders covering different lanes never collide, online or offline. Two captures of the same entry are refused (`RESULT_EXISTS`) and parked for a person, which is the right outcome for a genuine double capture.

### 5. Heat and lane are assigned live, at the marshalling point

No seeding. Assignment happens on the deck as students arrive, through the offline-capable outbox path ([ADR-0010](0010-offline-entry-creation.md)). A student enters a given event once per tier, so appears in one heat. Heats and lanes stay correctable after a result lands.

### 6. Lane count is venue configuration

One lane count per carnival (a carnival is at one venue), held in the carnival's settings. With none set, lanes are bounded only by a generous ceiling. There is no per-division lane or seeding configuration.

### 7. The grouping is generic

Flights in field events are the same idea as heats, so nothing is named or typed for swimming only. Sport-specific structure is data.

### 8. Social heats are logistics only

A Social division may be split into heats for venue reasons; its scoring stays flat per entry with no placings ([ADR-0014](0014-result-entry-navigation-and-schedule-model.md)).

How it works: [data model § races, heats and lanes](../architecture/data-model.md#races-heats-and-lanes), [scoring § two ladders](../architecture/scoring.md#two-ladders-over-one-set-of-results).

## Consequences

- Large events are modelled correctly; several staff can record one race with no coordination protocol; field and track share one structure.
- Two ladders over one set of swims: points read the heat, district selection and records read the division. No screen may blur them.
- Division progress aggregates over races.
- **Accepted trade-off:** unseeded heats with heat-level placings make house points depend partly on the draw. If heat 1 draws the three fastest swimmers (31.2, 31.8, 32.1) and heat 3's winner swims 35.0, the second-fastest swimmer earns 3 points and the slower winner 4; district selection still takes 31.2 and 31.8. This is the school's choice, the cost of every heat having something to race for, not a property of the software.
- Out of scope: seeding (needed only at zone or state level), heat-to-final progression, pre-event marshalling.

## Rejected alternatives

- **The heat owns the lane (no race)**: cannot express two divisions sharing a pass, and leaves lane collisions unpoliced.
- **The race owns everything and the heat is derived**: loses the division ↔ heat ownership that keeps scoring and record attribution simple.
- **Heat-to-final progression**: not how the school runs a carnival; every heat counts.
