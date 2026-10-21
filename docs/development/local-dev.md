# Local development loop (throwaway SQLite — NOT production parity)

Zero-dependency way to run the whole platform on the Windows dev machine: **no
Docker, no Postgres, no Entra tenant, no dev certificates**. The API runs
against a throwaway local SQLite file with synthetic seed data; the PWA runs
under the Vite dev server.

> **This is a development convenience only.** The real target remains
> PostgreSQL 17 behind Docker Compose on the Proxmox VM (ADR-0001,
> `docs/admin/self-host.md`). SQLite schema comes from
> `EnsureCreated()`, not the Npgsql migrations, so it proves nothing about
> migrations, Postgres behaviour, transactions under concurrency, or
> deployment config. All seeded data is synthetic (`data/synthetic/`) — the
> repo hard rule against real student data applies as always.

## Run

```powershell
.\scripts\dev-run.ps1          # API + app, two windows
.\scripts\dev-run.ps1 -ApiOnly # or -AppOnly
```

| What | URL |
|---|---|
| API | `http://localhost:5080` (health `/health`; `/api/v1/version` requires auth since 2026-07-31) |
| App (Vite) | `http://localhost:5173` — the app calls relative `/api/*`, which Vite proxies to the API, so dev is same-origin like a deployment (`VITE_API_BASE` stays unset — see the note in `app/.env.development`) |

Equivalent manual commands: `dotnet run --launch-profile http` in
`api/src/CarnivalApp.Api`, and `npm run dev` in `app/`.

## How it works

- **Provider switch** — `Database:Provider` in configuration. `Sqlite`
  (set by `appsettings.Development.json`, i.e. Development only) uses a local
  file, default `%LOCALAPPDATA%\CarnivalApp\dev.db` (override with
  `Database:SqlitePath`), created via `EnsureCreated()`. Any other value —
  the default everywhere else — keeps the unchanged Npgsql + migrations path.
- **Seeding** — opt-in via `Database:SeedSyntheticData=true`, which only the
  `dotnet run` launch profile (`Properties/launchSettings.json`) sets, so
  tests and deployments never seed. Idempotent: a database that already holds
  students or carnivals is never touched; the school-settings row is created
  by the bootstrap on every first start (ADR-0045).
- **Model stays provider-clean** (ADR-0001 review note 5) so the same EF model
  works on both providers. Note SQLite cannot `ORDER BY` a `DateTimeOffset`
  column — order such lists in memory (see `ListEventsAsync`).

## Seeded data

| What | Detail |
|---|---|
| School configuration | bootstrap, from configuration on first run — `Example Synthetic College`, timezone `Australia/Melbourne`, houses BANKSIA / WATTLE / MOUNTAIN ASH / GREVILLEA |
| Roster | 40 synthetic ACTV students from `data/synthetic/ST_SYNTH.csv`, promoted through the real import pipeline (stage → approve → promote), so an `ImportBatch`, current `RosterSnapshot`s and audit rows all exist |
| Carnival | "2026 Swimming Carnival (synthetic)", status **Open**, venue "Synthetic Aquatic Centre" |
| Events | `SW-50-FREE` (Freestyle 50m, Time/s) scheduled for the four most-populated age-group × M/F divisions in the seeded roster, all **Open** |

Seed actions are attributed to the subject `dev-seed`.

## Dev identities (header auth)

`Identity:DevAuth:Enabled=true` in Development enables the header
authentication scheme (`api/src/CarnivalApp.Api/Identity/DevAuthentication.cs`)
— there is no login screen or token. Every request supplies:

| Header | Meaning | Example |
|---|---|---|
| `X-Dev-Subject` | subject id (any string) — becomes the audit actor | `dev-admin` |
| `X-Dev-Roles` | comma-separated platform roles | `CarnivalAdministrator,SystemAdministrator` |
| `X-Dev-Groups` | optional Entra group object ids, exercises group→role mapping | *(rarely needed)* |

**Four roles** (ADR-0016, amending proposal §8):

| Role | Holds |
|---|---|
| `SystemAdministrator` | School configuration, roster imports, role mapping, retention — **plus the entire carnival surface**, so a school with one technical person is not locked out. |
| `CarnivalAdministrator` | Carnival lifecycle including finalisation, events, schedule, officials, settings, adjustments, the ADR-0003 category override, and result capture and amendment. **No access to the import surface at all** — not even the read-only reports. |
| `Recorder` | Marshals and records: create entries on the deck, team/relay entries, remove a result-less entry, assign race/lane/heat, capture results and amend any unpublished result in the carnival. **Not** the category override. |
| `Viewer` | Read-only. |

`EventAdministrator`, `Verifier` and `RecordsManager` collapsed into
`CarnivalAdministrator`; `ResultsOfficial` became `Recorder`; `ReadOnlyViewer`
became `Viewer`.

**The six retired names still work here**, and deliberately so. `X-Dev-Roles`
resolves through `PlatformRoles.TryParse` — the same parser the Entra group→role
transformation uses — so `X-Dev-Roles: Verifier` yields a `CarnivalAdministrator`
claim and logs at `Information`. Two consequences worth knowing in the dev loop:

- A stale script or curl one-liner keeps working, which means **it will not tell
  you your role names are out of date**. Watch the API console for the alias log
  line. The aliases are temporary (they go when the VM's group mapping is
  modernised — `docs/admin/self-host.md`).
