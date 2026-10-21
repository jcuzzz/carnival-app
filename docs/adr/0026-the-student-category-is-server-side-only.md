# ADR-0026: An Open division is open to everyone, and a student's category never leaves the server

- **Status:** Accepted (2026-08). Corrects [ADR-0023](0023-open-category-eligibility-and-one-entry-per-event.md) §1 (who an Open division admits) and narrows [ADR-0022](0022-event-and-division-management.md) §1 (Boys/Girls/Open names divisions, never students).

## Context

Every imported student carries a competition category, `M`, `F` or `S`, derived from the roster's gender field: M→M, F→F, anything else→S. In practice `S` marks gender-diverse students, and because the mapping is total and lossless the stored category is the source gender value under another name.

[ADR-0003](0003-event-division-and-category-overrides.md) requires the platform to group students for competition without becoming a gender register. Several things together undid that:

- An Open (S) division refused M and F students, so its entry list was precisely the gender-diverse students of its age band, by name.
- The per-student category was sent to every signed-in role, including Viewer, in the eligible-student and entry payloads, rendered beside student names on the add-entry panel, and held in device caches that survived sign-out.
- Even without labels, browsing divisions derived it: an M student appeared only in Boys, an F student only in Girls, an S student in all three.

At this school an Open event is for anyone who wants to enter, so the refusal was a mistake rather than a choice.

## Decision

### 1. An Open division admits every student

An `S` division refuses nobody on the category axis. Once it admits everyone, its entry list stops being a roster of gender-diverse students. The one-entry-per-definition-per-tier rule is unchanged: a student may enter Open or Boys, not both.

### 2. A student's category never leaves the server

The category is used only for server-side eligibility. It is not sent to a client, not rendered beside a name, and not cached on a device.

- The eligible-student and entry payloads carry no category fields. The server already filters the eligible list for the division being entered.
- The add-entry row shows house, year level and age group. The age group stays: it is not derived from gender and is the one thing year level does not tell you in an all-ages division.
- Entries carry `overrideApplied` as a bare boolean, because ADR-0003 requires an applied override to be visible.
- Boys/Girls/Open remain the names of **divisions** everywhere; no per-student category label may be reintroduced.
- `CATEGORY_INELIGIBLE`'s message names only the division's requirement, never the student's category.

### 3. A device keeps a roster only while someone is signed in

Sign-out and handover clear the cached eligible-student lists and the service worker's cached API reads, and cached rosters are pruned on a timer. A shared iPad must not hold the previous official's roster; this is student names and houses regardless of category.

### 4. The import review is a documented exception

The import reconciliation table shows the raw `M`/`F`/`S` beside each staged row. Checking that the gender mapping was read correctly is the one task that needs the value, and the screen is system-administrator-only. It is recorded as a deliberate exception so it is not mistaken for an oversight.

### Accepted residuals

- **The refusal still permits inference.** With categories `{M, F, S}`, a refusal from a Boys division implies F. Collapsing the code into a generic "not eligible" would take the reason away from the administrator trying to fix a real problem. Mitigated: it is POST-only, needs a recording role, and is reached only by posting a student the eligible list did not offer.
- **`overrideApplied` is a one-bit signal on a gendered division**, since an override there matters only for a student who would otherwise be refused. It stays, because ADR-0003 requires an applied override to be visible.
- **Browsing divisions derives M-or-F**, which is the axis the school's own program already publishes in its division names.

## Consequences

- Outside the system administrator's import review, the source gender value cannot be reconstructed from a browser, a device cache or the Open-division entry list.
- An Open school record would imply nothing about the holder's gender; whether Open divisions keep records at all is an ordinary records question (they do not: [ADR-0023](0023-open-category-eligibility-and-one-entry-per-event.md) §6).
- Only what is sent changes, not what is stored.
- A category that is never sent cannot leak from a cache, a screenshot or a shared device, and no future screen can show it by accident. That asymmetry is why the target is "no client" rather than "administrators only".

## Rejected alternatives

- **Show the category to administrators only** — still reaches device caches and screens; the strictest option was chosen.
- **Remove `overrideApplied`, or restrict it to administrators** — breaches ADR-0003's visibility requirement for a one-bit gain.
- **Keep Open divisions S-only** — the entry list is the register.

See [data model: eligibility](../architecture/data-model.md#eligibility) and [roster import: age group and category](../architecture/imports.md#age-group-and-category).
