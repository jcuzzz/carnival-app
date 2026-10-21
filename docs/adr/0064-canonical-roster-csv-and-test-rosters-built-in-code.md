# ADR-0064: The roster import reads a canonical CSV, and test rosters are built in code

- **Status:** Proposed. Partly supersedes [ADR-0033](0033-the-real-shape-fixture-is-generated.md): its §1 (a Python generator writes committed whole-school fixtures) is replaced by §4–§5 below; its §2–§5 (the sanitiser's limits and the explicit `.gitignore`) stand.

## Context

The README promises a roster import from "a simple CSV", but the importer reads a CASES21 ST export, the student system of Victorian government schools. It looks columns up by their CASES21 names (`STKEY`, `BIRTHDATE`, `SCHOOL_YEAR`…) and ignores the mapping file's `maps_to`, so a school whose system writes different headers cannot import at all, and renaming a mapping key passes the column check but rejects every row. The enrolment statuses (`ACTV`, `LEFT`…), the gender values and the 7–12 year range are constants, so a primary school, or any system that writes `Male` or `Current`, is shut out or silently misread: today any gender value other than `M` or `F` becomes the open category `S`.

The fixtures follow the importer. ADR-0033 commits two generated CSVs: a 5,198-row whole-school history and a 120-row file carrying all 227 CASES21 columns, plus a header file the generator reads. Their job is coverage of an awkward real shape, but they make the repository ship a CASES21 export format and a large data file for what a test could build, and every generator change rewrites a committed file. ADR-0033 rejected generating at test time only because it would make `dotnet test` depend on Python.

## Decision

1. **The import has a canonical format, defined by the platform.** Its fields are `student_id`, `surname`, `first_name`, `preferred_name`, `date_of_birth`, `gender`, `house`, `year_level`, `home_group` and `status`. `preferred_name`, `home_group` and `status` are optional. `date_of_birth` is written `YYYY-MM-DD` in the template; the parser keeps accepting the other encodings it reads today. A school that can produce this file needs no mapping at all.
2. **The importer reads fields, never source column names.** A mapping names, for each field, the source column that supplies it. The canonical format is the identity mapping and the default. Each student system is a **preset**: a mapping file shipped beside the default, with CASES21 the first. Row issue codes name fields (`MISSING_STUDENT_ID`, not `MISSING_STKEY`), so messages read the same for every school.
3. **School-specific values are configuration.** The mapping lists:
   - the source values that mean active and inactive; a file without a `status` column is a file of active students;
   - the source values for each competition category (`M`, `F`, `S`). A value not listed is a row error, never a silent `S`;
   - the year-level range, and the source values (such as `Prep` or `F`) that stand for a year level.

   A value outside these lists rejects its row with an issue that names the value and the mapping, so the fix is in configuration, not code.
4. **Test rosters are built in code.** A seeded builder in the API test project produces rosters in memory, in the canonical columns or in a preset's columns plus unmapped noise. It reproduces the real-shape properties ADR-0033 §1 listed (a whole-school history of mostly departed students, legacy house codes on departed rows only, blank houses including one active row, title and name variety, home-group oddities) as builder options, and tests assert against the options they built with, not against a pinned key or row count in a file. No whole-school fixture is committed.
5. **One small sample roster is committed**, in the canonical format: a few dozen invented, active students. The development seed, the Linux runbook's upload step and the screenshot run use it, and it is the template a school copies. It stays wholly invented (ADR-0033's rule that sanitiser output is never committed is unchanged).

How the import works is described in [Roster import](../architecture/imports.md).

## Consequences

- Any school whose student system can export a CSV can import, by producing the canonical file or by writing a mapping, without a code change, as `AGENTS.md` requires of school-specific rules.
- The CASES21 preset must keep behaving exactly as the import does today; a preset test builds a CASES21-shaped roster and checks the same outcomes.
- The issue codes change. Staged batches are purged within 7 days (ADR-0062), so no stored batch outlives the change by long; the review screen and any client matching on codes move with it.
- The two fixture CSVs, the CASES21 header file and `scripts/generate_synthetic_roster.py` are removed; the API tests no longer copy roster CSVs into their output folder. Changing the shape of test data becomes a code change reviewed with the tests that use it.
- The sanitiser still knows only CASES21's columns. Making it read the allowed columns from a mapping is follow-up work, needed only when a school with another system wants to inspect a real export.
- A file of current students only, with no `status` column, never deactivates a departed student: unmatched students are listed and left alone, as today. Whether an import can declare itself complete and deactivate the unmatched is a separate decision.

## Rejected alternatives

- **Keep CASES21 as the only format and tell other schools to reshape their export to it.** It makes a 227-column state-specific schema the public interface, and the values problem remains.
- **Keep committing fixtures, only smaller.** A small file loses the whole-school coverage ADR-0033 kept for good reason, and a large one is what this replaces.
- **Generate fixtures at test time with the Python script.** The test suite would gain a second language runtime; a C# builder runs inside `dotnet test` and lives next to the assertions.
- **Detect the student system from the file's headers.** Guessing a format on upload makes an import's meaning depend on which columns happen to be present; the mapping is chosen by configuration.
