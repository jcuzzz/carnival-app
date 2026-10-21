# ADR-0033: The real-shape roster fixture is generated, never sanitised

- **Status:** Accepted (2026-09). §1 (committed fixtures written by a Python generator) is proposed to be superseded by [ADR-0064](0064-canonical-roster-csv-and-test-rosters-built-in-code.md), which builds test rosters in code; §2–§5 stand.

## Context

The whole-school roster fixture the import tests stage against (`data/synthetic/ST_CLEAN.csv`) was once produced by running `scripts/sanitize_export.py` over a real student-system export. The names were fake, but everything else on the row was real: birth year, gender, title, house (including legacy codes), home group, year level and enrolment status. Birth year, gender and home group together plausibly single out individuals in a 25-student home group, and the student-key pseudonym used a fixed salt, so anyone holding the real key list could re-link every row. The process worked as documented; the documentation was wrong. **De-identifying the names does not de-identify the row.**

The file's value was real, though. The long-term direction is a direct sync from the student system's export rather than hand-prepared CSVs, and it is the whole-school history that is awkward: decades of departed students, houses that no longer exist, blank fields, a year-12 exit cohort far larger than any enrolled year. A fixture of tidy, in-range, enrolled students would let the pipeline pass its tests and fail on the first real file.

## Decision

**Committed fixtures are generator output only. Sanitiser output is never committed.**

| Tool | Input | Output | May be committed |
|---|---|---|---|
| `scripts/generate_synthetic_roster.py` | nothing: invents every row | `data/synthetic/*.csv` | yes |
| `scripts/sanitize_export.py` | a real export | `data/raw/` or outside the repo | never |

1. **The real-shape properties are reproduced, not inherited.** `generate_synthetic_roster.py --st-export` emits the 12-column whole-school shape with the awkwardness rebuilt from invented data: thousands of departed students against a few hundred active ones, legacy house codes on departed rows only, blank houses on many departed rows and on exactly one active row (which must surface as an exception), title variety, blank second names, preferred names that differ, home groups correlated with year level plus oddities. A fixed seed makes regeneration byte-stable. The distributions model how schools look, not any student.
2. **The sanitiser writes nowhere committable.** Its output must land under `data/raw/` (git-ignored) or outside the repo. Its job is narrow: letting a person look at the shape of a real file without the identities.
3. **The sanitiser's pseudonym is one-way:** a random salt per run, stored nowhere, so two runs over the same file do not agree and a real key list cannot be rehashed to find a row.
4. **Title and second name are not in the sanitiser's allowed columns.** Nothing in the import path reads them, and title is gender-correlated. The generator still emits both, because a real file has them and the parser should meet them.
5. **`.gitignore` names the fixture files explicitly** rather than re-including every CSV under `data/synthetic/`, so a stray file dropped there stays ignored.

## Consequences

- `data/synthetic/` holds generated, fictional data only, and the fixture is regenerable with `py scripts/generate_synthetic_roster.py --st-export`. Changing the generator changes the fixture, so the API tests must be re-run in the same change.
- **Real-shape coverage has to be maintained deliberately.** If a real export reveals a shape the pipeline mishandles, describe that shape in the generator; never commit the file that revealed it.
- Sanitiser output cannot be handed straight to a test. That friction is intended.

## Rejected alternatives

- **Accept the residual risk because the repository was private.** It leaves the "never commit real student data" rule contradicted by the very file it was written for, and the alternative cost an afternoon.
- **Delete the fixture and test against the small tidy roster only.** Removes the risk and the coverage together.
- **Keep sanitising with a stronger salt.** Birth year, house, home group and status survive any pseudonymisation of the key.
- **Generate at test time.** Adds a Python dependency to `dotnet test`; the fixed seed already gives reproducibility.
