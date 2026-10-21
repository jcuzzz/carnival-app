# Scoring and house points

House points are never stored. Every read of the standings recomputes them from the carnival's current results, its scoring rules and its bonus adjustments, so a correction, a late result or a voided bonus shows on the next read. The same ranking code feeds the house points, the deck's place badges and the printed results book, so the three cannot disagree.

## Two ladders over one set of results

| Ladder | Field it ranks | What it drives | Code |
|---|---|---|---|
| **Heat placings** | One heat of a Time event; the whole division for any other measure | House points, the place badge on the deck, the Place column in the results book | `HeatPlacings`, `ScoringService` |
| **Event ladder** | The whole division, across every heat | School records, district selection, "records today" on the dashboard | `EventLadder` |

A Time event split into heats is scored **heat by heat**: each heat has its own 1st, 2nd and 3rd, and the fastest swimmer of the carnival earns the same points as the winner of a slower heat. A Distance, Height or Count event split into flights is one competition and pays one set of placings. Entries not yet assigned to a heat form their own field and are ranked among themselves, never against a heat. Why the heat is the placing unit: [ADR-0015](../adr/0015-heats-races-and-lanes.md).

## Placement within a field

`Placement.Rank` is the only ranking implementation.

- **Direction.** Time ranks lowest first; Distance, Height and Count rank highest first.
- **Who is ranked.** Only results with status `Ok` and a measured value. `Dns`, `Dnf`, `Dq` and `Nm` (no mark: attempted, nothing valid) have no place. Draft results never reach the server, and a high-jump result whose card has had every mark removed counts as no result at all.
- **Ties.** Standard competition ranking: tied entrants share the higher place and the next place is skipped (1, 2, 2, 4).
- **Countback.** Where a result has an attempt series (field events), the ranking key is the entrant's valid marks best-first, compared in turn, so two equal best marks are split by the next-best mark. A high-jump card ranks on best height cleared, then fewest failures at that height, then fewest failures up to and including that height. Series equal at every position still share the place.

## Points for a result

Each carnival has its own rules, stored in the carnival's settings and edited on the Manage screen (Scoring rules). Defaults:

| Rule | Default | Applies to |
|---|---|---|
| `placePoints` | `[4, 3, 2]` | 1st, 2nd, 3rd… in a placed field |
| `participationFloor` | `1` | Every place beyond the end of `placePoints` |
| `dnfPoints` | `1` | `Dnf`, and `Nm` (competed without a valid mark) |
| `dnsPoints` | `0` | `Dns` |
| `dqPoints` | `0` | `Dq` |
| `socialPerEntryPoints` | `1` | Each entry in a Social-scored event (below) |

- Points are awards, never penalties: every value must be zero or more. A write with a negative value is refused (`400 SCORING_RULES_INVALID`); a stored blob that fails the same check is ignored and the defaults apply.
- Each save stamps a new rule version (`custom-<timestamp>`), shown under the standings and recorded on audit events.
- Rules cannot change once the carnival is finalised.

### Event weight

An event definition's `scoringWeight` multiplies its place points and its DNF/DNS/DQ points. Relay templates set it to 2, so a relay 1st scores 8 under the defaults. The weight belongs to the definition, not the carnival, and is frozen by template reconciliation once a finalised carnival has used the definition.

### Where the points land

Each house's total has three visible components:

| Component | Contains |
|---|---|
| **Placement** | Place points, including the participation floor for places beyond the table |
| **Participation** | DNF/NM/DNS/DQ points, and Social per-entry points |
| **Bonus** | The sum of the carnival's non-voided adjustments (may be negative) |

## Competitive, social and non-scoring events

Each division of an event (`CarnivalEvent`) has a **tier** (Competitive or Social), an optional **scoring mode** override (Places or Social; null follows the tier), and a **scores points** switch.

| Setting | Effect on house points |
|---|---|
| Places mode (the Competitive default) | Ranked; place points as above |
| Social mode (the Social default) | Not ranked. Every entry earns `socialPerEntryPoints` for its house unless it has a `Dns` result. Heats and the event weight do not apply. No result is needed |
| `scoresPoints = false` | Contributes nothing to any house total. The event is still placed on the deck and in the results book, and still owes results before finalisation |

A Social-mode event counts as complete on the dashboard once it has entries, and does not block finalisation for missing results. Social `0` points turns Social events into pure participation with no house effect. Scoring mode and the scores-points switch can change at any time before finalisation; the tier is locked once the division has entries.

## Relays and team events

A team entry represents one house. Its single result is placed like any other, and the points go to the team's house, multiplied by the event weight. No points go to individual swimmers.

## Bonus and novelty adjustments

Novelty races, best dressed, loudest house and similar awards are **house-points adjustments**, kept structurally apart from results: an adjustment names a carnival and a house, never a student, entry or result, so it can never reach a student's history or the record book. Why: [ADR-0005](../adr/0005-novelty-and-bonus-house-points.md).

- **Fields:** house, points (non-zero; negative is a deduction), category, required reason, the capturing actor and role, timestamp.
- **Categories** are per-carnival configuration (defaults: Novelty 1–3, Best Dressed House, Loudest House). An unknown category is refused. A schedule slot in the template can name the category its novelty item awards.
- **Capture** goes through the device outbox with an idempotency key, like a result; the recorded actor is whoever captured it, even if it drains after a device handover.
- **Correction** is by voiding (with a reason) and recording again; nothing is edited in place. A voided adjustment stays listed but stops counting.
- **Authorisation:** carnival administrators award and void; anyone who can view the carnival can list them.
- **Finalisation** freezes them: no award or void once the carnival is finalised.

Every award and void writes an audit event.

## Standings

