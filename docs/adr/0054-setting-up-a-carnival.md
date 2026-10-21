# ADR-0054: Setting up a carnival — a guided path from "New carnival" to the first result

- **Status:** Accepted (2026-09). §3 partly superseded: the catalogue gets its own create path ([ADR-0055](0055-the-school-area.md)), the "add divisions first" block applies only when there is neither a roster nor a saved profile ([ADR-0057](0057-age-bands-from-the-roster.md)), and adding an existing catalogue event is one press with "New event…" as the secondary path ([ADR-0059](0059-adding-a-catalogue-event-is-one-press.md)). The non-goal of deriving divisions from the roster is withdrawn by ADR-0057.

## Context

Making a carnival from scratch was not intuitive. Nothing said which steps remained or in what order (Divisions before Events), and Divisions, Events, Program and Roster import sat as equal links on Manage. The one-click setup, `POST /carnivals/{id}/apply-template`, was API-only. "Add an event" on a carnival with no divisions profile created a catalogue definition, scheduled nothing, and reported success. Opening divisions meant one row at a time, and a new Athletics carnival could not be created on the first press because auto-fill was ticked with no lane count.

## Decision

1. **Manage opens with a setup checklist.** Ordered steps, each a link to where the step is done, with a done state derived from data the screen already reads (never stored):
   - **Roster**: done when a promoted roster exists (count shown). The count comes from the carnival progress read, which every carnival viewer may make; the import screen itself stays SystemAdministrator-only, so only they get the link.
   - **Divisions**: done when the profile has at least one age, category and tier.
   - **Events**: done when at least one division is scheduled.
   - **Program**: optional.
   - **Open**: done once the carnival has left Draft, with the count of divisions still Scheduled.

   A failed read shows the step as unknown, never as not done. The deck's empty state links to the first undone required step.
2. **"Apply template" lives on the carnival.** Manage offers it to carnival administrators (`carnivals:administer`), calling the existing endpoint. The picker lists readable templates with the one matching the carnival's type pre-selected; when none matches it opens on a placeholder with Apply disabled. Re-applying is idempotent. To fill the picker, `GET /admin/event-templates` is `carnivals:administer`; previewing and applying a reconcile stay `school:administer` ([ADR-0050](0050-reconcile-template.md)).
3. **"Add an event" never finishes with nothing scheduled.** With no ages to build a grid from, the form is blocked with links to Divisions and Apply template; with no boxes ticked the submit is disabled; the toast reports the divisions scheduled in this carnival.
4. **Open all.** `POST /carnivals/{id}/events/open-scheduled` opens every Scheduled division in one act (`carnivals:administer`). Its state gate is the single-event open's: refused only on a finalised carnival (`CARNIVAL_NOT_EDITABLE`), so the bulk right equals the single right. It writes one audit row on the carnival (`carnival-event.open-all`, with the count and ids), none when nothing was opened, and bumps each opened division's `Version`. Manage offers **Open all divisions** beside Open carnival, and the event page's "results need Open" notice carries the Open control for those who may use it.
5. **A new carnival creates on the first press**: 8 lanes with auto-fill ticked for every type. Auto-fill applies only to timed events ([ADR-0038](0038-auto-fill-lanes.md)), so field events stay unlaned.
6. **The division grid checkbox is optimistic**: a ticked box stays ticked and disabled until the write lands; a refusal reverts it with the reason beside the grid.

A wizard was not built: the checklist is enough, and every step stays a real screen.

## Consequences

- One new endpoint and audit action; no migration.
- A carnival administrator can name a template (key, name, version, count; no catalogue contents or student data) but still cannot reconcile one.
- Setup is now: New carnival → Apply template → Open carnival → Open all divisions.
- Manage makes three more reads (events, schedule, progress). The progress read carries more than the roster count the checklist uses; a narrow count read would be tighter.

## Rejected alternatives

- **A default divisions profile on carnival create.** Invents ages and categories a Cross-country or Other carnival may not want; the template is the right default and is now one click away.
- **Opening divisions automatically when the carnival opens.** Some schools open events as they are called.
- **Silently applying a template on create.** Surprising for a school that meant to build the carnival by hand.
