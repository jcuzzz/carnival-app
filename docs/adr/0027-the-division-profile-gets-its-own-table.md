# ADR-0027: The division profile gets its own table

- **Status:** Accepted (2026-08). Supersedes [ADR-0022](0022-event-and-division-management.md) §2's storage choice. The row's age axis is amended by [ADR-0058](0058-one-age-per-division.md): one age per row, no merged bands or labels.

## Context

A carnival's *division profile* — which ages × categories × tiers it contests — was stored as JSON inside `Carnival.SettingsJson`, beside the scoring rules and adjustment categories. That cost three things:

1. **A shared size ceiling.** Everything in `SettingsJson` competed for one 4,000-character column. A settings write that overflowed was refused, but a template seed that overflowed silently wrote nothing, leaving the carnival reading "profile not configured" with no error.
2. **No SQL answer** to "which carnivals contest a 17?" without loading every blob.
3. **No reference** between the profile and the divisions generated from it.

## Decision

### 1. The profile is a table

`CarnivalDivision` holds one row per contested combination (age × category × tier) per carnival, unique on (carnival, age, category, tier) and indexed on age. The profile is the full cross product of its three axes, written in age → category → tier order and read back as the rows' distinct values in that order, so the API still exchanges it as three axes. A row set that is not a cross product cannot be written through the API.

`SettingsJson` keeps what it is for: scoring rules, adjustment categories and venue settings.

### 2. One enumeration

The age × category × tier product is enumerated in one place (`DivisionGenerator.Matrix`), used both to build profile rows and when a template generates divisions, so a template apply and the profile cannot drift apart.

### 3. Seeding never overwrites a usable profile, and repairs an unusable one

Applying a template seeds the profile only when the carnival has no usable one. Rows that do not form a valid profile read as "not configured" and are replaced by the template's in the same transaction; a valid profile is never overwritten. Two concurrent applies both passing the seed check end with one seeded profile: the loser catches the unique-index violation and saves without seeding.

### 4. The migration is reversible and lossless in profile

Existing blobs are read out into rows; the down migration rebuilds the blob from the rows (canonicalised, not byte-for-byte) and refuses rather than truncates if the result would not fit the column. A blob that cannot be read as a profile migrates to nothing usable, which the next template apply repairs.

### 5. The legacy key is carried, then shed under pressure

The old `Divisions` key in an existing carnival's `SettingsJson` is preserved verbatim through settings writes and read by nothing, so a code-only rollback still finds a profile. Because nothing can shrink it, it is dropped from that carnival's blob only when a settings write would otherwise exceed the ceiling.

### Not built

- **No `CarnivalEvent → CarnivalDivision` reference.** The generator accepts divisions the profile never named, and every settings save replaces the rows wholesale with fresh ids, so a stored reference would be nullable and would dangle after the first edit. A real reference needs durable row identity first, which is a separate decision. Cost 3 therefore remains.
- The "which carnivals contest a 17" query: the index exists, no endpoint uses it.

## Consequences

- The silent-overflow seed defect is gone with its cause: the profile has its own storage and no shared ceiling.
- The profile is queryable by SQL.
- Existing carnivals' blobs shrink only when the legacy key is shed; new carnivals never write it.
- A code-only rollback after the legacy key was shed reads that carnival's profile as "not configured" until re-entered; a schema rollback loses nothing.

## Rejected alternatives

- **Fix the silent seed overflow in place** — work thrown away once the profile leaves the shared column.
- **Separate ordinal columns per axis** — buy nothing a single row position does not.

See [data model: division profile](../architecture/data-model.md#division-profile).
