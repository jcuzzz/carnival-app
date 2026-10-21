# ADR-0052: Athletics — field measures, mark capture and record eligibility

- **Status:** Accepted (2026-09). Partly superseded: the original single best-mark capture (one result per athlete, the series kept on paper) by [ADR-0053](0053-field-attempts.md) (attempt series and countback); typed `Height` capture by [ADR-0056](0056-high-jump-heights-ladder.md) (high jump is always contested on a heights ladder); the slot filter's age-band labels and merged-band record categories by [ADR-0058](0058-one-age-per-division.md) (one age per division). The text below states the decision as it stands after those changes.

## Context

Athletics is the second sport. Most of the platform was already sport-neutral: four measure types (`Time`, `Distance`, `Height`, `Count`) with one sort-direction rule used by every ranking path, a decimal stored value in the event's canonical unit, results attached to entries rather than lanes, the heat as a generic grouping (a field flight is a heat), concurrent schedule streams and `facet:value` tags.

What was still time-bound: the deck could not see an event's measure, so capture and display assumed a time; the server printed non-Time values as bare decimals with no unit; there was no status for an athlete who fouled every attempt; a template slot could not name which divisions of a definition it held, so a field station's running order collapsed into one slot; there was no athletics template; and event setup listed the whole catalogue with no sport dimension.

## Decision

1. **Each measure kind has a canonical unit, precision and format.**

   | Kind | Stored | Capture | Display | Better |
   |---|---|---|---|---|
   | `Time` | seconds | hundredths | `m:ss.ff` / `ss.ff` | lower |
   | `Distance` | metres | centimetres, up to three whole-metre digits | `10.52 m` | higher |
   | `Height` | metres | on the heights ladder (ADR-0056) | `1.35 m` | higher |
   | `Count` | count | whole number, up to six digits | `12` | higher |

   The digit caps are syntax, not plausibility bands (ADR-0017 still defers those). One server formatter (`EventLadder.FormatValue`) serves the results book, records, district CSV and dashboard, and the client mirrors it; the unit is the canonical one, never the definition's free-text `Units`, which stays a catalogue label.
2. **The event read carries the measure.** `CarnivalEventDto.measureType` is required; the deck keys its capture mask, formatting, status vocabulary and lane display on it.
3. **Mark capture on the deck.** A `Distance` field is a mark input on the same positional idea as the time mask, in the unit officials call: **digits are centimetres** (`432` reads 4.32 m, `1052` reads 10.52 m), and a typed `.` fixes the decimal point instead. `Count` is a plain whole-number field. Letters switch the field to status mode. A non-Time event shows **no heats and no lanes** on the deck: no heat picker, no "heat N", no lane block or lane picker, and "Remove from heat" reads "Remove entry". The server still places a field entry in a heat with no lane (ADR-0039). The wire is unchanged: captures carry the raw typed string and the server parses it.
4. **`NM` ("no mark") is the fifth result status:** attempted, nothing valid. Refused on `Time` events (`NM_REQUIRES_FIELD_MEASURE`), where a runner who starts and does not finish is a DNF. It carries no value, never ranks, is never a record candidate, counts as resulted for finalisation, and **scores as DNF**. On attempts and ladder events it is derived, not typed.
5. **Record eligibility** follows the standing rules (ADR-0028): Competitive divisions only, `M` or `F` category, a single age the definition lists; `DNS`, `DNF`, `DQ` and `NM` are never candidates; equalling does not supersede. A record's identity is (definition, age, category), and the **implement specification is a property of the definition**: where a school contests two implement masses within one age and category, it splits the definition (for example `AT-SHOT-3KG` / `AT-SHOT-4KG`) before the first result, since measure and units freeze once results exist. Wind is not recorded and does not gate records.
6. **A template slot can hold a subset of a definition's divisions.** A slot's `events` entry is a definition code (every division) or `{ "code", "ages"?, "tiers"?, "categories"? }`, validated against the template's own declared values. A definition may therefore appear in several slots of one stream, preserving a field station's running order. A division no slot claims stays unscheduled, which is a usable state.
7. **`config/event-templates/athletics.json`** ships as data: single ages 12–18, categories `M`/`F`, both tiers; track events (`Time`), a 4×100m house relay (`scoringWeight` 2, Competitive), horizontal jumps and throws (`Distance`, with attempts per ADR-0053) and high jump (`Height`); `discipline`, `distance` and `surface` tags; a Track stream plus one stream per field station. Novelty races are house-point adjustment slots (ADR-0005), not definitions. No hurdles.
8. **Event setup gains a sport dimension.** Definitions whose sport does not match the carnival's type fold behind "Show N from other sports"; a definition the carnival already contests is never folded. The catalogue stays one list.

How measures, statuses and placings work: [result lifecycle](../architecture/result-lifecycle.md#measures); records: [records](../architecture/records.md).

## Consequences

- One code path for both sports: nothing forks by sport; the deck reads the measure.
- Applying both templates gives one catalogue with two sports.
- Seven streams exceed landscape A4's six columns; the program wraps streams onto extra rows.
- Age champions, hurdles, wind, heats-to-final rounds and relabelling heats as "flights" are not built.

## Rejected alternatives

- **A sport axis on the definition driving capture:** a second vocabulary for what the measure already answers.
- **Metres-positional digits with an opt-in fraction:** every field mark has a fraction, so the dot would be typed every time.
- **An `NmPoints` scoring rule:** "competed, no valid mark" is what `dnfPoints` already prices.
- **A whole-window slot per station:** loses the school's running order and the printed program's session lines.
- **Formatting on the client only, or a `formattedValue` on every result:** the server already formats for every report; a wire field would duplicate the value.