`GET /api/v1/carnivals/{id}/standings` returns, per house: total, the three components and a rank, plus the rule version, a `provisional` flag (true until the carnival is finalised), counts of events and results counted, the weighted definitions (`weightedEvents`: name and weight, for every places-scored definition whose weight is not 1) and whether any Social-scored event counts (`hasSocialEvents`).

- Until the carnival is finalised, every configured house is listed from the start, at 0 until it scores, so the leaderboard and the participation card always show the same houses ([ADR-0063](../adr/0063-standings-list-every-house.md)). A house removed from the school configuration still appears while the carnival has scoring rows for it.
- A **finalised** carnival is history: it lists the houses that took part (an individual or relay team entry, a relay leg swum for the house, or a points adjustment) plus any house with scoring rows. A house configured after the carnival does not appear. The participation card uses the same list.
- Houses are ordered by total, then house code. **Rank** uses standard competition ranking on the total: equal totals share a rank (shown as `=1`, including every house level at 0) and the next rank skips. The platform states a tie; it does not break one.
- `eventsCounted` counts events, not heats. A Social event counts once it has an entry that is not a no-show.

The dashboard and the Reports screen render the same response with `StandingsTable`, which prints the components under each house and a one-line legend of the rules: place points, the floor, DNF/DNS/DQ points, each weighted definition with its multiplier ("4×50m Freestyle Relay ×2"), and the Social per-entry points when a Social event counts. Standings are not polled: when the device's outbox drains, cached reads are marked stale without refetching, and the dashboard re-reads standings when it next mounts, on window refocus and on reconnect.

## Worked example

Default rules. Four synthetic houses, three events.

**50m Freestyle, 13 Boys, Competitive (Time), two heats**

| Heat | Result | Place | Points |
|---|---|---|---|
| 1 | Banksia 31.20 | 1st | 4 placement |
| 1 | Grevillea 32.05 | =2nd | 3 placement |
| 1 | Wattle 32.05 | =2nd | 3 placement |
| 1 | Mountain Ash DNF | — | 1 participation |
| 2 | Mountain Ash 30.90 | 1st | 4 placement |
| 2 | Banksia 33.40 | 2nd | 3 placement |
| 2 | Grevillea DQ | — | 0 |

Mountain Ash's 30.90 is the fastest swim of the event but earns the same 4 points as Banksia's slower heat win. On the event ladder (records, district list) it ranks 1st across both heats.

**4×50m Freestyle Relay, 13 Girls (weight 2):** Wattle 1st (4 × 2 = 8), Banksia 2nd (6), Grevillea 3rd (4), Mountain Ash 4th (floor 1 × 2 = 2), all placement.

**50m Freestyle, 13 Girls, Social:** Banksia has two entries, Wattle one, Grevillea one marked DNS. Banksia +2, Wattle +1, Grevillea 0, all participation.

**Adjustments:** Novelty 1, Grevillea +5; Loudest House, Wattle +3.

| Rank | House | Placement | Participation | Bonus | Total |
|---|---|---|---|---|---|
| =1 | Banksia | 13 | 2 | 0 | 15 |
| =1 | Wattle | 11 | 1 | 3 | 15 |
| 3 | Grevillea | 7 | 0 | 5 | 12 |
| 4 | Mountain Ash | 6 | 1 | 0 | 7 |

## Dashboard progress

`GET /api/v1/carnivals/{id}/progress` is about recording, not points: every event counts whether or not it scores, and a DNS, DNF or DQ counts as recorded. See [ADR-0043](../adr/0043-dashboard-progress-and-meet-day-stats.md).

- **Per event:** expected (entries) and recorded (entries with a result), and a status. `notStarted` when nobody has entered or nothing is recorded; `complete` when every entry has a result, or as soon as a Social-mode event has entries; otherwise `inProgress`. Events are ordered by the advisory schedule (stream, slot, order in slot), unscheduled events last.
- **Totals:** events complete, in progress and not started; results expected and recorded.
- **Participation per house:** the houses the standings list plus any house holding entries and, until the carnival is finalised, any house still on the roster, each with competitors (active students on the current roster), students entered in this carnival (individual entries and relay legs, each student once) and how many of those have a result. Counts only; no names. The card shows entered capped at competitors, for a student who entered and then left the roster.
- **Records today:** the event ladder's best in each record-eligible division compared with the standing record, marked `new` or `equalled`. Nothing is written; records are minted only at finalisation. Once the carnival is finalised, the records it minted are read back as `new` (beside the record each superseded), and `equalled` still compares against the book. See [Records](records.md).

On the device, queued captures and entries in the outbox are overlaid on the server's counts, so offline work shows as progress where it was captured.

## In the code

- `api/src/CarnivalApp.Api/Scoring/ScoringService.cs`: standings; `StandingHouses.cs`: which houses are listed
- `api/src/CarnivalApp.Api/Scoring/Placement.cs`, `HeatPlacings.cs`, `EventLadder.cs`: ranking
- `api/src/CarnivalApp.Api/Scoring/CarnivalSettings.cs`: rules, defaults, validation
- `api/src/CarnivalApp.Api/Scoring/AdjustmentEndpoints.cs`: settings and adjustments endpoints
- `api/src/CarnivalApp.Api/Scoring/ProgressService.cs`: dashboard aggregate
- `api/src/CarnivalApp.Api/Carnivals/AttemptSeries.cs`, `JumpCard.cs`: countback keys
- `app/src/features/standings/`, `app/src/features/dashboard/`, `app/src/screens/DashboardScreen.tsx`
- Tests: `ScoringServiceTests`, `HeatScoringTests`, `SocialScoringTests`, `AdjustmentEndpointsTests`, `EventLadderTests`, `ProgressEndpointsTests` in `api/tests/CarnivalApp.Api.Tests/`
