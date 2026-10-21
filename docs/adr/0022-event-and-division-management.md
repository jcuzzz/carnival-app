# ADR-0022: Event management — the catalogue gets a screen, and the division profile becomes data

- **Status:** Accepted (2026-08). Partly superseded: the division profile is stored in its own table, not in the carnival's settings ([ADR-0027](0027-the-division-profile-gets-its-own-table.md)); a division's age is one age or `OPEN` ([ADR-0058](0058-one-age-per-division.md)); adding a catalogue event is one press, with the grid as the way to adjust ([ADR-0059](0059-adding-a-catalogue-event-is-one-press.md)). §1's spelling applies to divisions, never to a student ([ADR-0026](0026-the-student-category-is-server-side-only.md)).

## Context

The hierarchy already existed (`EventDefinition` is the umbrella, `CarnivalEvent` one contest of it) and a name already cascaded, because it is stored only on the definition. What was missing was everything around it: the catalogue had no screen, so an event could not be edited; the only generator of the age × category × tier matrix was the template apply, which nothing in the app called, so divisions were created one per submit; which divisions a carnival contests existed only in a server-side template file; a division created by mistake could never be removed; and "category" meant M/F/S in the code but the umbrella event in staff speech.

## Decision

### 1. Three words, used the same way on every surface

- **Event**: the umbrella (`EventDefinition`), "Freestyle 50m".
- **Division**: one contest of an event at a carnival (`CarnivalEvent`), "Freestyle 50m · 13 · Boys · Competitive".
- **Category**: M/F/S only, rendered **Boys / Girls / Open**, as the school's own programs print them. A division is a contest, not a person; the spelling names divisions and is never applied to a student.

An absent axis reads "All ages" or "All categories"; "Open" means S and nothing else, so no label carries two meanings. The entity names do not change. No screen calls an umbrella a "category", and none calls M/F/S a gender or sex.

### 2. The division profile is carnival data, seeded from the template

A carnival holds its **division profile**: the ages, categories and tiers it contests. Applying a template seeds it only when the carnival has none, so re-applying never reverts an edit; the carnival owns what it contests from then on.

### 3. Generating divisions is one server operation, with one generator

`POST /carnivals/{id}/events/generate` creates a set of divisions for one event in one transaction, for carnival administrators, while the carnival is editable. It is idempotent on the division's uniqueness, so a division that already exists, or one listed twice, is skipped and a repeated press creates nothing. The template apply and this endpoint share one matrix loop, so the two cannot drift. Each created division writes the same `carnival-event.schedule` audit event as scheduling one division, so every removal pairs with a creation.

### 4. A division can be removed until it has been contested

`DELETE /carnivals/{id}/events/{eventId}` removes a division created in error, audited as `carnival-event.unschedule`. It is refused with `409 EVENT_HAS_ENTRIES`, naming the entries and results in the way, once anyone has entered. A division that ran is competition history: closing it is the path, and closing is reversible.

### 5. Record categories are not division reach

A definition's `RecordCategoriesJson` (the ages × categories it keeps school records in) is a different thing from which divisions a carnival contests, and stays on the definition. The old per-definition divisions list, which no logic read, is removed.

### 6. Renaming is global, by design

A definition is shared by every carnival and cited by school records, so renaming it relabels every division of it, past and present. The editor shows the usage (carnivals, divisions, records) beside the name and says a rename applies to all of them. An event is retired by deactivating it, never by renaming it into something else; an inactive definition cannot be scheduled.

### 7. Event setup is an online path

Configuration needs the server's uniqueness and conflict answers before an administrator can decide anything, so these screens write directly and refuse plainly when offline. Capture stays offline-capable ([ADR-0012](0012-client-data-layer.md)). The offline check hides a button; the server remains the authority.

### 8. The grid, and what a profile change does

Each event's editor has a **division grid** of ages × category and tier: ticking generates a division, unticking removes one, and each ticked box shows its entry count so an administrator can see what would be refused. **Adding an age to the profile does not create divisions**: the new boxes appear unticked, and the event list names what the profile declares that an event contests no division of.

### 9. Bounds

A profile has at most 32 ages, and one generate call creates at most 200 divisions, so a mistyped profile cannot run a transaction off a cliff.

How the catalogue and divisions are modelled: [data model § events and divisions](../architecture/data-model.md#events-and-divisions).

## Consequences

- A school configures its own carnival without editing a template file; a mistyped division is removable; "what do we contest" has an answer in the database.
- The profile is a second description of divisions and can disagree with the grid; the review prompt exists because of that. A school that deliberately runs an event for fewer ages is reminded of the missing ones on every visit.
- The usage count makes the cost of a rename visible, which will sometimes stop a quick fix.
- Athletics may contest different ages per event; the profile is carnival-wide.

## Rejected alternatives

- **An editable template JSON in the UI**: templates are versioned configuration beside the deployment; editing a server file is a support call, and would make the file the source of truth for a running carnival.
- **Client-side generation as a loop of POSTs**: two generators drift, and a partial failure leaves a half-built matrix.
- **A name on each division**: inverts the cascade asked for, and breaks tile grouping by definition.
- **Hard-deleting an event definition**: definitions are referenced by divisions and records forever; deactivation is right.
