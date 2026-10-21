# Backlog

Open work for Carnival App: the Phase 2 roadmap, known defects and technical debt. Each item is one line with a priority: **P1** should be fixed before a 1.0 release, **P2** matters but does not block it, **P3** is worth doing when the area is next touched. Paths are repo-relative (API paths under `api/src/CarnivalApp.Api/`); line numbers are approximate. Roadmap items change behaviour, so each needs an ADR before it is built (see [`AGENTS.md`](../AGENTS.md)). When an item is done, delete it here and update the docs tree in the same change.

## Roadmap (Phase 2)

- **[P1] Generic roster import** ([ADR-0064](adr/0064-canonical-roster-csv-and-test-rosters-built-in-code.md), proposed). A canonical CSV template keyed on date of birth, with CASES21 as one preset; enrolment-status, gender and year-level values move into the mapping configuration; the committed fixture CSVs and the Python generator give way to a seeded C# roster builder and one small sample roster. Known blocker: `Imports/RosterImportService.cs` (~128) looks columns up by hard-coded CASES21 names and ignores the mapping's `maps_to`, so renaming a mapping key passes the column check but rejects every row; inactive statuses (~24) and the 7–12 year range are constants.
- **[P1] Built-in accounts and generic OIDC.** Local accounts by default, one standard OIDC provider (Entra, Google Workspace, Okta…) as the upgrade, replacing the Entra- and ADFS-specific sign-in paths. In-app role assignment already works independently of the provider.
- **[P1] Per-school records toggle.** A `recordsEnabled` school setting, enforced server-side (detection, minting, records screens and exports), for schools that do not keep a record book.
- **[P1] Deploying a release.** A pushed `vX.Y.Z` tag now builds the images and a GitHub release, but `scripts/deploy/vm-deploy.sh` follows `main`: its script-currency check compares with `origin/main`, so a host cannot stay on a release tag. Let it deploy a checked-out tag, and document upgrading from one release to the next.
- **[P1] Self-host template.** A school deployment template (configuration only: `.env`, mapping, event templates, branding) to sit beside [self-hosting](admin/self-host.md).
- **[P2] Docs link check in CI.** Catch broken relative links across `docs/` and the root documents.
- **[P2] Staff guide pages.** The *(planned)* pages in [`docs/README.md`](README.md): setting up a carnival, importing the roster, carnival day, house points/records/reports, roles and shared devices.

### Later (each needs an ADR)

- **[P3]** House icons: upload per house in School settings (the first file-upload surface), shown beside the house name on the entry page.
- **[P3]** Age-group champions on the Dashboard: a per-carnival champion-point table and a per-student aggregation.
- **[P3]** Progress against the clock: ahead of or behind the program's slot start times.
- **[P3]** High-jump jump-offs (a tie on best, failures at best and total failures stands today).
- **[P3]** Scheduled roster sync from the student system instead of a manual CSV upload.
- **[P3]** "Resume where you left off" on the Deck; a warning in the carnival switcher when the outbox holds undelivered items; bottom navigation on phones.

## Known defects

- **[P3]** `Records/RecordDetection.cs`: one current record per category is enforced in code only; two concurrent finalisations could leave two (acknowledged in a code comment).
- **[P3]** No in-app way to remove an empty heat: a mis-tapped "+ New heat" is removable only through `DELETE …/races/{id}`.
- **[P3]** The results book does not print a field event's attempt series.

## Technical debt

### Data and API

