# ADR-0059: Adding a catalogue event to a carnival is one press

- **Status:** Accepted (2026-09). Amends [ADR-0054](0054-setting-up-a-carnival.md) §3: "Add an event" becomes "New event…", the secondary path.

## Context

With the roster default ([ADR-0057](0057-age-bands-from-the-roster.md)) and one age per division ([ADR-0058](0058-one-age-per-division.md)) in place, there was still no clear way to put an existing catalogue event, such as High Jump, into a carnival. Event setup's only button, "Add an event", created a **new definition**, so it either duplicated the existing one under another code or was abandoned. The existing definition was a list row reading "not in this carnival", linking to the editor, where the divisions grid ticks one division per box. Nothing said "add", and nothing added the lot.

The act the organiser means is "put High Jump in this carnival", which the server already does in one call: `POST …/events/generate` over the profile's matrix ([ADR-0022](0022-event-and-division-management.md)).

## Decision

1. **Event setup has two lists.** **In this carnival**: each definition with at least one division here, linking to its editor. **Available to add**: each active catalogue definition of the carnival's sport with no division here, with an **Add** button. Other sports stay behind the "Show N from other sports" disclosure, with the same button. A retired definition appears only if the carnival already contests it; the catalogue screen is the way to the rest.
2. **Add is one press.** It generates every division the profile contests for that definition (the full age × category × tier matrix, exactly what the New event screen sends with every box ticked) through the existing generate endpoint. With no saved profile but a roster default, the press saves the default first; with neither, Add is disabled beneath the "import a roster or add ages first" notice. Add and New event's create share one helper, so the two presses cannot drift. The row moves to In this carnival; the editor's grid remains the way to untick divisions this carnival will not contest. Add is offered only to those who can manage the carnival, and is disabled offline.
3. **Creating a definition is secondary.** "Add an event" becomes **New event…**, placed after the Available list, with ADR-0054 §3's behaviour (definition and divisions in one press). The checklist's Events step reads "N in this carnival · M available to add" once the catalogue has been read.

## Consequences

- App only; no endpoint changes.
- An organiser who wants fewer divisions than the profile presses Add, then unticks; the grid keeps its off-profile badges.

## Rejected alternatives

- **Keep the row-then-grid path and explain it**: asking a person to tick 28 boxes to say "yes, High Jump" is the problem.
- **A picker inside "Add an event"** (existing or new): hides the catalogue behind a second screen; the list already shows it.
- **A confirm dialog on Add**: one press is reversible (unschedule, or untick in the grid); a dialog is ceremony.
