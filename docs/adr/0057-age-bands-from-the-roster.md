# ADR-0057: Age bands come from the roster and are picked, not typed

- **Status:** Accepted (2026-09). §3 (the band editor) superseded by [ADR-0058](0058-one-age-per-division.md) §4; §1, §2 and §4 stand, in ADR-0058's one-age shape. Withdraws [ADR-0054](0054-setting-up-a-carnival.md)'s non-goal of deriving divisions from the roster.

## Context

A divisions profile had to exist before an event could be added, and its ages were typed as free text. The order was imposed by the client only: the server never requires a profile to schedule a division, but the "Add an event" grid had no rows without one, so ADR-0054 blocked the form.

Typed ages were the riskier half. Eligibility is exact string equality between a division's age and the student's `CompetitionAgeGroup`, which the import derives from the birth date and the cut-off and then discards the date. A code the roster never derives ("U13", "Year 8") passes validation on shape alone and surfaces as every entry refused on carnival day. The roster is imported before divisions are set up anyway (it is the checklist's first step), so its age groups are known when the organiser reaches Divisions, and unlike a fixed default they invent nothing.

## Decision

1. **The carnival settings read exposes the roster's age groups.** `GET /carnivals/{id}/settings` returns `rosterAgeGroups`: the distinct `CompetitionAgeGroup` values of current, active students, youngest first with non-numeric codes (`SNR`) last, excluding any code the age rule would refuse (such as a negative age from a bad birth date). It is read-only; the PUT ignores it.
2. **A carnival with no saved profile has a roster default.** When no profile is saved and the roster has age groups, the settings read also returns `divisionsDefault`: every roster age, categories `M` and `F`, tier Competitive. `divisions` stays null, so "not configured" is still readable. The default is computed, never stored, until the organiser acts on it:
   - "Add an event" is not blocked: the grid renders from the default, and the create press first saves it through the ordinary settings PUT, then generates. If another administrator saved a different profile meanwhile, the press is refused and the grid re-renders from the saved one.
   - The Divisions checklist step reads done, showing the roster default, and the Divisions screen opens pre-filled with it.
   - Applying a template still seeds the template's divisions when no profile is saved, so a template wins over the roster default.
   - With neither a roster nor a saved profile, "Add an event" stays blocked (ADR-0054 §3).

   An Open (`S`) category is not defaulted, because an `S` student already clears Boys and Girls divisions ([ADR-0023](0023-open-category-eligibility-and-one-entry-per-event.md)); Social is a tick.
3. *(Superseded by ADR-0058 §4.)* Ages are picked from the roster's codes in the Divisions editor rather than typed. The API still accepts any alphanumeric code, so a script or template can name an age ahead of a roster.
4. **What does not change.** The profile remains the carnival's data ([ADR-0027](0027-the-division-profile-gets-its-own-table.md)); the generate endpoint remains the one writer of divisions ([ADR-0022](0022-event-and-division-management.md)); editing the profile creates or removes no divisions; eligibility stays exact-match on the derived code.

## Consequences

- A new carnival with a roster reaches "Add an event" without visiting Divisions; the profile is saved the first time an event is created.
- Every settings read and write runs one distinct query over current, active students; no migration.
- A template naming ages the roster lacks becomes visible in the editor rather than on carnival day.
- A re-import that changes the derived codes (a new competition year) does not touch a saved profile; the Divisions screen flags saved ages the roster no longer derives, and re-picking is the fix.

## Rejected alternatives

- **A fixed 12–18 default on carnival create**: invents ages; the roster default cannot name an age the school has no student in.
- **Seeding the profile inside `POST /events/generate`**: a write hidden in another endpoint; the settings PUT stays the one profile writer.
- **Per-event age scopes without a profile**: loses the consistent grid and its off-profile badge.
- **Free text with autocomplete**: still typeable wrong.
- **Year-level bands**: the roster derives an age, not a year level, and eligibility is by age.
- **Computing the default on the client**: one place computes it, so the checklist and both screens agree.
