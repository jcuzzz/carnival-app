# ADR-0045: School settings administered in-app (admin GUI, slice 1)

- **Status:** Accepted (2026-09). The settings screen's place in the School area is set by [ADR-0055](0055-the-school-area.md).

## Context

Every school-specific value was file or environment configuration: school name, timezone, brand colour (ADR-0040), houses and house colours (ADR-0044). Changing one meant editing `.env` or a mounted file on the host and recreating the container, and a house code with a space cannot even be an environment-variable name. A `SchoolConfiguration` row had existed since the initial schema for exactly this ("configuration changes are data, never code forks, and auditable"), but nothing read it.

This is the first slice of an in-app admin area; later slices are role assignment ([ADR-0048](0048-in-app-role-assignment.md)) and template reconciliation ([ADR-0050](0050-reconcile-template.md)), and they follow this pattern.

## Decision

1. **The `SchoolConfiguration` row is the source of truth for school identity:** display name, brand colour, and the houses (code, display name, colour). The `School:*` configuration is the **first-start seed only**: when no row exists, one is created from configuration and validated as at startup (a bad brand colour stops that first start; a bad house colour degrades to text-only with a warning). After that the configuration for those values is ignored, and the startup log names the source used. Timezone, identity, connection strings, import mapping and template paths stay file-backed; the timezone is read from configuration and is not editable in the app.
2. **Two endpoints, one permission.** `GET` and `PUT /api/v1/school/settings` (full replacement) under `school:administer`, **SystemAdministrator only**: school-level configuration is the platform owner's act, not a carnival administrator's. Each house on the way out carries `inUse`, computed by the same query the PUT's removal guard uses, so the form can disable what the server would refuse. The PUT carries the `updatedAtUtc` it was rendered from (an EF concurrency token) and is refused `409 SETTINGS_STALE` when the row has moved, so two administrators cannot silently overwrite each other.
3. **Validation is server-side and refuses rather than degrades**, because a person is present to fix it: `422` naming the first failing field. Brand colour: parseable, and black or white reaches 4.5:1. House colour: parseable and not another house's (no contrast minimum, ADR-0044). House codes: non-empty, unique ignoring case, stored upper-case. **A house code used by any roster snapshot, entry, relay leg or house-points adjustment cannot be removed or renamed** (`422 HOUSE_IN_USE`); adding houses is always allowed.
4. **Every change is audited** as `school.settings`, with prior and new values and the session actor.
5. **Consumers read the row through one cached provider** (`SchoolSettingsProvider`, invalidated on a successful save; the deployment is single-instance per ADR-0001): `GET /branding`, `GET /school/houses`, roster-import house validation and `/version`'s school name.
6. **Client: a School settings screen**, shown to SystemAdministrators (a nav entry, not access control): school name, brand colour with a live preview, houses as rows with remove disabled where in use. A successful save refetches branding and house marks so the chrome repaints without a reload; a stale save says another administrator saved first and offers a reload; typed edits are never discarded by a background refetch. Saving needs a connection.
7. **Per-carnival branding is out of scope.**

Field-level rules: [configuration § school settings](../admin/configuration.md#school-settings).

## Consequences

- No migration: the existing columns hold the values.
- To change a colour or a house on a running instance, use the screen; editing `.env` after the first start has no effect.
- Startup must read the row, so an unreachable or unmigrated database stops the API with the remedy in the message.

## Rejected alternatives

- **Keep it file-backed and document the mount:** house codes with spaces never fitted the environment form.
- **Configuration overrides the row after first start:** two sources of truth for one value, with the losing one silent.
- **Degrade on a bad colour in the PUT, as startup does:** a form has someone to answer; refusing with the reason is the honest response.
- **Per-field endpoints:** the values are one form and one audit record.
- **Allow removing a house that has rows:** orphans house codes on snapshots and entries.
