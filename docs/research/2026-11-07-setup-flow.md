# Setup flow: first run to the first carnival

Research and proposal, 2026-11-07, branch `feature/setup-flow` (from `main` at 39b5867). For the maintainer to decide from. Inputs: the maintainer's feedback, the current-state audit and external research (both outside the repo, in `projects/setup-ux-audit.md` and `projects/setup-ux-research.md`), ADRs 0022, 0024, 0027, 0032, 0045, 0054, 0055, 0057–0060, 0064, 0065, the code and `app/ui-review/`. Paths are relative to `app/src` unless they start `api/`, `config/` or `docs/`.

## 1. Problem

The maintainer says that "Add divisions" on the Deck is a strange workflow. He wants the first admin account to be account creation only, then school configuration, then a carnival with the student list imported into it, and the whole setup flow looked at again. The code confirms all three points:

| Symptom | Cause |
|---|---|
| "Add divisions" is the Deck's primary button | The Deck's empty state links to the first undone checklist step (`screens/CarnivalDetailScreen.tsx:70-80,108-114`, ADR-0054 §1). The Deck passes `participation: null`, so the Roster step reads "unknown", is skipped, and Divisions becomes the first step. A new carnival lands on the Deck (`features/carnivals/NewCarnivalForm.tsx:59`), not on Manage, which breaks the Draft → Manage landing rule (`carnivals/landing.ts:10`, ADR-0024 §5). |
| After the first admin there is no path forward | `finish()` navigates to `/` (`screens/SetupScreen.tsx:87-89`). With no carnival selected, Home shows "No carnivals yet." (`screens/CarnivalsScreen.tsx:83`). The School hub is seven links with no order and no done-state (`screens/SchoolScreen.tsx:23-67`). The intended order exists only in `docs/admin/linux-runbook.md` §B7. |
| The student import is far from the carnival | Import is reachable only from School, and from Manage only for a SystemAdministrator (`features/carnivals/setupSteps.ts:82`). Its year defaults to the earliest upcoming carnival's year, not this carnival's. Nothing leads back to the carnival afterwards. |
| Divisions is a step people do not understand | "Divisions profile" is a second description of what the event grid already says (ADR-0022, Consequences). Wrong ages get frozen two ways: templates hard-code 12–18 (`config/event-templates/*.json`), and with no roster the editor falls back to 5–21 (`features/events/ageCodes.ts:4`). |
| "Open carnival" does not make results possible | The primary button leaves every division Scheduled. The secondary "Open carnival and all N divisions" is the one that works (`screens/ManageScreen.tsx:299-310`). |

## 2. Target journey

These screens make the maintainer's order concrete without changing the data model. The roster stays school-wide: one current `RosterSnapshot` per student (ADR-0060), with entries copying the snapshot (data model).

