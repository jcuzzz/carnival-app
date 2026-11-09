# HANDOFF — state of play

> The first document a fresh session reads after `AGENTS.md`. Keep it current: update at the end of every work session (`handoff-update` skill). Terse pointers, not a diary.

## Now

- **Pre-release, public on GitHub** (`jcuzzz/carnival-app`, squashed initial commit 2026-10-21). Ported from the maintainer's private pilot repository: same behaviour, renamed, school details removed. The carnival workflow (import → setup → live capture → house points → records → reports) is complete.
- **Name:** `Carnival App` / `carnival-app` / `CarnivalApp`, kept for now (maintainer, 2026-10-21); renaming later is a find-and-replace across the tree (the prefix names databases, roles, cookies and storage keys: `carnival`, `carnival_app`, `carnival.*`); the pilot's port script no longer applies.
- **Licence:** AGPL-3.0 with dual licensing (commercial licences for closed integration). Dependencies are permissive only, checked per push by `scripts/check_licences.py` (MS-PL allowed in the test project, OFL for the bundled font). **No CLA and no outside pull requests** (maintainer, 2026-10-21: no lawyer); if contributions are wanted later, adopt an established CLA text unmodified.
- **Docs:** the current-state tree in `docs/README.md` is the primary documentation (architecture, admin, development); ADRs are short decision records (`docs/adr/README.md`). Staff guide: signing in and setting up a carnival are written; the other pages are planned.
- **Gates (branch `feature/setup-flow`, `0627733`):** API 2280 passed / 0 skipped (Docker tests included) · app tsc, lint, vitest 1833 / 127 files, build clean · local-accounts e2e 1/1 · dev e2e 84/88 (only the BACKLOG template test, on each device).

## Where code is written

**Here.** The port from the private pilot repository ended on 2026-11-04: a fresh code-only port of the pilot's last commit matched this tree file for file (one line-ending difference in a synthetic CSV). Work goes on a feature branch and merges to `main` when ready. **Never run the pilot's port script against this tree again**: with `--force` it clears and rewrites `api/`, `app/`, `config/`, `data/`, `deploy/`, `scripts/` and `.github/`. The school pilot instance still runs from the pilot repository; a fix it needs is made in both repositories by hand.

## Done: local accounts (merged to `main`, `39b5867`, 2026-11-07)

Built-in local accounts ([ADR-0065](adr/0065-built-in-local-accounts.md)), VM-tested, plus the deploy-script fixes from the first fresh-clone run (empty-database first start, branch/tag deploys, HTTPS by IP, no-change redeploys).

## In progress: the setup flow (branch `feature/setup-flow`, not merged)

- **What it is:** [ADR-0066](adr/0066-the-setup-flow.md) (the flow) and [ADR-0067](adr/0067-divisions-come-from-the-student-list.md) (divisions come from the student list), both accepted and edited in place before merge. Research and proposal: [setup flow](research/2026-11-07-setup-flow.md).
- **State:** all committed, reviewed and walked through three times on a fresh install; deployed to the VM (`0627733`, data kept, migrations 28 → 29). Behaviour in short: the first admin lands on School's "Set up your school" list (done only after School settings is saved; a set-up school shows nothing); a new carnival lands on Manage (Students → Events → Program → Open); students are imported from the carnival; Event setup holds the ages line and the standard events; **a carnival opens only with a student list and events** (`409 CARNIVAL_NOT_READY`; step 4 shows both opens greyed with what is missing); the Deck is for the day only. New installs seed no sample school name or houses.
- **Next step:** the maintainer tries the flow on the VM (`https://192.168.0.65`); on their OK, merge `feature/setup-flow` into `main` and push.
- **Screenshots for the maintainer:** a private artifact page, "Carnival App: first-run walk-through" (the maintainer views from another PC; republish it after UI changes).

## Working notes for the next agent

