# Carnival App — guide for contributors and coding agents

Carnival App runs school swimming and athletics carnivals: roster import, live result entry on any device (a PWA whose outbox rides out connection dropouts), verification, house points, and optional school records. ASP.NET Core 10 API + React 19 PWA, PostgreSQL, one isolated deployment per school.

This file is the canonical instruction set for humans and AI coding agents alike. `CLAUDE.md` points here.

**Start by reading `docs/HANDOFF.md`** (state of play, active work) and skim the ADR index in `docs/adr/`. AI-work conventions: `docs/development/agent-practices.md`.

## Hard rules

- **Never commit real student data.** Only synthetic data in `data/synthetic/`. If a file looks like a real export, stop and flag it; de-identify first with `scripts/sanitize_export.py` (the `sanitize-data` skill). Never read or list `data/raw/` (git-ignored).
- **Data minimisation:** no addresses, contact details, health data or stored birth dates. The date of birth is read at import to derive the age group and is not stored.
- The school's own student system stays the system of record; this platform imports controlled snapshots only.
- All authorisation, validation and audit is server-side. UI hiding is not access control.
- **School-specific rules (houses, events, scoring, branding) are configuration or data, never code forks.** If a school needs a code change, the missing configuration point is the bug.

## Commands

- API: `cd api && dotnet build` / `dotnet test`; migrations: `dotnet ef migrations add <Name> --context CarnivalAppDbContext` in `api/src/CarnivalApp.Api`
- App: `cd app && npm ci && npm run build` (Vite); dev server `npm run dev`; tests `npm test` (Vitest)
- Synthetic data: `python scripts/generate_synthetic_roster.py`
- Testcontainers tests skip when Docker is unavailable.
- Machine-specific notes (paths, interpreters) go in a git-ignored `AGENTS.local.md`, not here.

## Documentation

**The docs tree (`docs/README.md`) is the primary documentation: it describes the system as it is today.** Every change that alters behaviour, structure, configuration or operation updates the relevant page in the same change: `docs/architecture/` for how it works, `docs/admin/` for running it, `docs/guide/` for using it. A change without its docs update is incomplete.

ADRs are secondary and short. Write one only for a significant design decision: context, decision, consequences, rejected alternatives, about a page. **No implementation notes, ruling transcripts, review logs or dated amendment histories in ADRs**: implementation detail belongs in the architecture page, history belongs in git. If a later decision changes an earlier one, write a new ADR that supersedes it; do not append to the old one.

## Conventions

- Significant design decisions → `docs/adr/NNNN-title.md` (the `new-adr` skill), before the behaviour changes; then the docs tree.
- **Code comments: minimal and concise.** Comment only a non-obvious constraint or a why the code cannot express, in one or two lines. Never narrate the code or restate ADR history in comments. Reviewers flag comment bloat like any other defect.
- Research → `docs/research/`. Governance templates → `docs/governance/`.
- Result lifecycle: draft/queued → provisional → verified → final → ratified record. Offline submissions are idempotent (client-generated UUIDv7 keys, ADR-0002).
- Change flow: document the decision → implement → static validation → runtime smoke test → docs sync.
- **Before any UI change, read `docs/development/design-system.md`** (ADR-0040): tokens, component vocabulary, icon/type/44px rules. No raw hex or size literals outside `app/src/tokens.css`.
- **After any UI-affecting change**, run `cd app && npm run screenshots` and review the PNGs in `app/ui-review/` (open `app/ui-review/index.html`) before committing. Captures use synthetic data only.
- **Before ending a work session**, update `docs/HANDOFF.md` (the `handoff-update` skill).
- Commits carry no AI attribution (no `Co-Authored-By` lines).

## Contributions

Outside pull requests are not accepted until a contributor licence agreement is adopted; see `CONTRIBUTING.md`.
