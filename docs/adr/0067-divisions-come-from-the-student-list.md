# ADR-0067: Divisions come from the student list

- **Status:** Accepted (2026-11). Partly supersedes:
  - [ADR-0022](0022-event-and-division-management.md) §2: the profile is seeded from the roster's ages, not the template's.
  - [ADR-0027](0027-the-division-profile-gets-its-own-table.md) §3: what a template's seed holds.
  - [ADR-0054](0054-setting-up-a-carnival.md) §1 and §3: the Divisions step, and the no-ages block now points at the student import.
  - [ADR-0057](0057-age-bands-from-the-roster.md) §2: the template no longer wins, there is no Divisions step or screen to pre-fill, and the no-roster wording changes.
  - [ADR-0058](0058-one-age-per-division.md) §5: there is no 5–21 fallback.
  - [ADR-0059](0059-adding-a-catalogue-event-is-one-press.md) §2: the no-roster notice.

  [ADR-0022](0022-event-and-division-management.md) §8 and [ADR-0057](0057-age-bands-from-the-roster.md) §4 stand.

## Context

A carnival's divisions profile (its ages × categories × tiers) was a setup step of its own. Organisers did not understand it, and it froze the wrong ages in two ways:

- **Templates.** Both shipped templates name ages 12–18, and applying one generated divisions over those ages whatever the roster or a saved profile said. A school whose roster derives `SNR`, or an 11, got divisions no student could enter, and students no division would take.
- **No roster.** With no roster, the editor offered 5–21, and saving that froze 17 ages.

Eligibility is exact match on the roster's derived age ([ADR-0058](0058-one-age-per-division.md)), so any age the roster does not derive is a division nobody can enter. Since the student import now comes first in carnival setup ([ADR-0066](0066-the-setup-flow.md)), the roster's ages are known before any event is added.

## Decision

1. **The divisions profile is not a setup step.** Event setup shows the carnival's ages, categories and tiers as one line, with Change. The profile is still stored per carnival ([ADR-0027](0027-the-division-profile-gets-its-own-table.md)). It is saved the first time divisions are generated, whether by Add, New event or the standard events.
2. **Ages come from the student list.** Until saved, they are the current roster's age groups. Change can narrow them over the roster's own ages, but never adds an age the roster does not derive. There is no fallback range.
3. **Categories and tiers are a short carnival setting.** They default to Boys and Girls, Competitive.
4. **A template uses the carnival's ages.** Applying one generates each event's divisions over the carnival's ages: the saved ones, else the roster's. It uses the carnival's categories and tiers, else the template's (which then seed the setting), narrowed by any tiers the template gives one event. `openDivision` still adds the all-ages division. The template's age list still names the record categories of the definitions it creates and its program's slot filters, but no longer decides which divisions exist. A division outside every slot filter is left out of the program and named on the ready-to-open check.
5. **With no roster yet**, a carnival with no saved ages cannot have events added:
   - Add, New event and the standard events point at the student import.
   - The API refuses the template apply with `409 ROSTER_REQUIRED`.
   - The carnival's details, scoring, lanes and program can still be set up.
6. **A re-import never rewrites saved ages** ([ADR-0057](0057-age-bands-from-the-roster.md) §4). When the saved ages include an age no current student has, Event setup warns and offers one press that removes those ages, keeping any deliberate narrowing. When the roster has ages the saved ones leave out, a deliberate narrowing, Event setup only notes them quietly, and Change widens the list. Divisions follow through each event's grid, as before ([ADR-0022](0022-event-and-division-management.md) §8).
7. **Existing carnivals keep their saved profiles.** Nothing is migrated.

How divisions are modelled: [data model § division profile](../architecture/data-model.md#division-profile).

## Consequences

- A carnival's divisions match the students it has, and setup loses a step and a word nobody used.
- A school that wants an event for fewer ages still unticks them in that event's grid.
- An age the roster lacks cannot be planned ahead in the app; the API still accepts any code ([ADR-0057](0057-age-bands-from-the-roster.md) §3), so a script can.
- A template's program places only the ages its slot filters name, so a school with other ages extends the program by hand.
- Carnivals saved under the old rules (for example 5–21, or a template's 12–18) are warned on Event setup when they hold ages no student has, and are fixed with one press.

## Rejected alternatives

- **Keep the step, with better defaults:** fixes the wrong ages but keeps the step the maintainer found strange.
- **Store no ages at all, deriving them live from the roster:** the cleanest model, but it needs a migration and a young-end clamp at import, and moves ages under divisions that already have entries.
- **Keep the template's ages where the roster has none:** invents ages no student can match, which is the defect this decision removes.
- **Rewrite saved ages automatically on re-import:** silently changes a carnival someone set up, against [ADR-0057](0057-age-bands-from-the-roster.md) §4.