- **[P2]** Multi-device marshalling residuals: two devices can each mint a race for the same division (the pending-mint gate is per device, and materialise is not idempotent per division); a race emptied by per-entry "Remove from race" can still receive another device's queued placements, minting a lone placing pool; an explicit-heat placement makes later taps join that heat one at a time, splitting the pool. Each wants a decision.
- **[P2]** No locally runnable test executes migrations against PostgreSQL (`ApiTestFactory` uses `EnsureCreated`; Postgres tests are Docker-gated and run in CI only), so broken migration SQL and the placement `DbUpdateException` path are first exercised in CI.
- **[P2]** Template reconcile diffs definitions only, so a changed slot division filter in a template never reaches an already-applied carnival's schedule.
- **[P2]** School settings → Event templates auto-selects the alphabetically first template (athletics before swimming).
- **[P2]** Dead model surface: `Result.Place` is never written; `ImportBatchStatus.Staged` is never persisted; audit actions `ResultVerify`, `ResultReturnToProvisional` and `CarnivalEventFinalise` have no writer; error codes `EVENT_FINALISED`, `EVENT_NOT_CLOSED`, `EVENT_FINALISE_VIA_GATE` and `FINALISATION_BLOCKED` have no emitter; `ResultEndpoints.TransitionRequest` is unused.
- **[P2]** Internal runtime identifiers keep the pre-port `sports` prefix deliberately: IndexedDB `sports-platform`, localStorage `sports.*` keys, database and role names `sports`/`sports_app`, backup files `sports-<timestamp>.dump.age`. Renaming breaks existing installs, so any rename needs a migration path.
- **[P3]** Remove the retired role-name aliases in `Identity/PlatformRoles.cs` once no deployment relies on them (alias hits are logged).
- **[P3]** Drop the frozen legacy `Divisions` key from `Carnival.SettingsJson` (a data migration); it is carried through writes and never read.
- **[P3]** Serialisable isolation only guards the definition editor against the reconciler; a result capture runs Read Committed, so editor-versus-first-result is a narrow window, not a closed one.
- **[P3]** The unkeyed (online) entry create is three commits (entry, race mint, placement), not one transaction.
- **[P3]** A forged `/signin-oidc` answers 500 (no `OnRemoteFailure` handler); an anonymous `/api` 401 on an OIDC host still sets nonce and correlation cookies.
- **[P3]** Hand-written SQL with double-quoted identifiers (`RoleAssignment` filtered index, `CK_Entry_IndividualOrTeam`) would break on SQL Server with `QUOTED_IDENTIFIER OFF`; six columns keep a migration-only DDL `DEFAULT` that an `EnsureCreated` database lacks.
- **[P3]** Audit `PriorValuesJson` has no pinned serialiser naming policy.
- **[P3]** `EventDefinition.ScoringWeight` is `int` on the entity and `decimal` in `EventDefinitionDto`.
- **[P3]** Adjustments store the configured house spelling as typed; scoring is case-insensitive, but the standings label is the first spelling seen.
- **[P3]** `RecordEndpoints.Pick` repeats `RecordDetection.Standing`'s ordering by hand; `ScoringService` derives a Social no-show inline instead of through the shared "recorded" rule.
- **[P3]** `HEIGHT_ATTEMPTS_EXHAUSTED` is unreachable through the mark PUT (`ATHLETE_ELIMINATED` answers first).
- **[P3]** The setup checklist sums roster counts from the heavier `/progress` aggregate, the only read a carnival administrator has; a narrow roster-count read would fit better. Carnival administrators also cannot reach roster import from the checklist's blocked notice.
- **[P3]** Roster cache searches are capped at the server's 50-row search limit (`Carnivals/EntryEndpoints.cs` ~172), which matters most for team entry across all houses.

### App

