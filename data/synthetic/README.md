# Synthetic data

All fixtures in this folder are generated, fictional data only — every row is invented by
`scripts/generate_synthetic_roster.py`. No file here is derived from a real export, not even a
de-identified one (ADR-0033). Real student exports must never be placed here or anywhere in this
repository.

| File | Shape | Regenerate with |
| --- | --- | --- |
| `ST_CLEAN.csv` | Narrow 12-column ST export, whole-school history (5,198 rows, ~17% active, legacy house codes on departed students) | `py scripts/generate_synthetic_roster.py --st-export` |
| `ST_SYNTH.csv` | Wide 227-column raw CASES21 shape with deliberate validation edge cases (120 rows) | `py scripts/generate_synthetic_roster.py` |
| `cases21_st_header.txt` | Column header only, no data | — |

Both generators use a fixed seed, so regeneration is byte-stable; the API tests pin counts and one
STKEY from `ST_CLEAN.csv`, so re-run `cd api && dotnet test` after changing the generator.

When sample export files are provided for shape analysis, place them in `data/raw/` (git-ignored)
and sanitise them with `scripts/sanitize_export.py`. Sanitiser output stays in `data/raw/` or
outside the repo — it is never committed and never becomes a fixture here.