- **No .NET SDK on the maintainer's Linux machine.** Sessions ran it in `mcr.microsoft.com/dotnet/sdk:10.0` through a wrapper script (repo mounted at the same path, host Docker socket and CLI mounted so Testcontainers run, `ASPNETCORE_*`/`DOTNET_ENVIRONMENT`/`Section__Key` variables forwarded). Its containers outlive killed parents: after e2e runs, `docker ps` and stop any `dotnet/sdk` container. Installing the SDK locally would remove this friction.
- **The dev SQLite database** is created with `EnsureCreated`, so a schema change needs it deleted (`~/.cache/carnival-dotnet-home/.local/share/CarnivalApp/dev.db*` under the wrapper).
- **Test VM** `ssh carnival` (192.168.0.65, Debian 13, 2 vCPU, 3.8 GiB, Proxmox VM 165; 443 allowed from the LAN). Disk is 16 GB at about 77%: each `LOCAL_BUILD` deploy needs `docker builder prune -af` first until it is grown to 32 GB. The VM's checked-out branch refuses a direct push: push to `refs/deploy/<sha>`, then `git merge --ff-only` on the VM. `APPLY_MIGRATIONS` is still `true` there. `stash@{0}` holds the maintainer's runbook notes (already folded into the runbook).
- **How to work here** (maintainer, 2026-11-09): show mockups or screenshots before building UI; walk through a real fresh install before calling UX done; subagents for large parallel work and one review per phase, small fixes directly, nits batched or to BACKLOG.

## Before the first release

Done 2026-10-21: dependency licence audit and CI check; the wide synthetic roster regenerated by the port so it reproduces (ADR-0033); tag-triggered releases (`vX.Y.Z` → `X.Y.Z`/`X.Y` images and a GitHub release, ADR-0049); contribution policy (issues only); [Linux runbook](admin/linux-runbook.md); squashed and pushed to the private GitHub repository.

Done since: built-in local accounts (ADR-0065, merged); the fresh-clone homelab test (runbook parts A and B, fixed where reality differed).

1. **Merge the setup flow** (above).
2. **First release:** push tag `v0.1.0`; check the workflow published four images and a release, then set each GHCR package's visibility to public (packages do not reliably inherit it).
3. Check private vulnerability reporting is enabled on the public repository.

Nice to have at launch: README screenshots (synthetic data only) and a live demo (`docs/architecture/demo-mode.md`; the template build and demo deploy tooling are not in the repo yet).

## Next (Phase 2 — each needs an ADR first)

1. **Generic roster import** ([ADR-0064](adr/0064-canonical-roster-csv-and-test-rosters-built-in-code.md), accepted): the importer reads generic fields through the mapping file; CASES21 becomes a preset mapping; status, gender and year-level values move into the mapping; test rosters are built by a seeded C# builder and only a small sample roster is committed. Blocker: `RosterImportService` hard-codes the CASES21 column names and ignores `maps_to`.
2. **Records toggle**: a per-school `recordsEnabled` setting, enforced server-side.
3. **Generic OIDC**: one standard OIDC provider (Google Workspace, Okta, Keycloak…) behind the ADR-0065 sign-in box, replacing the Entra/ADFS-specific paths. With it, **sign-in settings move into the app** (BACKLOG; needs its own plan and ADR).
4. **School deployment template** (configuration only, pinned to a release) and a self-host guide pass for a tech-capable teacher (`docs/admin/self-host.md` exists).
5. **Staff guide** pages listed as planned in `docs/README.md`.

Open defects and debt: `docs/BACKLOG.md`.

## Open decisions (maintainer)

- ADR-0056 (high-jump heights ladder): confirm Accepted.
- The setup flow: try it on the VM, then approve the merge of `feature/setup-flow`.
- Grow the test VM's disk to 32 GB (optional; saves a cache prune per deploy).

## Constraints

- Synthetic data only, in every environment, fixture and capture.
- No outside pull requests (no CLA). Issues only.
