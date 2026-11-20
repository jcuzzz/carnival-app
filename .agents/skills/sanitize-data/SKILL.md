---
name: sanitize-data
description: Handle any student-data export (CASES21 or similar) safely — de-identify with scripts/sanitize_export.py before use. Use whenever the user mentions a real export, a file in data/raw/, importing student data, or asks to inspect/test against roster data that is not in data/synthetic/.
---

# Sanitising student-data exports

## Non-negotiables

1. **Never read, list, cat, or paste content from `data/raw/`** into the session. Assume anything there is real student data.
2. If the user pastes or attaches something that looks like a real export (real names, STKEY codes, birthdates, contact/medical columns): **stop**, do not process or echo it, and tell them to run the sanitiser first.
3. Only sanitised (de-identified) output or `data/synthetic/` fixtures may be used in this repo or in AI sessions.
4. **Sanitised output is never committed** (ADR-0033). De-identifying names does not de-identify the row: birth year, house, home group and status all survive. Committed fixtures come from the generator, not from the sanitiser.

## The tool

`scripts/sanitize_export.py` (run with `python`; `py` on Windows). The user runs it **outside the agentic workflow** on the raw file. Both modes refuse to write anywhere in the repo except `data/raw/` (git-ignored); writing outside the repo is also fine.

- **Default (de-identify)** — for local inspection and AI-session use: keeps only approved import columns, pseudonymises STKEY with a random per-run salt (one-way, not stable across runs), fakes names, truncates BIRTHDATE to 1 January of birth year, drops everything else (medical, family, contact, welfare — and TITLE/SECOND_NAME, which the import mapping never reads).
- **`--minimise-only`** — keeps real STKEY/names for authorised local import-pipeline testing. Output MUST stay in `data/raw/` and MUST NOT enter an AI session.

Suggest the command; do not run it on raw files yourself unless the user explicitly directs it and the output path is safe.

## After sanitising

- Test fixtures are **generated, never sanitised**: `python scripts/generate_synthetic_roster.py --st-export` writes the real-shape `data/synthetic/ST_CLEAN.csv`; without the flag it writes the wide `ST_SYNTH.csv`. If a real export reveals a shape the pipeline mishandles, reproduce that shape in the generator — do not commit the sanitised file.
- Schema/column questions: check `config/import-mapping.example.json` rather than opening exports.
- Data-minimisation rules (no addresses, parent info, health data, full birth dates) apply to schemas and fixtures too — see the AGENTS.md hard rules and proposal §11.
