# ADR-0064: The roster import reads generic input data, and test rosters are built in code

- **Status:** Accepted (2026-10). Partly supersedes [ADR-0033](0033-the-real-shape-fixture-is-generated.md): its §1 (a Python generator writes committed whole-school fixtures) is replaced by §3 below; its §2–§5 stand.

## Context

The roster import is built around one student system's export. Column names and the values for status, gender and year level are fixed in code, so a school whose system writes anything else cannot import without a code change. The test fixtures follow the same export and are large committed files.

## Decision

1. **The import reads a generic roster.** The platform defines the fields it needs (student ID, names, date of birth, gender, house, year level, home group, status) and the importer works on those fields only, never on a source system's column names.
2. **The mapping file connects a school's export to those fields.** It names the source column for each field and lists the school-specific values: which statuses mean active, which gender values map to each competition category, and the year levels in use. A value the mapping does not list rejects the row with an issue that says so. A default mapping ships for the generic template, and CASES21 ships as a preset mapping.
3. **Test rosters are built in code.** A seeded builder in the API test project produces rosters in memory, covering the awkward shapes the old fixtures covered. One small invented sample roster is committed as the template a school copies.

How the import works is described in [Roster import](../architecture/imports.md).

## Consequences

- A school with any student system can import by editing configuration, not code.
- The CASES21 preset must behave as the import does today.
- Row issue codes name generic fields, so the review screen changes with them.
- The committed fixture CSVs and the Python generator are removed.

## Rejected alternatives

- **Keep CASES21 as the only format.** Every other school would have to reshape its export to a state-specific schema.
- **Detect the format from the file's headers.** The meaning of an import would depend on which columns happen to be present.
