# ADR-0056: High jump — the heights ladder, captured as the card grid

- **Status:** Accepted (2026-09)

## Context

[ADR-0053](0053-field-attempts.md) modelled field attempts for the horizontal events and left high jump as a single typed best height, because its rules have a different shape: attempts are taken **per height**, not per trial; the bar rises and never comes down; consecutive failures eliminate; and ties on the best height are broken by **fewest failures at that height, then fewest failures up to and including it** (World Athletics TR 26.8). Ties on the best height are the normal case, since heights rise in fixed steps, so a single-mark model would print shared places where the real rule separates them, and house points would follow the platform rather than the official.

Typing countback numbers off a paper card would give correct placings but keep the card on paper. The platform should capture the card itself.

## Decision

1. **A Height event is always contested on a bar.** The definition's measure is chosen as an event type: **Race (timed)** = Time, **Field, best of attempts (distance)** = Distance, **Field, bar ladder (vertical jump)** = Height. The type is the capture mode, so it is frozen once the definition has results (`MEASURE_FROZEN`). An attempt count belongs to Distance and Count definitions; a Height definition carries none, and may carry a suggested opening height and rise (`LadderJson`), which only pre-fill the deck and never gate a rule.
2. **The model: a ladder per division, a card per athlete, everything else derived.**
   - The ladder (`CarnivalEvent.HeightsJson`) is the bar heights contested so far: whole centimetres, ascending, append-only, starting empty. Raising it lower than the last height is refused (`BAR_NEVER_LOWERED`).
   - The card (`Result.AttemptsJson`) is one entry per height attempted, each a string of marks: `O` cleared, `X` failed, `-` passed. `O` or `-` ends a height.
   - The server derives everything else on every write: best height, failures at best, countable failures, elimination and status (`Ok` once a height is cleared, otherwise `Nm`). `MeasuredValue` is the best height, so records and exports read it unchanged. DNS, DNF and DQ remain typed statuses.
3. **Attempts per height is 3 or 2.** The definition holds a default (absent reads as 3), stamped onto each division when it is created. The division may switch until its first mark lands, then it is fixed (`ATTEMPTS_PER_HEIGHT_FROZEN`). At 3, three consecutive failures across the card eliminate, a pass not breaking the run; at 2, failing both attempts at one height eliminates.
4. **One write per mark.** `result.jump` is an outbox kind: `PUT …/entries/{entryId}/jumps/{height}`, idempotent, verified at capture ([ADR-0017](0017-automatic-result-validation.md)), one revision per mark. The server appends the mark to that height. Refusals are conflicts, so a queued mark parks: `ATHLETE_ELIMINATED`, `HEIGHT_NOT_ON_LADDER`, `BAR_NEVER_LOWERED` (below a height the athlete has already attempted), `HEIGHT_ALREADY_CLEARED`, `HEIGHT_PASSED`, `HEIGHT_ATTEMPTS_EXHAUSTED`. A typed value, an attempt series or a jump on the wrong kind of event draws `LADDER_REQUIRED`.
5. **Raising the bar and setting attempts per height** are online, audited, versioned writes under the capture policy (the people at the mat raise the bar), allowed while the carnival is Open and the division Scheduled or Open.
6. **A correction restates the whole card**, with a reason and the expected revision, and is replayed mark by mark through the same rules as capture, so a correction cannot build a card capture would refuse. An empty card is accepted: the result keeps its history and reads as not yet recorded.
7. **Countback.** The ranking key is `[best height, −failures at best, −failures up to and including the best]`, higher is better. Failures above the last cleared height do not count. Equal keys share the place; jump-offs are not modelled.
8. **The deck is the card grid.** Athletes down, heights across, each height a group of one box per attempt; the current bar is tinted and the grid opens there, with the athlete column sticky. Tapping the next box, or a marked box, expands a panel in place under the row (no dialog at any width) offering O / X / pass; on a marked box it replaces or removes that mark, names any later marks the edit invalidates and asks to confirm before dropping them. The raise and attempts-per-height controls sit in the card header. The results book prints the same grid with Best, Fails at best and the countable total, and the countback rule as a footnote.

The behaviour as built is described in [result lifecycle § the high-jump heights ladder](../architecture/result-lifecycle.md#the-high-jump-heights-ladder).

## Consequences

- High-jump ties resolve as the rules say, and the printed card is the evidence for every place.
- One capture surface serves phone, tablet and paper; on a phone the per-attempt boxes make the grid scroll sideways sooner.
- Marks are positional (the server appends), so while one of an athlete's marks is parked, that athlete's later marks and card corrections are held in the outbox; other athletes keep draining.
- A Height event holding pre-ladder typed results still reads them in the results book, but its deck is the ladder.
- Not modelled: jump-offs, wind, two bars for one division, a per-athlete opening height (an athlete simply takes no mark at lower heights).

## Rejected alternatives

- **Bar-first capture** (one row per athlete still in, three targets for the current height): better one-handed at the mat, but the card grid gives the whole-division overview and matches the printed series, so one layout serves capture, screen and paper.
- **Countback numbers typed off the paper card**: correct placings, but the platform becomes a transcription of paper.
- **Reusing `result.attempt` with an ordinal per mark**: the ordinal is meaningless across heights, and the server still needs the height.
- **Ladder mode inferred from whether a suggested start is set**: the mode must be an explicit choice of event type, not a side effect of an optional field.
