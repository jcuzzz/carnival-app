# ADR-0014: Result-entry navigation and the carnival schedule model

- **Status:** Accepted (2026-08). Partly superseded by [ADR-0058](0058-one-age-per-division.md) (a division's age is one age or `OPEN`, not a set) and [ADR-0018](0018-race-first-capture.md) (the division chips became a category and age selector). The carnival's tabs are [ADR-0024](0024-carnival-context-and-primary-navigation.md).

## Context

Result entry is the core activity of the day, but the path to it was built for finding an event, not working through a carnival: one flat list of 70–140 divisions behind a filter bar, and a full round trip for every move to the next age group of the same stroke.

Real carnival programs showed how a carnival actually runs:

- **Time-slotted blocks, not numbered events.** Swimming runs "9:45 Freestyle events" holding that stroke's divisions in order, with novelty events and logistics interleaved. Athletics runs a track column beside several field-site columns at once.
- **Out of order is normal.** Events run out of order and students arrive out of order because of clashes. The schedule is a plan, not a contract.
- **The model lacked** a Competitive/Social tier, non-competition program items, and any notion of the program itself.
- **Labels such as "16+" are session labels**, not merged divisions: 16, 17 and 18 are separate events with their own placings and points that may run in one session.

## Decision

### 1. Schedule: streams of slots, advisory, never gating

- A carnival has one or more **schedule streams** that run concurrently (swimming: one; athletics: track plus each field site). A stream holds ordered **slots**, each with optional local start and end times and either divisions in listed order or a **non-competition item** (a label, optionally naming the adjustment category its novelty points are awarded under, never results).
- **Nothing is gated on the schedule.** Entry, capture, eligibility and the lifecycle ignore it. A division in no slot is fully usable. Progress, not the clock, says what is left.
- The model is stream-and-slot-shaped from the start, so athletics is configuration, not a migration.

### 2. The organiser builds the program in the app

The schedule is built in the app, replacing the word-processor document as the source of truth, and prints as a program page through the browser. Clarity beats fidelity to the old layout; streams are columns.

### 3. Navigation

- **Tiles derived from data, never hard-coded**: one tile per event definition, showing progress, so each sport shapes its own grid from configuration.
- **"My events" first**: an official with division assignments sees those tiles in a block at the top, everything else below. Assignments are advisory ([ADR-0016](0016-role-model-simplification.md)).
- **Switching division is a URL navigation.** Every division keeps its own route, so deep links, refresh, back, the audit trail and the outbox all key off the division id.
- **"Next event" follows the program** (slot order, then listed order) as a suggestion with an easy escape, never a wizard.
- The searchable event list survives as the secondary "find anything" path.
- The UI says **category**, never sex or gender ([ADR-0003](0003-event-division-and-category-overrides.md)).

### 4. Tier, scoring mode, the points switch and division scope

- **Tier is data on the division: `Competitive` or `Social`.** Both score house points by default.
- **Scoring mode is `Places` or `Social`**, defaulting from the tier. Social scoring is a flat per-entry point (default 1, configurable per carnival), so a Social division needs no results to score and has no placings.
- **Any division can be excluded from house points** (`ScoresPoints`), independently of tier and mode.
- **Boys and Girls are separate divisions**, even where the printed program shows one line.
- **A division's age is one age or `OPEN`** ([ADR-0058](0058-one-age-per-division.md)). `OPEN` is reserved for "all ages": a derived senior band on the roster must use a different code, and the import mapping refuses `OPEN`.
- **Divisions that share a session** are several divisions in one slot. Capture stays per division.
- **School records ignore Social.** A swimmer who wants a time to count enters the Competitive division, an ordinary audited entry.

### 5. Wrong-division safeguards

The division stays prominent in the capture header; the eligible-student search is scoped to the division, so a wrong-division name does not match; results remain correctable. Residual risk is accepted.

Current model: [data model § schedule](../architecture/data-model.md#schedule), [scoring § competitive, social and non-scoring events](../architecture/scoring.md#competitive-social-and-non-scoring-events).

## Consequences

- Near-zero navigation on the day, progress on the tiles for end-of-day stragglers, and no parallel program document.
- The schedule builder is net-new surface, and the model carries one more concept than a bare "order" column.
- Tier and scoring mode touch eligibility, scoring and the switcher.

## Rejected alternatives

- **A single program-order or event-number field**: breaks on concurrent streams and on numberless, time-slotted programs.
- **Enforced sequence or a wizard-style "next"**: contradicts out-of-order reality at the busiest moment.
- **In-page division switching without a URL change**: breaks deep links, refresh and back, and muddies audit and outbox attribution.
- **A hard-coded sport taxonomy for tiles**: school-specific structure is configuration.
- **Keeping the document as the schedule's source of truth**: double entry, drift, and no data for "Next event".
