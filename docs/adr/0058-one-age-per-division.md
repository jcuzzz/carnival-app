# ADR-0058: One age per division — the merged age band is removed

- **Status:** Accepted (2026-09). Supersedes the age-axis-as-a-set parts of [ADR-0014](0014-result-entry-navigation-and-schedule-model.md) §4, [ADR-0022](0022-event-and-division-management.md) §2 and §9, [ADR-0027](0027-the-division-profile-gets-its-own-table.md) (row and wire shape), [ADR-0028](0028-records-and-district-selection.md) (merged-band record categories), [ADR-0052](0052-athletics-measures-field-capture-and-record-eligibility.md) §7 (slot `ages` filters), and [ADR-0057](0057-age-bands-from-the-roster.md) §3 (the band editor).

## Context

A division's age axis was a **set** of age codes so a school could merge ages (a "16+" division). The set needed a canonical key (`16-17-18`) to index and a free-text label because the key is unreadable, and every layer inherited all three forms: storage, the unique indexes, the record book, template slot filters, an app-side canonicaliser, and a band editor with chips, derived labels and overlap notes.

The merge had not earned its place:

- A printed program's "16+" is a session label; 16, 17 and 18 are separate events with their own placings and house points. Both shipped templates declare single ages only.
- Ages that run together in the pool or on the field are one schedule slot holding several divisions, which keeps each age's placings.
- A senior cohort too small to split is merged **at roster import** by the mapping's `openFromAge` rule, which derives one code (`SNR`) for everyone at or above that age; every consumer then sees an ordinary age.
- A relaxed all-ages Social division is the `OPEN` age, which stays.

## Decision

1. **A division's age is one code, or `OPEN`.** `CarnivalEvent.AgeGroup` is a single code the roster derives (`13`, `SNR`) or the reserved `OPEN` meaning every age. `CarnivalDivision.AgeGroup` (the profile row) is one age, never `OPEN`. A record category is (definition, age, category). Unique indexes are (carnival, definition, age, category, tier) for divisions and (carnival, age, category, tier) for profile rows.
2. **The age rule is small.** `AgeScope` keeps three operations: normalise (trim, upper-case; blank reads as `OPEN`), validate (letters and digits, at most 16 characters, or `OPEN`; refused with `AGE_SCOPE_INVALID`) and order (`OPEN` first, then numeric by value, then others). A division is named by its code, or "All ages" for `OPEN`.
3. **Eligibility is equality**: the division is `OPEN` or its age equals the student's `CompetitionAgeGroup`.
4. **Wire and templates carry one string.** Events, schedule and generate requests, records and the results book carry `ageGroup`; the division profile carries `ageGroups: string[]` (with `categories` and `tiers`). Templates declare `divisions.ageGroups` (no `OPEN`, no hyphenated code, no duplicates); a definition's `openDivision` still yields an `OPEN` division.
5. **The Divisions screen** is a *From* and *To* pair over the numeric ages (the roster's, or 5–21 with no roster) and one checkbox per non-numeric roster code, plus the category and tier rows. Notes appear only when true: roster ages not contested, saved ages the roster no longer derives, and a saved profile the range cannot express. Skipping one age for one event is the event grid's untick. No labels, band rows or chips.
6. **Migration `OneAgePerDivision`** refuses to run while any division, profile row or record holds a hyphenated (merged) key; an operator removes those first. A merged row cannot be carried across.

## Consequences

- A division's identity is (definition, age, category, tier) everywhere, and the app reads one string.
- **Lost, deliberately:** a per-carnival "15 and 16 in one Social event". It runs as two Social divisions in one slot, or as one all-ages Social division.
- Records have one category per age, always.
- A school whose tradition is a "17+" Competitive division with placings across three ages must set `openFromAge` for the whole roster.
- A contract change on every event read and on the settings and template documents; API, app and templates ship together.

## Rejected alternatives

- **Keep the set model and hide it in the UI**: keeps the three-form storage, the canonicaliser and its tests for a capability nobody can reach.
- **Represent open as `NULL`**: a nullable column in the unique index lets two open divisions of one definition coexist; the sentinel stays.
- **A per-carnival "16+" pseudo-age**: the set model under another name; the senior merge is a school rule, made once at import.
- **A free-form label on a single-age division** ("Under 13"): configuration the school does not need; the code is the label.
