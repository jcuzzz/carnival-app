# ADR-0050: Reconciling a changed event template onto an existing catalogue

- **Status:** Accepted (2026-09). Amended by [ADR-0054](0054-setting-up-a-carnival.md) §2: the template list is open to `carnivals:administer`; preview and apply stay `school:administer`.

## Context

`POST /carnivals/{id}/apply-template` matches catalogue definitions by `Code` across the whole catalogue and **never overwrites an existing definition**, deliberately, because that row may have been edited in the app (ADR-0022). The consequence: a corrected template never reaches a school whose catalogue was already seeded, and creating a new carnival does not deliver it either, because the new carnival reuses the rows already there. Template changes (a relay activated, discipline tags added, events renamed category-first) had to be applied by hand-run SQL or one `PUT` per definition.

Templates are data, not code ([ADR-0007](0007-configuration-and-auth-modes.md)): a file in `./config/event-templates`, read from disk. This is the third slice of the in-app admin area, after [ADR-0045](0045-school-settings-in-app.md) and [ADR-0048](0048-in-app-role-assignment.md).

## Decision

1. **A preview-then-apply pair on the template key.** `GET /api/v1/admin/event-templates/{templateId}/reconcile` previews; `POST` on the same path applies. Each code is `Unchanged`, `Update` (field by field, prior and new, plus any guard), `Missing` (would be created), or `CatalogueOnly` (in the catalogue, not the template). **Reconcile never deletes and never deactivates a catalogue-only row:** definitions are history referenced by finalised carnivals and the record book, and retirement stays an explicit soft-deactivate.
2. **The apply carries the preview's hash.** The hash covers every planned change, whether each field is guarded, every value a create would write, and the template file's content hash. Planning, the hash check and the writes run in one serializable transaction; a mismatch, or a code created concurrently, is `409 TEMPLATE_RECONCILE_STALE`. A preview from before another administrator's edit, a new result, or a swapped template file cannot be applied blind.
3. **Reconcile creates catalogue rows but schedules nothing.** No carnival event, slot or division is created, moved or removed in any carnival.
4. **Field policy.** The template owns and overwrites: name, sport, measure type, attempt count, ladder defaults, attempts per height, units, active flag, tags, format, team size, legs and scoring weight. Never touched: `Code` (the match key; a code change is a new definition plus a deactivation), id, creation time, and eligibility, which no template expresses. Record categories are template-derived and reconciled only while no school record cites the definition. JSON fields compare canonically, so whitespace is not a change.
5. **Guards refuse rather than apply** (`422 TEMPLATE_RECONCILE_UNSAFE`, naming code and field):
   - measure type, units and attempt count, once any result exists (a stored value does not restate its unit or its series length);
   - format and team size, once any entry exists; legs, once team entries exist;
   - scoring weight, once a finalised carnival scores the definition (moving it would move published standings);
   - record categories, once the record book cites the definition.

   Renames, tag changes and activation flips are never guarded. Other refusals: `404 TEMPLATE_NOT_FOUND`, `422 TEMPLATE_INVALID`, `422 REASON_REQUIRED`.
6. **Preview and apply are `school:administer`** (SystemAdministrator only): definitions are shared by every carnival, past and present, so a bulk overwrite is a school-level act. Carnival administrators keep the per-definition editor, and can list templates to apply one to a carnival.
7. **Audit:** one `event-definition.update` or `.create` row per changed definition, in the editor's own projection so a definition's history reads as one stream, plus one `event-template.reconcile` summary row with the template, hash, counts, excluded codes and a **required reason**.
8. **Admin edits are shown, not guessed.** Each updated row shows whether the definition was edited in the app (the reconciler's own audit rows excluded). The apply takes the whole preview minus an optional `excludeCodes` list; a guarded row is excluded whole. A per-field merge can come later without changing the request shape.
9. **It is a screen, not a runbook command:** School → Event templates, preview table, apply with a reason, a result summary. Online only.
10. **`apply-template` is unchanged.** The order of operations is **deploy the template file → reconcile → create the carnival**.

How templates, apply and reconcile work: [configuration § reconciling a changed template](../admin/configuration.md#reconciling-a-changed-template).

## Consequences

- Template corrections reach existing schools without SQL, audited, with a reason.
- A reconcile mid-carnival is safe for results (everything keys on ids), but a device shows the event names it last fetched until it refetches. Reconcile between carnivals where possible.
- Reconciling one sport lists the other sport's definitions as catalogue-only: true, and left alone.
- No migration: every field already exists on the definition.

## Rejected alternatives

- **Make `apply-template` overwrite:** it is the idempotent, frequently re-run action; overwriting there would destroy admin edits silently, with no preview or reason.
- **A migration per template change:** puts school configuration in the code path.
- **Row-by-row editing only:** a full athletics template is thirty-plus definitions.
- **Auto-reconcile at startup when a file changes:** a silent overwrite with nobody present to answer the guards.
