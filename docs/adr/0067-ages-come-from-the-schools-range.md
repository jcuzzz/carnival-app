# ADR-0067: Ages come from the school's range, and the oldest age takes everyone older

- **Status:** Accepted (2026-11). Partly supersedes:
  - [ADR-0022](0022-event-and-division-management.md) §2: the profile is seeded from the school's age range, not the template's.
  - [ADR-0027](0027-the-division-profile-gets-its-own-table.md) §3: what a template's seed holds.
  - [ADR-0054](0054-setting-up-a-carnival.md) §1 and §3: the Divisions step, and what blocks adding events.
  - [ADR-0057](0057-age-bands-from-the-roster.md) §2: the roster default is gone, and so is the Divisions step it pre-filled.
  - [ADR-0058](0058-one-age-per-division.md) §1, §3 and §5: an age code may be `N+`, eligibility is not pure equality for it, and the Divisions screen is a range.
  - [ADR-0059](0059-adding-a-catalogue-event-is-one-press.md) §2: the no-roster notice.
  - The import mapping's optional senior band (`openFromAge`, `openLabel`).

  [ADR-0022](0022-event-and-division-management.md) §8 and [ADR-0057](0057-age-bands-from-the-roster.md) §4 stand.

## Context

A carnival's divisions are its ages × categories × tiers. Ages used to be a setup step of their own that organisers did not understand, then were read from the student list, so no event could be added until students were imported. The maintainer wants it simple: a school runs the same ages every year (12 to 18 and over in a secondary school, 5 to 12 in a primary school), events are made over those ages, and the student list only says who can enter them. A student whose age no division covers should be pointed out, not prevent anything.

The oldest students also need a home. Students past the oldest age (a 19-year-old in Year 12) could enter nothing, and the only remedy was a senior band configured in the import mapping, out of sight of the organiser.

## Decision

1. **The school sets its age range** in School settings: a youngest and an oldest age, saved with the name and houses. A new install has none, and the form offers 12 to 18. The school row of "Set up your school" ([ADR-0066](0066-the-setup-flow.md) §2) names ages with the houses.
2. **The oldest age takes everyone older.** Its code is `N+` (shown "18+"), and a student is eligible for an `N+` division when their derived age is a number of at least N. Every other division still matches its age exactly. Students keep their plain derived age, and the import mapping's senior band is removed: a mapping that sets it is refused at startup.
3. **A carnival's ages start as the school's range.** Until the carnival saves its own, its ages are the school's range (`12` … `17`, `18+`), categories Boys and Girls, tier Competitive. The profile is saved the first time divisions are generated, as before ([ADR-0027](0027-the-division-profile-gets-its-own-table.md)). Event setup shows the ages as one line ("12 to 18+") with Change, which edits this carnival's From and To; the top age is always `N+`. Lowering From adds the new ages to the events as **Add age** does (§5).
4. **Events need no student list.** Add, New event and the standard events work as soon as the carnival has ages (its own, else the school's). A template generates each event's divisions over the carnival's ages, with the template's categories and tiers when the carnival has saved none (they then become its saved ones); a template's age filter names plain ages, and a filter age at or above the carnival's oldest selects the `N+` division. With no ages anywhere, adding events points at School settings (`409 AGES_REQUIRED` from the template apply). `ROSTER_REQUIRED` is removed.
5. **Students no division covers are a warning, never a block.** Students younger than the carnival's youngest age are counted per age: "14 students are aged 11, and this carnival has no events for age 11." The warning appears on Event setup, on the import review launched from the carnival, and in Ready to open. Its one action, **Add age 11**, lowers the carnival's youngest age and adds the new ages' divisions to every event that has a division for the carnival's youngest age, with that division's scoring and place in the program, in one audited write; events kept to older ages are left alone. It changes only this carnival, not the school's range.
6. **Ages with no students are a quiet note** in Ready to open ("Age 18+ has no students"). The "Remove those ages" notice is gone.
7. **Opening still needs a student list and at least one event** ([ADR-0066](0066-the-setup-flow.md) §5).
8. **Existing data is not migrated.** A carnival keeps its saved profile, including a plain oldest age, and records keep their age codes.

How divisions are modelled: [data model § division profile](../architecture/data-model.md#division-profile).

## Consequences

- Setting up a carnival no longer waits for a file from the student system, and a school sets its ages once.
- No student is left without an age division by being too old; students too young are named, with one press to include them.
- A division can exist with nobody to enter it. That is visible in Ready to open and costs nothing on the day.
- Records for the oldest age are kept under its `N+` code, a separate category from a plain `18` saved before this change. A definition's record ages cover an `N+` division when any of them is N or older, and a record age `N+` covers every division from N up.
- A primary school sets 5 to 12 itself; a per-school-type default is left for later.

## Rejected alternatives

- **Ages read from the student list** (this ADR's first version): events could not be set up until students were imported, and the ages moved with each import.
- **A senior band in the import mapping:** hidden from the organiser, decided at import time, and a code (`SNR`) that reads as nothing.
- **Block opening while students are uncovered:** some students deliberately do not compete, and the warning already names them.
- **Every age plain, with a separate open division for the oldest:** two divisions where schools run one.