- **[P2]** Offline on a device that never fetched the entries or races reads, the deck cannot subtract what is already entered or placed: it can offer an already-entered student, cannot name a heat, and a refused incoming lane move shows only on the source row. All degrade to a parked conflict; a cached-read warm-up at sign-in would narrow them.
- **[P2]** The service worker can serve a stale results body under a fresh "updated" time (fetch-resolution time is stamped on a cache hit).
- **[P2]** Privacy surface: the import reconciliation table shows each staged student's raw `M`/`F`/`S` category (`app/src/features/imports/ReconciliationSection.tsx`, system administrators only) and bypasses `categoryLabel`; the override badge on an entry row reads as a claim about the student. Both want a documented position.
- **[P3]** `app/src/api/errorCodes.ts`: `NOT_ASSIGNED_OFFICIAL` is still classed retryable, though the server no longer emits it (the assignment gate is retired).
- **[P3]** User-facing and API detail strings cite ADR numbers (e.g. `app/src/features/results/AddEntry.tsx` ~164, `EntryRow.tsx` ~395, `app/src/features/schedule/SlotPanel.tsx` ~325, `app/src/features/standings/AdjustmentsPanel.tsx` ~47, `Carnivals/EntryEndpoints.cs` ~164 and ~321).
- **[P3]** `app/src/results/` coexists with `app/src/features/results/`; fold one into the other.
- **[P3]** Route typing does not check the HTTP method, and the `client.test.ts` type block is checked by `tsc -b`, not by `npm test`.
- **[P3]** The drain-invalidation effect keys on the accepted-item count, so an accept and a retention prune in one live-query emission fire nothing.
- **[P3]** A second offline edit of the same cell queues on the first's base revision and parks with `CONCURRENCY_CONFLICT` once the first lands.
- **[P3]** "Latest race" for auto-fill follows organiser-editable `SortOrder`, so a renumbering can redirect auto-fill into an older unswum race; a failed context create falls back to Heat 1 without telling the official.
- **[P3]** `EventResultsScreen` rebuilds its grid rows on every render, so rows cannot be memoised.
- **[P3]** The settings PUT returns only the first validation failure; read-only house-code inputs look editable.
- **[P3]** The program builder's "Add an event" label (`SlotPanel.tsx`, `app/src/App.tsx` ~118) names a different act from Event setup's add.
- **[P3]** Design tokens: `--opacity-quiet` and `--brand-ink-soft` do one job two ways; a spacing token is used as a width addend in `app/src/styles.css` (~849); the legacy off-scale spacing and type tokens are closed to new uses and await retirement.
- **[P3]** Phone deck header: at 390px the pickers wrap to two lines and "Next event" to a third (~190px before the first swimmer).
- **[P3]** Dashboard copy: "0 of 0 entered" for a house with no roster rows; stream labels show in their stored upper case.
- **[P3]** The division-profile editor cannot express a profile of only non-numeric ages, and a roster re-import that changes the derived ages does not flag carnivals whose profile names ages no longer derived.

### Tests, tooling and deployment

- **[P2]** `scripts/deploy/vm-deploy.sh` (~49) hard-codes `GHCR_OWNER`, so a fork must edit the script; make it configurable.
- **[P2]** `ci.yml`, `e2e.yml` and `supply-chain.yml` declare no `permissions:`; CycloneDX tools are unpinned and every `uses:` is a mutable major tag, including steps holding `packages: write`.
- **[P2]** Stale documentation: `docs/development/frontend-deployment.md` still describes the old `DEV_AUTH` header sign-in plus banner demo (the demo build is now banner, landing page and demo session cookie against a demo host); `config/import-mapping.example.json` comments misstate status, gender and year-level handling.
- **[P3]** Stale code comments: `Data/Entities/CarnivalEvent.cs` (~7, "no house points"); `Data/Entities/Enums.cs` (~34, "forward-only" carnival status; ~93, results enter at Provisional); `Data/Entities/IdempotencyKey.cs` (~11) says "canonicalised" though raw bytes are hashed.
- **[P3]** `environmentMatchGlobs` (`app/vite.config.ts` ~164) is deprecated; move to `test.projects` at the next Vitest major.
- **[P3]** Screenshot coverage gaps: the idle privacy overlay, the records-today strip (the seed has no finalised carnival or standing record) and the deck-progress wrap on a phone. The no-clipping sweep runs only in the screenshot pass, not the e2e gate.
- **[P3]** Floating async after `signOut()` (a deliberately un-awaited cache clear) makes some app tests intermittently report teardown errors.
- **[P3]** The dev database accumulates carnivals and events from every e2e and screenshot run (`scripts/dev-reset.ps1` clears it); `DevelopmentSeeder` creates Boys and Girls divisions only, so an Open division cannot be exercised by hand.
- **[P3]** An image built on the host with `LOCAL_BUILD` is an index with attestations and is not digest-comparable to a CI-built image.
- **[P3]** With `ApplyMigrationsOnStartup=false` and an unmigrated database the API refuses to start (the settings bootstrap reads its row); document this in the upgrade procedure.