- Dev-auth now *validates* role names, which it did not before: a junk
  `X-Dev-Roles` value is dropped rather than becoming a claim. A typo that used
  to produce a nonsense role now produces no role, and the symptom is 403.

Convenient personas (these are the ones in the dev login picker,
`app/src/screens/DevLoginScreen.tsx` — keep the two in sync):

```text
X-Dev-Subject: dev-admin          X-Dev-Roles: SystemAdministrator,CarnivalAdministrator   # "Alex Admin"
X-Dev-Subject: dev-carnival-admin X-Dev-Roles: CarnivalAdministrator                       # "Cass Carnival"
X-Dev-Subject: dev-official-1     X-Dev-Roles: Recorder                                    # "Olivia Official"
X-Dev-Subject: dev-official-2     X-Dev-Roles: Recorder                                    # "Omar Official"
X-Dev-Subject: dev-viewer         X-Dev-Roles: Viewer                                      # "Riley Readonly"
```

Notes on the picker, because both are load-bearing:

- **"Alex Admin" must keep that display name** — the Playwright helpers
  (`app/e2e/app.ts`, `app/e2e/dev-api.ts`) key on it, and renaming the persona
  fails every e2e journey at sign-in. The e2e seeding header is
  `X-Dev-Roles: CarnivalAdministrator,SystemAdministrator`.
- **There are two Recorder personas on purpose.** ADR-0006's device handover is
  sign out → sign in, and queued outbox items keep their capture-time actor, so
  two recorders are what it takes to exercise `X-Captured-Actor` on a drained
  capture *or correction* — the correction now sends it too (ADR-0016 §2).
- The old "Vera Verifier" persona is gone with the role. `dev-carnival-admin`
  replaces it with the shape ADR-0016 expects a small school to run: one person
  who administers the carnival **and** records, which used to need two accounts.

Example:

```powershell
Invoke-RestMethod http://localhost:5080/api/v1/carnivals `
  -Headers @{ 'X-Dev-Subject' = 'dev-admin'; 'X-Dev-Roles' = 'CarnivalAdministrator' }
```

DevAuth only activates when no Entra tenant is configured AND
`Identity:DevAuth:Enabled=true`; it **must remain false in any school
deployment** (`appsettings.School.example.json` documents this).

## What the result lifecycle does now (ADR-0017)

If you last ran this loop before 2026-08-11, four things changed and none of
them will throw an error at you:

- **A captured result arrives `Verified`.** There is no human verification step
  and no `Verifier`. `POST /api/v1/results/{id}/verify` and
  `/return-to-provisional` are **deleted routes — 404, not 403.** So is
  `POST …/events/{id}/finalise`.
- **The badge reads "Recorded".** `Provisional` and `Verified` both render that
  way; only `Final` prints as "Final". The raw lifecycle state is no longer put
  on screen.
- **Event completion is derived, not a status.** An event reads complete when
  every entry carries a result or a non-result status; a new entry
  **un-completes** it. Expect the tile badge to flicker while you are seeding
  entries — that is the design, not a race.
- **Finalisation is one act per carnival.** `Closed → Finalised` sweeps every
  non-`Draft` result to `Final` in one transaction, and `Final` results refuse
  correction with `RESULT_FINALISED`. `Closed → Open` is legal, so closing a
  carnival early in the dev loop is recoverable.

## Reset

```powershell
.\scripts\dev-reset.ps1   # stop the API first, then run this
```

Deletes the SQLite file (`dev.db` + WAL/SHM). The next API start recreates the
schema and reseeds. The database is disposable by design — never keep anything
you care about in it.

Reset is **required after any entity change**: the schema comes from
`EnsureCreated()`, which never migrates an existing file, so a stale `dev.db`
answers new columns with a 500.

## The compose stack locally (Docker, optional)

`docker-compose.yml` **pulls** its images from GHCR since ADR-0049 — the
`build:` blocks moved into `docker-compose.build.yml`, an overlay. To build and
run everything from your own working tree, with no registry credential:

```bash
cp .env.example .env    # set POSTGRES_PASSWORD / APP_DB_PASSWORD at least
docker compose -f docker-compose.yml -f docker-compose.build.yml up --build
```

That behaves exactly as a bare `up --build` did before, `VITE_DEMO_MODE` build
arg included. Two things to know:

- A local build **tags** the result with the `image:` value from
  `docker-compose.yml` (`ghcr.io/jcuzzz/carnival-app-*:main`), so a later plain
  `docker compose up` reuses your local image instead of pulling. Run
  `docker compose pull` when you want GHCR's copy back.
- Contributors need a GHCR credential only to *deploy*, never to work on the
  code. The VM never runs this overlay: it deploys digests
  (`docs/admin/self-host.md`).

This is still not production parity in the way that matters here — it *is*
PostgreSQL and the real images, but the loop above is faster for everything
except a Postgres- or container-specific question.

## End-to-end tests

`cd app && npm run test:e2e` drives this same loop in real browsers across four
device profiles (iPad/iPhone on WebKit, Pixel/desktop on Chromium) — including
the offline-capture → drain journey. It starts the API and Vite itself, or
reuses the ones `dev-run.ps1` started. See `app/README.md`.
