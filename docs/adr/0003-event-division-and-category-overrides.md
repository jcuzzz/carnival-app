# ADR-0003: Event divisions and competition category overrides

- **Status:** Accepted (2026-07). Category eligibility amended by [ADR-0023](0023-open-category-eligibility-and-one-entry-per-event.md). Partly superseded by [ADR-0026](0026-the-student-category-is-server-side-only.md): reports and exports do not show a student's effective category or an override flag; a student's category never leaves the server.

## Context

Events are contested in divisions by age group and by a competition category (M, F or S). The roster export carries a gender column. The platform must group students for competition without becoming a gender register (data minimisation), and must let a student compete in a different category from their imported default without corrupting roster history or the audit trail.

## Decision

- **The default category is derived at import.** The roster's gender column maps M → M, F → F and anything else → S, stored as the roster snapshot's competition category. It is a competition grouping default, not a gender attribute; no other gender data is imported or stored.
- **A division is age group × category** (the tier joined later, [ADR-0014](0014-result-entry-navigation-and-schedule-model.md)). A null category means all categories. School record categories use the same age × category pair.
- **Per-entry override.** A carnival administrator may override an individual entry's category, with a required reason. The override (`CategoryOverride`, actor, time, reason) sits beside the snapshot: the effective category is `override ?? snapshot`. Every override is audited (`entry.category-override`).
- **Overrides never rewrite the snapshot.** The roster snapshot and the entry's snapshot category are immutable event-time context.
- **Changing a division's own category** is an audited administration change, and is locked once the division has entries.

Eligibility and the override in the current model: [data model § category overrides](../architecture/data-model.md#category-overrides).

## Consequences

- A student can be placed in the right division without policy-sensitive data entering the system; the imported value is a default, not a lock.
- Anything about a contest (placings, points, reports, records) groups by the division's category, never the entry's effective category ([ADR-0023](0023-open-category-eligibility-and-one-entry-per-event.md)).
