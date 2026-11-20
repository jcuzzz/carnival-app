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
- Planned changes: `openspec list`, `openspec show <change>`, `openspec validate <change>` (CLI: `npm install -g @fission-ai/openspec`, Node 22.12+).
- Machine-specific notes (paths, interpreters) go in a git-ignored `AGENTS.local.md`, not here.

## Documentation

**The docs tree (`docs/README.md`) is the primary documentation: it describes the system as it is today.** Every change that alters behaviour, structure, configuration or operation updates the relevant page in the same change: `docs/architecture/` for how it works, `docs/admin/` for running it, `docs/guide/` for using it. A change without its docs update is incomplete.

ADRs are secondary and short. Write one only for a significant design decision: context, decision, consequences, rejected alternatives, about a page. **No implementation notes, ruling transcripts, review logs or dated amendment histories in ADRs**: implementation detail belongs in the architecture page, history belongs in git. If a later decision changes an earlier one, write a new ADR that supersedes it; do not append to the old one.

`openspec/` plans the work: `openspec/changes/<name>/` holds a change in flight (proposal, spec deltas, design, tasks) and moves to `changes/archive/` when done; `openspec/specs/` holds only testable requirements for the areas changes have touched. It does not replace the docs tree or ADRs: specs link the docs page that explains a behaviour, and `design.md` links the ADR.

## Writing

New and changed text follows the **simple-english** skill (`.agents/skills/simple-english/`, plain English after ASD-STE100): replies, docs, specs, OpenSpec artifacts, commit messages, pull request text, code comments, and log and error messages.

- Apply it to text you write or change. Do not rewrite untouched text only for style. ADRs stay as they were written, because they are decision records.
- Keep the Australian spelling of the project (authorisation, minimisation, colour). It overrides the skill's American spelling rule.
- In the docs tree, the existing page structure and formatting stay (headings, tables, bold where a term is defined). The skill decides sentences and word choice.

## Conventions

- Significant design decisions → `docs/adr/NNNN-title.md` (the `new-adr` skill), before the behaviour changes; then the docs tree.
- **Code comments: minimal and concise.** Comment only a non-obvious constraint or a why the code cannot express, in one or two lines. Never narrate the code or restate ADR history in comments. Reviewers flag comment bloat like any other defect.
- Research → `docs/research/`. Governance templates → `docs/governance/`.
- Result lifecycle: draft/queued → provisional → verified → final → ratified record. Offline submissions are idempotent (client-generated UUIDv7 keys, ADR-0002).
- Change flow: see Workflow below.
- **Before any UI change, read `docs/development/design-system.md`** (ADR-0040): tokens, component vocabulary, icon/type/44px rules. No raw hex or size literals outside `app/src/tokens.css`.
- **After any UI-affecting change**, run `cd app && npm run screenshots` and review the PNGs in `app/ui-review/` (open `app/ui-review/index.html`) before committing. Captures use synthetic data only.
- **Before ending a work session**, update `docs/HANDOFF.md` (the `handoff-update` skill).
- Commits carry no AI attribution (no `Co-Authored-By` lines).
- Commit messages: a subject of 72 characters or less, a blank line, then the details in plain sentences. Reason: `git log --oneline` stays readable.

## Workflow

The method behind these rules is in `docs/development/agent-practices.md`.

- **Change flow:** stress-test the plan (`/grill-me`, the `grilling` skill) → plan non-trivial work as an OpenSpec change (`openspec-propose`) → ADR if a significant decision → implement stage by stage (`openspec-apply-change`, the `staged-implementation` skill) → static validation → runtime smoke test → docs sync → archive (`openspec-archive-change`). Small fixes skip OpenSpec. Project rules for the artifacts live in `openspec/config.yaml`.
- **One branch per change.** Before `openspec-propose` writes artifacts, ask the maintainer for the branch name, and create the branch from `main`. Use a separate worktree (`git worktree add ../carnival-app-<name> <branch>`) only when two changes run at the same time, or for the lanes of the `parallel-lanes` skill. Reason: each worktree needs its own `npm ci` and build.
- **One review per stage.** After each stage, one independent reviewer agent checks the stage's diff. Fix real issues in one follow-up commit. Batch low-severity findings into that commit or add them to `docs/BACKLOG.md`. Do not review a fix commit again unless it changes logic.
- **Pull requests:** push the feature branch and open the pull request with the `pr-description` skill. Give its full URL in the report. Merging is the maintainer's decision.
- **Archive before the merge.** Archive the OpenSpec change on the feature branch as its last commit, so that the merged pull request already carries the updated `openspec/specs/`. Never archive in a follow-up branch.
- **Agent files and skills:** before you add or change an `AGENTS.md`, `CLAUDE.md` or skill, read the `agent-instructions` skill. Skills live in `.agents/skills/`, with stubs in `.claude/skills/`.
- Deferred work goes to `docs/BACKLOG.md`.

## Model tiers

Pick the model for each task and each subagent by what the task needs. A larger model costs more per token, but on hard multi-step work it often costs less per finished task, because it needs fewer steps and less rework. A smaller model does routine, precisely described work as well, faster and cheaper. Judge by cost per finished task, including the fix-ups after review. `CLAUDE.md` maps the tiers to Claude Code models.

| Tier | Use it for |
|---|---|
| Frontier (the most capable model) | Ambiguous root causes (outbox and concurrency bugs), data-model and architecture decisions that are hard to undo |
| Strong | The main session (planning, grilling, design, review decisions), the serial part of an OpenSpec change (shared contracts), code review, scoring, result-lifecycle and authorisation work |
| Standard | Parallel lanes whose tasks are precise and whose contract is committed, docs for finished code, pull request comments, small doc and link fixes, web research for a decision |
| Small | Finding code and files, running builds, tests and screenshots and collecting the results |

- Every subagent gets an explicit model. A subagent without one runs on the main session's model.
- Change the effort before you change the model. Use the default effort for most work. Use high effort where a mistake is hard to see and expensive later: shared contracts, scoring, the result lifecycle, authorisation and audit, and review. Raise the effort when an agent skipped a file or the tests. Choose a larger model when the agent had the right context and was still wrong.
- This table is a starting point. When a standard-tier task needs more fix-ups after review than strong-tier tasks did, move that kind of work up a tier, and note it here.

## Working preferences

The maintainer's preferences for how agents work in this repository. When you notice a new preference, add it here and tell the maintainer.

- **The main session coordinates.** Hand each multi-step task (an implementation stage, a fix pass, a screenshot review, a walk-through, a commit or deploy run) to a subagent with a self-contained brief and a short report. Do only small edits (a few lines in one file) and decisions in the main session. Reason: a large main context lowers quality. Use subagents for large work on disjoint files, not for every small fix: 50 or more agent runs on one feature cost too much time and too many tokens.
- **Agree the scope and the definition of done first**, for example "the walk-through screenshots look right, one review round, nits to the backlog".
- **While iterating, run only the relevant tests.** Run one full suite per stage, never several full suites at the same time.
- **Mockups before UI.** Before you build UI, show mockups or screenshots of the current screens, and let the maintainer decide from what they see.
- **Check UX on a fresh install.** When a change alters what a user sees, walk the real screens as a new user on a fresh install with the shipped defaults (`.env.example`), before you report it done. When you design a done-state, make sure that shipped defaults or seed data cannot satisfy it without the user acting. A finished state shows nothing rather than a "done" badge. Reason: a setup list once showed "School set up" ticked on a fresh install, because `.env.example` seeds a sample school name.

## Contributions

Outside pull requests are not accepted until a contributor licence agreement is adopted; see `CONTRIBUTING.md`.
