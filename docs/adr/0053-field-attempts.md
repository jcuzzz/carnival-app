# ADR-0053: Field attempts — a configurable series per athlete, the best mark derived

- **Status:** Accepted (2026-09). Supersedes [ADR-0052](0052-athletics-measures-field-capture-and-record-eligibility.md) §5 (one typed best mark). High jump, left here as a single typed height, is superseded by [ADR-0056](0056-high-jump-heights-ladder.md) (the heights ladder, which reuses this ADR's capture, correction and countback shape).

## Context

ADR-0052 captured one best mark per field athlete and left the trial-by-trial series on the paper card, so two equal best marks always tied. For most field events the real difference from swimming is the attempts logic: usually three attempts, sometimes two, configurable, with ties broken by countback on the next-best mark. An event manager must also be able to make any type of event by hand, and there must be no sport-specific race logic: one code path for both sports.

The existing model: one `Result` per entry with a single `MeasuredValue`, verified at capture (ADR-0017), corrected with a reason through an append-only revision log, keyed by a client UUIDv7 that doubles as the idempotency key; one ranking function, `Placement.Rank`.

## Decision

1. **The attempt count is a property of the event definition.** `EventDefinition.AttemptCount`, 1–6; null means a single typed measure (every `Time` event, and any field event captured as one mark). It is refused on `Time`, `Height` and team definitions, in the editor and the template loader. The New event and Event editor forms offer it for `Distance` (defaulting to 3); the API also accepts it on `Count`. The shipped athletics template sets `attempts: 3` on the horizontal jumps and throws. It is **frozen once the definition has results**, in reconcile (ADR-0050) and on the editor path (`ATTEMPT_COUNT_FROZEN`), because a series recorded against three attempts cannot be re-read against two. `attemptCount` is on `EventDefinitionDto` and `CarnivalEventDto`.
2. **The result holds the series; the server derives the rest.** `Result.AttemptsJson` is an ordered array, one element per ordinal: a mark, a foul, or `null` for an ordinal not yet taken. The server derives `MeasuredValue` (best valid attempt) and `Status` (`Ok` if any attempt is valid, `NM` if every taken attempt is a foul) on every write. A typed best mark or `NM` is refused on an attempts event (`ATTEMPTS_REQUIRED`). `DNS`, `DNF` and `DQ` remain typed statuses; typing one clears the series, and an attempt on such a result is refused (`RESULT_STATUS_BLOCKS_ATTEMPTS`). The series is part of every revision and audit row, and `ResultDto.attempts` carries it.
3. **One write per attempt: a new outbox kind, `result.attempt`.** `PUT /carnivals/{id}/events/{eventId}/entries/{entryId}/attempts/{ordinal}` with the raw typed value or a foul, under the idempotency filter. The first attempt creates the result (201, its id the capture's key); later ordinals append (200) with a revision reason `Attempt {n}`. Ordinals may arrive in any order from several devices. Refusals (`ATTEMPTS_NOT_CONFIGURED`, `ATTEMPT_ORDINAL_OUT_OF_RANGE`, `ATTEMPT_ALREADY_RECORDED`, `RESULT_STATUS_BLOCKS_ATTEMPTS`) are all 409, so any client build parks them for a person rather than discarding them. One result per entry is a unique index; a lost race between two officials re-reads and re-applies once.
4. **Correcting an attempt is a correction.** `result.correct` carries the **whole series**, the reason and `expectedRevision`; the server re-derives. A correction that would drop a recorded ordinal (`ATTEMPTS_SERIES_TRUNCATED`) or empty the series is refused; `DNS`, `DNF` or `DQ` is how a series is cleared.
5. **Countback.** `Placement.Rank` compares a key: the valid marks sorted best first, compared position by position; where one key runs out, the one with a further valid mark is better. Equal throughout is a tie. A single-measure result's key is its one value, so every existing ranking is unchanged. Records and district selection read the best mark only.
6. **A non-`Time` event places as one field.** Heats are hidden on the deck for every non-Time event, and placings, scoring and the results book ignore the heat for them too, so a field division split across flights pays one set of placings. The heat is still stored on the entry. Nothing about races forks by sport.
7. **The deck on an attempts event** shows a strip of attempt cells under each entry row: an empty cell takes a mark (the ADR-0052 mask) or a **Foul**; a taken cell shows its mark or X; the row shows the derived best mark or `NM`. Each cell saves on its own, with its own sync badge; a cell's Edit opens the correction sheet for that ordinal (which can also mark it a foul). The row menu keeps DNS, DNF and DQ, which go through a correction where a result exists.

How attempts, countback and corrections work: [result lifecycle § field attempts](../architecture/result-lifecycle.md#field-attempts).

## Consequences

- A tie on a field event resolves by countback where the series differs: a visible change to athletics standings, none to swimming.
- Swimming rows and rankings are unchanged.
- The results book prints the best mark but not the horizontal-event series beside it; the series is available on `ResultDto.attempts`.
- A "bonus final" for the top N, per-attempt timestamps and wind are not built.

## Rejected alternatives

- **Sending the whole series through `result.submit`/`result.correct` only:** round-by-round capture is N writes per athlete, and the second would hit `RESULT_EXISTS` or need a reason it does not have.
- **A `ResultAttempt` table:** the series is read and audited as a unit and never queried by attempt; a JSON column follows the repo's convention and needs no join.
- **Client-derived best mark:** the server owns every derived value.
- **Not materialising heats for field events:** would fork entry placement by sport.
- **Breaking ties on fewest fouls:** not the rule for horizontal events (it is high jump's).
