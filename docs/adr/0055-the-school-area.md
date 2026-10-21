# ADR-0055: The School area — one place for everything school-wide

- **Status:** Accepted (2026-09)

## Context

[ADR-0054](0054-setting-up-a-carnival.md) left the app with no way to create a catalogue definition outside a carnival, and school-wide functions were scattered. The School tab was one long page (settings, houses, roles, templates). Roster import was a separate nav link. School records administration ([ADR-0035](0035-records-administration.md)) was reachable only through a carnival's Manage → Reports card. The event catalogue had no carnival-less home: a definition could be opened only from inside a carnival's event setup.

## Decision

1. **`/school` is a hub**: a list of sections, each a link with a one-line summary, each on its own route:

   | Route | Section | Who sees the row |
   |---|---|---|
   | `/school/settings` | Name, brand, colours, houses | SystemAdministrator |
   | `/school/roles` | Role assignments | SystemAdministrator |
   | `/import` | Roster import (route unchanged) | SystemAdministrator |
   | `/school/templates` | Event templates and reconcile | SystemAdministrator |
   | `/school/events` | The event catalogue | CarnivalAdministrator, SystemAdministrator |
   | `/school/records` | Records administration | CarnivalAdministrator, SystemAdministrator |

   Rows a role cannot use are not shown, and the hub says so when none remain. The shell shows the School tab to anyone who can use at least one row (carnival or system administrators); the separate Import nav link is gone and the tab stays lit on `/import`. The hub is navigation, not access control: each section screen still refuses on its own, and the server enforces every policy. `/records-admin` redirects to `/school/records`; the carnival's Reports card keeps a link because that is where an organiser stands on the day.
2. **The event catalogue** lists every definition, grouped by sport, with a sport filter and a name search. Each row shows name, code, event type and "used by N carnivals"; retired definitions are listed and badged. A row opens the definition editor without a carnival (no divisions grid; the usage line stays, because a rename reaches every carnival either way). **New definition** (`/school/events/new`) creates a catalogue definition without scheduling it, picking the sport from the catalogue's existing sports or typing a new one (matched case-insensitively to an existing spelling). This is the home for the create path ADR-0054 removed from carnivals.
3. **School records move under School** unchanged in content and policy.

No new endpoint or policy: every section reuses the reads and writes it already had.

## Consequences

- Every school-wide function has a stable address that the checklist, the docs and bookmarks can link to; the hub costs one tap.
- The definition create and editor screens each have a carnival mode and a catalogue mode; ADR-0054's guard applies only to the carnival mode.
- A carnival administrator sees a two-row hub (catalogue and records).

## Rejected alternatives

- **Keep one long `/school` page and append two more sections.** Already several screen-heights long on a phone.
- **A catalogue create inside the templates section.** Reconcile and hand-create are different acts with different guards; one screen for both would hide which one happened.