**Reconciling "import the student list to the carnival":** the import becomes the first step of carnival setup. It is launched from the carnival as `/import?carnival=<id>`, takes its competition year from the carnival's date, and returns to the carnival afterwards. It still updates the school's one student list. Every carnival in that year sees the same list, and a second carnival in the same year finds the step already done. School → Roster import stays for mid-year re-imports. ADR-0060 stands: the year is still chosen at import, and the carnival only supplies the default. A per-carnival roster (the audit's structural item 6) is rejected. It would duplicate students per carnival and supersede ADR-0060 §2 for no gain over this.

| # | Screen | What the user sees | Primary action | Leads to |
|---|---|---|---|---|
| 1 | **Set up your account** (`/setup/:token`) | Username (hint "a plain name such as jsmith"), display name, password. Then the authenticator offer (skippable, ADR-0065 §5) and recovery codes. | Create account → Continue | 2 |
| 2 | **School**, with a "Set up your school" list at the top (SystemAdministrator) | Three rows built from the existing `SetupChecklist`: 1 School name, colours and houses; 2 Staff accounts (optional); 3 Your first carnival. Each row has a quiet Done badge, and the hub rows sit below. Row 1 is done once an administrator has saved School settings, since a new install seeds no sample school. Once every required row is done, the list disappears and nothing replaces it. | The first undone row | 3, 4 or 5 |
| 3 | **School settings** | Name, colour, houses, with the note "A house can't be renamed once students are imported" (ADR-0045 §3). | Save | 2 (row ticks) |
| 4 | **People** (optional) | Add a person and their role in one form. The setup link to hand over. | Add | 2 |
| 5 | **New carnival** | Name, sport, date, venue. | Create | 6 |
| 6 | **Manage → Set the carnival up** | Checklist: 1 Students, 2 Events, 3 Program (optional), 4 Open. "Do this next" marks one row. | The first undone step | 7, 8, 9 or 10 |
| 7 | **Import students** (`/import?carnival=<id>`) | Year preset to the carnival's, "Ages as at 31 December 2027". Upload, then review counts and issues. | Update the student list (approve and promote in one press) | "Back to *carnival*" → 6 (Students: "412 students · ages for 2027") |
| 8 | **Event setup** | One line: "Ages 12–18 from the student list · Boys, Girls · Competitive · Change". On an empty carnival the primary is "Add the standard swimming events". After that come the In this carnival and Available to add lists. "← Manage". | Add the standard events, or Add per event | 6 |
| 9 | **Program** (optional) | The program the template built, editable. "← Manage". | Save | 6 |
| 10 | **Manage → Ready to open** | A short check, all from reads Manage already makes: the roster year matches; N events and M divisions; divisions whose age no student has; divisions not in the program. Warnings only, never a block. | **Open carnival** (opens the carnival and every Scheduled division). Secondary: "Open the carnival only". | 11 |
| 11 | **Deck**, on the day | Event tiles, find an event, per-division Open and Close as events are called, capture. No setup actions. A Draft carnival with no events shows "This carnival isn't set up yet" and one "Set the carnival up" link (managers only). | Pick an event | Capture deck |

This follows the research: a short wizard only for the account, then a small task list with real done-states (GOV.UK task list, Business Central, at most six rows), empty states that link to the action (NN/g), and a check-answers page before the commit (GOV.UK).

## 3. Placement

- **School (once):** name, colours, houses; staff accounts and roles; the catalogue (maintained, rarely touched); records; mid-year re-imports.
- **Carnival setup (Manage):** students for this carnival's year, events and their ages and categories, the program, officials, scoring, lanes, opening.
- **Deck (on the day only):** capture, and opening or closing a division as it is called. ADR-0054 rejected auto-opening because "some schools open events as they are called", so per-division Open and Close is an on-the-day act and stays. Officials are not.

| Action | Now | Moves to | Phase |
|---|---|---|---|
| "Add divisions" / "Add events" button | Deck empty state | Manage only. The Deck says "Set the carnival up" | 1 |
| New carnival landing | Deck | Manage (the landing rule already says so) | 1 |
| First-admin finish | `/`, the empty carnival list | `/school`, with "Set up your school" | 1 |
| "← Carnival" back links on Event setup and Program | Deck (`EventSetupScreen.tsx:98`, `ScheduleBuilderScreen.tsx:66`) | "← Manage" | 1 |
| "Open carnival" (carnival only) as the primary | Manage | The primary opens the carnival and all divisions; carnival-only becomes the secondary | 1 |
| Roster import | School hub only | The carnival checklist's Students step, plus the School hub | 1 |
| Officials (raw subject ids) | Deck event list (`EventRow.tsx:82-110`) | Hidden in Phase 1; a picker on the event editor later, if staff ask | 1 |
| Divisions profile as a checklist step and its own screen | Manage step 2 | A line with Change on Event setup | 2 |
| Apply a template | A section on Manage | The empty-state primary on Event setup, "Add the standard *sport* events" | 2 |
| School → Event templates (reconcile) | School hub, "Event templates" | Stays, renamed "Update the event catalogue from a template" | 1 |
| People, then Roles | Two screens for one act (`SchoolPeopleScreen.tsx:95-96`) | Add a person with a role in one form. Roles keeps provider grants and history | 2 |
| Per-division Open and Close | Deck event list | Stays (on the day) | — |

**Templates and the catalogue.** A carnival's Apply template already creates any missing catalogue definitions (ADR-0054 §2), so a fresh school needs no catalogue step: the first carnival fills it. The School reconcile remains an advanced tool for taking a new template version. Both acts keep their names, which no longer look alike.

## 4. Divisions and age bands

The facts:

- Eligibility is exact match between a division's age and the student's derived `CompetitionAgeGroup` (ADR-0058).
- `GET /carnivals/{id}/settings` already returns `rosterAgeGroups` and a computed `divisionsDefault` (ADR-0057).
- Applying a template **generates divisions from the template's ages whatever the saved profile or roster says** (`api/.../EventTemplateService.cs:236-249`). Only the profile seed is conditional (:293-299).
- The template's schedule slots filter on those same ages (`athletics.json` field stations, ages 12–18).

**Option A: keep the step and fix its defaults.** Rename it "Ages and categories". Templates generate from the saved profile, or else the roster default. Drop the 5–21 fallback, so with no roster Add stays blocked and points at the import. *Supersedes:* ADR-0057 §2 ("template wins") and ADR-0058 §5 (5–21). Size S–M. This fixes the wrong-age bugs but keeps the step the maintainer finds strange.

**Option B: no step; ages from the roster, categories and tiers as a short setting (recommended).** The checklist loses Divisions. Event setup shows one line, "Ages 12–18 from the student list · Boys, Girls · Competitive", whose Change opens today's editor, trimmed. It edits categories and tiers, keeps the From/To pair only for narrowing, and has no 5–21 fallback. The profile table stays as storage (ADR-0027), saved silently on the first Add or template apply, as Add already does (ADR-0057 §2). A template applies with the carnival's ages (saved profile, else roster default) and the carnival's categories and tiers (else the template's, which then seed the setting), narrowed by any per-event tiers the template gives; with no roster and no saved ages it is refused (`409 ROSTER_REQUIRED`). After a re-import, saved ages that no current student has are warned on that line with one press, "Remove those ages", which keeps any deliberate narrowing; student-list ages the saved ones leave out get only a quiet note. Neither is automatic (ADR-0057 §4). Existing saved profiles are kept; no migration. *Supersedes (ADR-0067):* ADR-0022 §2, ADR-0027 §3 (the seed's ages), ADR-0054 §1 and §3, ADR-0057 §2, ADR-0058 §5, ADR-0059 §2 (the no-roster notice). ADR-0022 §8 and ADR-0057 §4 stand, because the profile is still stored. Size M.

**Option C: no stored ages at all.** The profile holds categories × tiers only. Divisions are generated from live roster ages, and the young end is clamped in the import mapping beside `openFromAge`. This is the cleanest model, but needs a migration and supersedes ADR-0027's row shape and ADR-0057/0058 more widely. Size L. Not now.

**Templates' 12–18.** Under A or B, `divisions.ageGroups` in a template stops deciding which divisions exist. It still names the record categories of the definitions a template creates, orders divisions and feeds the slot filters, so a roster age outside 12–18 (an 11, or `SNR` where a school sets `openFromAge`) gets divisions that sit outside the program. The program is advisory, so this is a warning on the Ready to open check, not a failure. Templates are deployment configuration, not code, so this respects the hard rule.

**With no roster yet**, the school and carnival can still be created and the program sketched, but Add and Add the standard events are disabled under "Import the student list first", which links to step 7. That is the honest answer, because nobody can be entered anyway.

## 5. Carry-over between carnivals

The app has no copy: `POST /carnivals` takes only details. Templates rebuild events, divisions and the program on every apply, idempotently. Scoring, lanes and adjustment categories default per carnival. SportsTrak ("import last year's carnival … the best way") and OpenTrack ("Copy competition") make cloning the main answer to "setup is infrequent and forgotten".

**Recommendation: not now.** No school has run a second season on this app yet, and "Add the standard events" with roster ages rebuilds a year-two carnival in one press. What a school loses is its edits (removed events, scoring tweaks, program changes). If that hurts after the first season, the simplest version is an optional "Start from: *last Swimming carnival*" on New carnival, preselected by sport. It would copy the event list (definition × category × tier, never ages), the program and the settings, and take ages from the roster. That needs one endpoint and an ADR, size M.

## 6. Changes, in phases

[ADR-0066](../adr/0066-the-setup-flow.md) records the order and the placement; [ADR-0067](../adr/0067-divisions-come-from-the-student-list.md) records Option B. Both were accepted on 2026-11-07. Every change updates `docs/architecture/app-navigation.md` (whose Deck sentence is already stale, see the report) and `docs/guide/`, and runs `npm run screenshots`.

### Phase 1: quick wins (app only, ADR-0066)

| # | Change | Size | Files | ADRs | Risk | Tests |
|---|---|---|---|---|---|---|
| 1.1 | First admin lands on `/school`; "Set up your school" list (settings, staff, first carnival); username hint; the empty carnival list links to School | M | `screens/SetupScreen.tsx`, `screens/SchoolScreen.tsx` (reuse `SetupChecklist`), `screens/CarnivalsScreen.tsx` | 0066 | The done-states rest on existing reads (settings, role assignments, carnivals), and settings seeded from `.env` read as done | `SetupScreen.test`, `SchoolAdminScreens.test`; the `local-accounts.spec` first-admin path; shots `setup-first-admin`, `school-hub`, new `school-hub-setup` |
| 1.2 | New carnival lands on Manage | S | `features/carnivals/NewCarnivalForm.tsx:59` | none (ADR-0024 §5) | none | `NewCarnivalForm.test`, `carnival-new` |
| 1.3 | The Deck's empty state is one "Set the carnival up" link; the Officials button leaves the Deck | S | `screens/CarnivalDetailScreen.tsx` (drop the setup-steps read), `features/events/EventRow.tsx`, `e2e/screenshots.spec.ts:415` | 0066 supersedes ADR-0054 §1's last sentence and restates ADR-0024 §5 | "My events" gets no new assignments until a picker exists (Later); assignment is unusable today anyway | `CarnivalDetailScreen.test`, `EventRow.test`, a new `deck-not-set-up` shot |
| 1.4 | Back links on Event setup and Program go to Manage | S | `screens/EventSetupScreen.tsx:98`, `screens/ScheduleBuilderScreen.tsx:66` | none | none | screen tests, `event-setup` |
| 1.5 | One "Open carnival" (with every division) plus a "Ready to open" check; carnival-only as the secondary | S | `screens/ManageScreen.tsx:294-314`, `features/carnivals/setupSteps.ts` (the Open step) | 0066 supersedes ADR-0054 §4's presentation (endpoints unchanged) | A school that opens events as called presses the primary by habit; this is reversible per division | `ManageScreen.test`; shots `manage-*` |
| 1.6 | Import from the carnival: Students step → `/import?carnival=<id>`, year from its date, "Back to *carnival*"; approve and promote in one press; plain copy; batch GUID hidden | M | `features/carnivals/setupSteps.ts`, `screens/ImportReviewScreen.tsx`, `features/imports/BatchList.tsx`, `docs/architecture/imports.md` | 0066 supersedes ADR-0060 §3's pre-fill (the year stays an import input) | A failure between approve and promote leaves an Approved batch, which the screen already shows; CarnivalAdministrators still cannot import | `ImportReviewScreen.test`, `setupSteps.test`; the import e2e; shots `import-review`, `manage-checklist-roster-*` |
| 1.7 | Rename School's "Event templates"; an empty catalogue on Event setup offers the template | S | `screens/SchoolScreen.tsx`, `screens/SchoolTemplatesScreen.tsx`, `screens/EventSetupScreen.tsx:141` | none | none | `school-templates`, `event-setup` |

### Phase 2: structural

| # | Change | Size | Files | ADRs | Risk | Tests |
|---|---|---|---|---|---|---|
| 2.1 | Option B: Divisions leaves the checklist; the Event setup line with Change; templates use the carnival's ages and are refused without a roster; no 5–21 fallback; a "Remove those ages" press for saved ages no student has | M | `api/.../Carnivals/EventTemplateService.cs`, `features/carnivals/setupSteps.ts`, `screens/EventSetupScreen.tsx`, `screens/DivisionsProfileScreen.tsx`, `screens/NewEventScreen.tsx:339`, `features/events/ageCodes.ts`, `config/event-templates/*.json` (`$comment`) | ADR-0067 (see §4 for what it supersedes) | Roster ages outside the slot filters go unscheduled (warned at 1.5); existing saved profiles are untouched | API: apply with roster ages, `SNR`, no roster refused, saved profile wins. App: `setupSteps.test`, `DivisionsProfileScreen.test`. Shots `divisions-*`, `manage-checklist*`, `event-new-*` |
| 2.2 | "Add the standard events" moves from Manage into Event setup | S | `features/carnivals/ApplyTemplateSection.tsx`, `screens/ManageScreen.tsx`, `screens/EventSetupScreen.tsx` | ADR-0066 supersedes ADR-0054 §2's placement | none | `manage-apply-template`, `event-setup` |
| 2.3 | People and Roles merged: add a person with a role | S | `screens/SchoolPeopleScreen.tsx` (create, then the existing role grant by username; no API change) | ADR-0066 §2; ADR-0048 stands | Two writes: a refused grant after a created account is reported as "created; role not granted" with a link to Roles | `SchoolPeopleScreen.test`, `local-accounts.spec`; shots `school-people*` |

### Later

- An officials picker on the event editor, from people holding roles, with a narrow `carnivals:administer` read of names; only if staff ask for "My events" (decision 4), size M.
- Copy from the last carnival of the same sport (§5), size M.
- Option C, size L.
- Column mapping on import (ADR-0064, already planned).
- A young-end age clamp in the mapping, if a school asks.
- A server-side gate on opening: not recommended, because ADR-0054's warn-don't-block stands.

## 7. Decisions for the maintainer

All six were approved as recommended on 2026-11-07.


1. **Divisions:** Option B (no step, roster ages, a short categories and tiers setting), A, or C? *Recommend B.*
2. **Where the student import lives:** as carnival setup's first step, with the year from the carnival and the list still school-wide, plus School for re-imports? *Recommend yes.* A per-carnival roster is not recommended.
3. **"Open carnival":** should the primary open every division, with "Open the carnival only" for schools that open events as called? *Recommend yes.*
4. **Officials:** hide them from the Deck now and build a picker in setup later, or drop per-division officials entirely? *Recommend hiding them now and building the picker only if staff ask for "My events".*
5. **Approve and promote in one press?** It is the same person in practice, and the review table is already above the button. *Recommend yes.*
6. **Carry-over:** *Recommend not now.* Revisit after the first school's second season.

## 8. Sources

From the external research file:

- Hy-Tek Meet Manager, [generic steps in setting up a meet](https://hytek.active.com/User_Guides_HTML/TFMM6/genericstepsinsettingupmeet.htm), [meet setup](https://hytek.active.com/user_guides_html/swmm8/meetsetup.htm), [scratches](https://hytek.active.com/user_guides_html/swmm6/scratch.htm)
- SwimTopia, [Meet Maestro quick start](https://help.swimtopia.com/hc/en-us/articles/360046833691-Meet-Maestro-Quick-Start-Guide), [Block Party meets](https://help.swimtopia.com/hc/en-us/articles/41497227746459-Block-Party-Meets-Getting-Started-Guide)
- OpenTrack, [docs](https://docs.opentrack.run/), [events and scoring](https://docs.opentrack.run/comp/manage/eventsandscoring/), [age groups](https://docs.opentrack.run/comp/precomp/agegroups/), [copy a competition](https://docs.opentrack.run/start/copycomp/)
- SportsTrak / CaRob, [setting up from scratch](https://www.carobcom.com/help/setting-up-from-scratch), [ages and years](https://www.carobcom.com/help/ages-and-years), [student import](https://www.carobcom.com/help/student-import), [CSV import](https://www.carobcom.com/help/import-students-from-csv2)
- [Houseplay](https://houseplay.com.au/); LiveHeats, [importing athletes](https://help.liveheats.com/en/article/importing-your-athlete-or-teams-list-keh8ag)
- NN/g, [wizards](https://www.nngroup.com/articles/wizards/), [progressive disclosure](https://www.nngroup.com/articles/progressive-disclosure/), [empty states](https://www.nngroup.com/articles/empty-state-interface-design/), [onboarding tutorials](https://www.nngroup.com/articles/onboarding-tutorials/)
- GOV.UK Design System, [task list](https://design-system.service.gov.uk/components/task-list/), [complete multiple tasks](https://design-system.service.gov.uk/patterns/complete-multiple-tasks/), [check answers](https://design-system.service.gov.uk/patterns/check-answers/)
- Microsoft, [Business Central onboarding checklist](https://learn.microsoft.com/en-us/dynamics365/business-central/dev-itpro/administration/onboarding-checklist); Shopify, [setup guide](https://shopify.dev/docs/api/app-home/patterns/compositions/setup-guide); Primer, [feature onboarding](https://primer.style/design/ui-patterns/feature-onboarding); Material, [onboarding](https://m2.material.io/design/communication/onboarding.html); Candu, [Linear onboarding teardown](https://www.candu.ai/blog/linear-onboarding-teardown)
