# ADR-0041: The deck in direction A — seconds-first time entry, one row-actions menu, the lane block

- **Status:** Accepted (2026-08). Builds on [ADR-0040](0040-design-system.md); the deck's data flow (ADR-0018, ADR-0036, ADR-0038, ADR-0039) is unchanged by it.

## Context

The capture deck is the screen officials use most, usually on a wet iPad at the pool edge. Three direction previews were drawn on the new design system; the "quiet utility" direction was chosen. Two problems needed settling with it: the stopwatch-style fill-from-the-right time mask surprised officials, and three always-visible status buttons (DNS, DNF, DQ) plus lane and remove controls cost width on every row for actions that are rare.

## Decision

1. **Time entry: digits read positionally, hundredths default to `.00`.** Digits before a dot read as on a stopwatch face: the last two are seconds, anything before them minutes (`35` → 35.00, `123` → 1:23.00, `1234` → 12:34.00). Hundredths are `.00` unless the official types `.` and enters them (`123.23` → 1:23.23, `9.5` → 9.50). `:` is still accepted. Seconds of 60 or more are refused, never carried. The field echoes the reading as it is typed and the caption teaches the rule. iPads have no `:` on the decimal keypad, which the positional reading makes unnecessary. The server re-parses every value and is authoritative.
2. **One row-actions menu.** DNS, DNF and DQ, **Change lane** and **Remove from heat** sit behind one `⋮` button per row, at every width. The status items appear only where the capture form is on screen; a saved row's menu holds lane and remove, and changing a recorded result starts at Edit. The menu is `@base-ui/react` Menu (headless, styled by our CSS), the one Base UI primitive adopted here. The kebab is icon-only, a named exception to the icon rule, with an accessible name carrying the swimmer ("Actions for …").
3. **The row leads with the lane as a numeral block**, then name and house/year as text, then the time in large tabular numerals. A saved row shows its time, the winner badge (ADR-0036) and Edit.
4. **Chips say their state:** `n/m` counts and a tick when complete; the deck header carries "n of m recorded" with a thin progress bar.
5. **Save stays a per-row primary button**, disabled until the field parses. All targets are at least 44px.

## Consequences

- DNS, DNF and DQ move from one tap to two. Accepted: they are the rare path, and the reclaimed width buys a larger time field.
- Officials used to the fill-from-the-right mask retype; the echo and caption teach the rule at the point of use.
- First dependency on `@base-ui/react` (MIT, tree-shaken to what is used).
- The deck header was later condensed by [ADR-0042](0042-condensed-deck-header.md); non-Time events use a mark mask on the same pattern ([ADR-0052](0052-athletics-measures-field-capture-and-record-eligibility.md)).

## Rejected alternatives

- **Direction B (dense grid):** laptop-first with smaller targets; the deck's primary device is a wet iPad.
- **Direction C (one swimmer at a time):** biggest targets, but loses the heat overview and multiplies taps.
- **Seconds-only input up to three digits** (`210` = 3:30): reads `123.23` as 2:03.23 when officials mean 1:23.23.
- **Keeping the three status buttons on the row:** width on every row for rare actions.
- **A custom dropdown for the menu:** menu keyboard and assistive-technology semantics are what a headless primitive is for.
