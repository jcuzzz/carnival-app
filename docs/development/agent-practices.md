# Working with AI agents

How this project's work is done with AI coding agents (Claude Code and similar). [`AGENTS.md`](../../AGENTS.md) holds the rules every agent loads; this page is the method behind them. The same rules apply to human contributors.

## Session shape

1. **Start:** read `AGENTS.md`, then `docs/HANDOFF.md`, then the docs-tree pages and ADRs the task touches. Do not re-derive a decision an accepted ADR already records.
2. **Plan before building.** Non-trivial work becomes an OpenSpec change (see below). A significant design decision is proposed as a short ADR with status `Proposed` (the `new-adr` skill); the maintainer accepts it in review of the change or pull request. Routine changes, fixes and UI tweaks need neither.
3. **Build, then update the docs tree in the same change**: `docs/architecture/`, `docs/admin/` or `docs/guide/`. A change without its docs update is incomplete.
4. **End:** run the `handoff-update` skill: docs-tree check, `docs/HANDOFF.md` current, backlog deltas recorded.

## Planning a change with OpenSpec

[OpenSpec](https://github.com/Fission-AI/OpenSpec) (MIT) keeps each change's plan in the repository, so a fresh session picks up a half-done change from its files rather than from a chat transcript.

1. **Grill (optional):** `/grill-me` interviews the maintainer in rounds of numbered questions, each with a recommended answer, until every decision the plan depends on is settled. The agent looks facts up itself; the decisions are the maintainer's.
2. **Propose:** the `openspec-propose` skill writes `openspec/changes/<name>/`: `proposal.md` (why, who needs it, out of scope), `specs/<capability>/spec.md` (requirement deltas: ADDED, MODIFIED, REMOVED), `design.md` (how; links the ADR and names the docs pages it updates) and `tasks.md`. It plans only; nothing is built until the maintainer has reviewed the artifacts. `openspec-explore` is a thinking step before a proposal; `openspec-update-change` revises one.
3. **Apply:** the `openspec-apply-change` skill works through `tasks.md`, ticking items as they are done. Every task list ends with the docs-tree update, screenshots and a fresh-install walk-through for UI, one review, the full gates and the handoff.
4. **Archive:** the `openspec-archive-change` skill merges the spec deltas into `openspec/specs/` (or `openspec-sync-specs` on its own) and moves the change to `openspec/changes/archive/YYYY-MM-DD-<name>/`.

`openspec/specs/` holds testable requirements (SHALL statements with scenarios), grouped by the capability names of the `docs/architecture/` pages; the docs tree still explains how things work. It grows only as changes touch an area, so there is no back-fill. The project's rules for every artifact (the docs-tree, ADR, mockup and review steps) live in `openspec/config.yaml`, which the OpenSpec skills read each time. `docs/HANDOFF.md` points at active changes rather than restating their tasks.

The `openspec` CLI is installed per machine (`npm install -g @fission-ai/openspec`, Node 22.12+). After upgrading it, run `openspec update` to regenerate the skills; they live in `.agents/skills/`, linked from `.claude/skills/`. Turn off its anonymous usage telemetry with `openspec config set telemetry.enabled false`.

Before building a new surface, check that a named user needs it. Asking costs minutes; building and retiring a speculative screen costs days.

## Briefing subagents

When work is split across parallel agents, each brief carries:

- **The shared rules**, written once in a common brief every agent reads first: hard rules, the comment rule, the docs rule, "never run prettier".
- **A disjoint file set.** Each agent edits only its assigned files, so parallel work never collides.
- **No git writes.** Agents may read history (`git log`, `git show`, `git diff`) but never commit, stage, stash, reset or check out. The coordinating session or the human reviews the combined diff and commits.
- **A required report**: files changed, claims not verified, suspected bugs with `file:line`.

If another session owns `docs/BACKLOG.md` or a source tree, record deltas in `docs/HANDOFF.md` instead of editing its files. Check `git status` before large edits.

## Review after build

Scoring, result-lifecycle, authorisation and audit work goes through layers: **build → adversarial reviewers → fix batch → verifier**. Reviewers get the change and the relevant rules, not the builder's reasoning, and look for defects rather than confirming the design. Run the full suites (`dotnet test`, `npm test`, `npm run test:e2e`) before the verifier signs off.

## Proving mechanical changes

A large refactor, rename or CSS-only change is proven neutral, not asserted:

- **Code:** compare the token stream before and after (whitespace and comments excluded). Equal streams mean behaviour cannot have changed; any difference is reviewed by hand.
- **UI:** run `SCREENSHOTS_FIXED=1 npm run screenshots` on the baseline and on the change, then diff the PNGs. The fixed mode pins the database, seed names, clock, server timestamps and motion (`app/e2e/fixed.ts`), so a neutral change is pixel-identical.
- **Both:** the full test suites pass unchanged.

## Code style

- **Comments:** minimal. Only a non-obvious constraint or a why the code cannot express, in one or two lines. No narration, no ADR, date or review citations, no block-comment history. Reviewers flag comment bloat like any other defect.
- **Never run prettier.** There is no formatter config; the code is hand-formatted at about 100 columns and a reformat buries the real diff.
- **UI:** read [the design system](design-system.md) first; no raw hex or size literals outside `app/src/tokens.css`.

## Data

- **Synthetic data only.** Fixtures come from `scripts/generate_synthetic_roster.py` into `data/synthetic/`.
- **Never open, list or echo `data/raw/`.** It is git-ignored because it may hold real exports, and real data must never enter an agent's context.
- Real exports are de-identified **outside** agent sessions with `scripts/sanitize_export.py` (the `sanitize-data` skill). Sanitised output is never committed.
- Data minimisation applies to code you write: no addresses, contact details, health data or stored birth dates in schemas, fixtures, tests or log statements.

## Screenshots

After a UI-affecting change, run `cd app && npm run screenshots` and review the captures in the local gallery (`app/ui-review/index.html`, git-ignored) before committing. Layout defects and stale data show up there that jsdom tests cannot see. Captures are reviewed locally; they are never published anywhere with real data in them.

## Agent permissions

Make the data rule mechanical in your local agent settings rather than advisory, for example in Claude Code's `.claude/settings.json`: deny `Read(./data/raw/**)` and `Read(./.env*)`, and allow the routine build and test commands to cut prompt fatigue. Agents do not write settings files themselves.

## In the code

- `AGENTS.md`: the rules every agent loads
- `.claude/skills/`: `new-adr`, `handoff-update`, `sanitize-data`; `grill-me` and `grilling` (adapted from mattpocock/skills, MIT); the `openspec-*` skills (generated, linked from `.agents/skills/`)
- `openspec/config.yaml`: project context and per-artifact rules for OpenSpec
- `app/e2e/fixed.ts`: pixel-reproducible screenshot mode
- `scripts/sanitize_export.py`, `scripts/generate_synthetic_roster.py`
