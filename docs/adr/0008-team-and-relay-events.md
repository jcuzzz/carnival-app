# ADR-0008: Team and relay events

- **Status:** Accepted (2026-07)

## Context

Swimming carnivals include freestyle and medley relays: one team per house, per age group, per category. The competing unit is a house team with a single result and placing that earns house points. The entry model was strictly individual (one student, one result), so relays did not fit.

## Decision

Team events are a first-class, additive concept.

- **`EventDefinition.Format`** is `Individual` or `Team`. A team definition has a fixed `TeamSize` and, for a medley, an ordered `LegsJson` of strokes. A freestyle relay is `TeamSize` freestyle legs.
- **A team entry** has a `HouseCode` and no student; its swimmers are `RelayLeg` children (leg order, optional stroke, student and a per-leg roster snapshot). An individual entry has a student and no legs. A check constraint enforces exactly one of the two shapes.
- **A team has exactly `TeamSize` distinct swimmers.** No reserves or substitutes are recorded. A leg records who swam, not a split time.
- **One result per team entry**, through the ordinary result lifecycle and idempotent capture. Its placing scores for the house, multiplied by the definition's scoring weight; the shipped relay templates set the weight to 2, so relays score double.
- **Eligibility**: each swimmer belongs to the team's house and is eligible for the division's age and category. Category overrides do not apply to legs.
- **An open-age relay** is a team division with the age `OPEN`, like any other open division.
- **School records** for relays are kept per age group × category, separately from individual records, where the definition lists record categories. The holder is the team.

The relay programme itself (which ages, which strokes, the medley order back → breast → fly → free) is template data, not code.

The model: [data model § relay teams](../architecture/data-model.md#relay-teams).

## Consequences

- Result lifecycle, idempotency, audit and finalisation are reused unchanged: a team result is a result whose entry is a team.
- Marshalling needs a team-entry flow (pick the house, assign swimmers in leg order) beside the individual one.
