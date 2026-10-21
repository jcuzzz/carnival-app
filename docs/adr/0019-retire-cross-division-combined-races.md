# ADR-0019: Retire cross-division combined races; keep single-division race materialisation

- **Status:** Accepted (2026-08)

## Context

[ADR-0018](0018-race-first-capture.md) built **combined races**: a marshal could merge several divisions' unraced entries into one race, with a "Combine divisions" mode, a combined multi-division view and an uncombine control. It had already been restricted to scheduling only, with no scoring effect. Seen finished, it overcomplicated a simple process and was not needed for the pilot; its hardest defects were all cross-division artefacts (a phantom race on re-combine, a URL asserting a combination the server did not hold, non-atomic multi-race uncombine).

The combine endpoint was dual-use. Besides merging divisions, it was the mechanism behind lane marshalling: a marshal's first lane tap in a division mints that division's race through it, and that mint must stay on `carnivals:marshal` rather than administrator-only race creation.

## Decision

- **Cross-division combining is removed end to end**: the combine mode, the combined view and uncombine, on server and client. The code is deleted, not left dormant; git history is the archive and a revival is a new ADR.
- **Single-division materialisation stays**, named for what it is: `POST …/races/materialise` takes exactly one division, audited and queued as `race.materialise`. Materialising an empty division is legal. Idempotency, the capture-state guards and the lane-overflow warning are unchanged.
- **The cohort-heat rule stays** ([ADR-0018](0018-race-first-capture.md)): materialisation still joins a whole division heatless, so a placing pool must still move as one.
- Two ways to take entries out of a race remain: deleting the race (administrators; heats are kept) and clearing an entry's placement (marshals; the heat is cleared with the race).

## Consequences

- Marshalling loses a mode and the division view a rendering branch; the everyday flow (tap a lane, the race appears, heats derive, DNS, further races) is unchanged.
- **A physically merged race is recorded as separate races that happen to swim together.** Points, heats and placings were already per division, so the loss is shared-lane bookkeeping only.
- A race emptied by clearing placements stays alive for another device's queued placements.

## Rejected alternatives

- **Remove materialisation too**: forces race minting onto administrator-only race creation, so a marshal could not lane a division's first swimmer.
- **Remove the UI but keep the server capability dormant**: untested plumbing behind a surface that cannot reach it rots unnoticed.
- **Keep the name "combine"** for a single-division endpoint: a lying name.
