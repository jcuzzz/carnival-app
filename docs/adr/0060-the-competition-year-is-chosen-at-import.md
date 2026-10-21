# ADR-0060: The competition year is chosen at roster import

- **Status:** Accepted (2026-09). Amends [ADR-0054](0054-setting-up-a-carnival.md) §1: the setup checklist's Roster step gains a roster-year warning.

## Context

A student's competition age group is their age as at 31 December of the competition year (the School Sport Victoria convention). The roster import derives it from the birth date and then discards the date; no full birth date is stored. The cut-off was a full date in the import-mapping config (`2026-12-31`), whose year also stamped every snapshot's `SchoolYear`.

That year was static. A roster imported in November for a February carnival, or any roster imported after New Year against an unedited config, placed every student one year young, silently. Rolling it forward meant a hand edit of a server file every year: an unowned step whose failure is wrong for every student.

The roster is one current snapshot shared by every carnival, not a per-carnival copy, so the year belongs to the import, not to a carnival.

## Decision

1. **The config holds the cut-off day, not the year.** `ageGroupRule.cutOff` is `MM-DD` (`12-31`). A legacy `cutOffDate` is still read for its month and day only.
2. **Each import names its competition year.** The upload takes an optional `competitionYear`. When absent, the server uses the year of the earliest carnival dated today or later (looking no further than next year), else the current year, with "today" in the school's timezone. A year more than one from the current year is refused (`IMPORT_COMPETITION_YEAR_INVALID`) and nothing is staged.
3. **The year is visible before anything changes.** The upload form pre-fills the default from `GET /imports/defaults` and lets the organiser change it; the form and the review screen state "Ages as at 31 December YYYY".
4. **A mismatch is loud where carnivals are set up.** The carnival settings read returns `rosterYear` (the year most current active snapshots carry). When the carnival's date falls in another year, the checklist's Roster step warns, naming both years and pointing at the roster import. It never blocks: a school may deliberately run on last year's roster.
5. **Birth dates stay unstored.** Re-importing the roster is how ages move; schools re-import each year for enrolments anyway.

How the import applies it: [roster import](../architecture/imports.md).

## Consequences

- The shipped config no longer goes stale each January, and no deployment needs a yearly edit.
- A roster imported for the wrong year is caught at import (the stated year) and again at setup (the warning).
- "Most" snapshots, not "all": a student missing from the new file keeps last year's snapshot.
- Saved division profiles are not rewritten when a re-import changes age codes ([ADR-0057](0057-age-bands-from-the-roster.md)); the Divisions screen flags saved ages the roster no longer derives.
- The development seeder uses a fixed synthetic year so seeded data and screenshots stay reproducible.

## Rejected alternatives

- **Store the birth date and derive the age per carnival**: makes a full birth date stored personal data for no accuracy gain over (2)–(4).
- **A school setting for the year**: still a yearly hand edit, only moved into the app.
- **Derive the year from a carnival chosen at import**: the roster is shared across carnivals; the year, not the carnival, is the true input.
