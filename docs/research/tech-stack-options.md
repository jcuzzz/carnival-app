# Technology Stack Options & Recommendation

**School Sports Event and Records Platform**
Research companion to *proposal_v0.1.md* · Compiled July 2026 · Author: research pass for technical owner

> **Purpose.** This document evaluates concrete technology options for each layer of the platform and gives a single recommended stack. Choices are weighted against the proposal's binding constraints: **solo maintainer initially**, **per-school isolated single-tenant deployment** on **school ICT infrastructure (Windows-friendly)**, **offline-first event-day entry**, **OIDC SSO via Microsoft Entra ID** (Victorian government school context), **relational DB with strong migration tooling**, and **low operational burden with long-term support**. The governing bias is *boring, well-documented, LTS technology* over novelty.

---

## 1. Summary recommendation

| Layer | Recommendation | Leading alternative(s) | Primary reason |
|---|---|---|---|
| **Frontend framework** | **React 19 + TypeScript + Vite** | Vue 3.5; SvelteKit | Largest talent pool & docs, lowest continuity risk for a solo maintainer; framework-agnostic PWA tooling. |
| **PWA / service worker** | **vite-plugin-pwa (Workbox under the hood)** | Hand-rolled Workbox; Serwist | Zero-config generated service worker; precache app shell, runtime-cache API reads. |
| **Offline data / local store** | **Dexie.js (IndexedDB) + explicit outbox queue** | RxDB; idb | Right weight — typed tables, transactions, versioned schema; custom sync gives control over idempotency/conflict rules the proposal mandates. |
| **Backend language/framework** | **ASP.NET Core 10 (LTS) Web API, C#** | Node.js + NestJS; Spring Boot | LTS to Nov 2028, first-class Entra ID integration, superb tooling, one language end-to-end optional. |
| **ORM + migrations** | **EF Core 10** (code-first migrations) | Dapper + DbUp/FluentMigrator | Versioned, testable migrations; migration bundles for controlled upgrades; LINQ productivity for a solo dev. |
| **Database** | **PostgreSQL 17** | SQL Server 2022 Express/Standard | $0 licensing, cross-platform, container-native, excellent backup/restore tooling — decisive for per-school replication. |
| **Identity / OIDC** | **Microsoft.Identity.Web** (confidential client, auth-code + PKCE) | Plain `Microsoft.AspNetCore.Authentication.OpenIdConnect` | Purpose-built for Entra ID; token handling, group-to-role mapping, secure defaults. |
| **Container / deployment** | **Docker (Compose) images, Linux containers via WSL2 or a small Linux VM** | Podman; Windows native service | One reproducible artifact per school; Compose is the de-facto single-host format; Postgres + API + reverse proxy. |
| **Reverse proxy / TLS** | **Caddy** (auto-TLS) or **Nginx** | Traefik; IIS/ARR | Automatic certificate management lowers solo-maintainer burden. |
| **Test tooling** | **xUnit + Testcontainers + WebApplicationFactory** (backend); **Vitest + Testing Library + Playwright** (frontend/E2E) | NUnit; Cypress | Migration tests against real Postgres in a container; offline queue & auth-per-endpoint coverage. |

**One-line stack:** *React/TypeScript PWA (Dexie offline outbox) → ASP.NET Core 10 versioned API (Microsoft.Identity.Web) → EF Core 10 → PostgreSQL 17, all shipped as Docker Compose images per school, fronted by Caddy for automatic TLS.*

---

## 2. Frontend framework

**Options**

1. **React 19 + TypeScript + Vite** *(recommended)* — 68% framework market share in 2026; the deepest documentation, hiring pool, and third-party ecosystem. React 19's stable features are relevant but the app can stay a plain SPA (no Next.js/SSR needed — SSR adds operational complexity a single-tenant offline app does not want). Vite gives fast builds and clean PWA integration.
2. **Vue 3.5** — genuinely excellent DX, smaller/cleaner than React, strong docs, Vapor Mode performance gains. A completely defensible pick; slightly smaller talent pool than React for continuity/handover.
3. **SvelteKit** — smallest bundles, great PWA fit, compiles away the runtime. Best raw performance, but the smallest ecosystem/hiring pool of the three — a real consideration given the proposal's *single-developer dependency* risk.

**Recommendation: React 19 + TypeScript + Vite.** For a solo-maintained system whose top named risks are *single-developer dependency* and *continuity*, optimise for the largest documentation base, the most AI-assistant coverage, and the easiest future handover. Keep it a plain client-rendered SPA — no SSR framework — since the app is an installable PWA talking to a versioned API, and SSR would only add moving parts. TypeScript is non-negotiable for the durable, well-tested codebase the proposal wants.

---

## 3. PWA / service worker strategy

**Options:** `vite-plugin-pwa` (recommended), raw Workbox, or Serwist (Workbox successor for some stacks).

**Recommendation: `vite-plugin-pwa`**, which wraps Google's **Workbox**. It is Vite-native and framework-agnostic, so it survives even a later frontend change.

**Concrete service-worker strategy for this app:**
- **App shell** (JS/CSS/HTML): `injectManifest` or `generateSW` precache with a versioned cache — the installed PWA opens offline at the venue.
- **Carnival config, assigned events, minimum roster** (the "event-day package"): fetched on pre-event preparation and stored in **IndexedDB (Dexie)**, *not* the SW cache — this is application data, not static assets.
- **API reads** (reference/list data): Workbox `NetworkFirst` with a short cache fallback so stale-but-usable data survives a dropout.
- **API writes** (result submissions): **never** go through SW background sync alone. Writes are written to a **Dexie "outbox" table first**, then flushed by an explicit sync worker (see §4). This keeps idempotency and conflict handling in application code where it is testable, per the proposal's Section 10.
- Show explicit **pending / synchronising / accepted / rejected / conflict** states from the outbox record status — a direct proposal requirement.

---

## 4. Offline data / local storage layer

**Options**

1. **Dexie.js** *(recommended)* — a clean, promise-based, typed wrapper over IndexedDB with real indexed queries, transactions, and **schema versioning/upgrade hooks**. Lightweight; `dexie-react-hooks` gives live-updating queries. Sync is left to you.
2. **RxDB** — full reactive offline-first database with a pluggable replication/sync protocol and conflict handling built in. Powerful, but heavier, opinionated, and its best sync stories assume compatible backends; more framework to learn/maintain solo.
3. **idb** — minimal typed IndexedDB promise wrapper. Too low-level; you would rebuild Dexie's ergonomics.

**Recommendation: Dexie.js with a hand-written outbox + sync module.** The proposal's offline requirements are *specific and safety-critical* (idempotency keys, no duplicate on retry, surfaced conflicts, "cannot finalise with unresolved queue"). These rules must live in explicit, unit-tested application code, not be delegated to a generic sync engine. Dexie gives exactly the right primitives without hiding the sync semantics.

**Design sketch:**
- Tables: `rosterSnapshot`, `carnivalConfig`, `assignedEvents`, `outbox`, `syncMeta`.
- Each `outbox` row carries a **client-generated idempotency key (UUID)**, entity payload, event-time snapshot fields, status, retry count, and last error.
- Sync flush POSTs each row with its idempotency key; the API **upserts by key** so a retry is a no-op (server-side idempotency table). Conflicts (concurrent server change) return a conflict response that flips the row to `conflict` for manual resolution.
- Dexie's versioned `.upgrade()` handles local schema evolution when the app updates.

---

## 5. Backend language / framework

**Options**

1. **ASP.NET Core 10 (LTS), C#** *(recommended)* — LTS with support to **November 2028**; the strongest option for Entra ID integration; excellent, stable tooling; minimal-API or controller styles; built-in DI, config, health checks, structured logging. Compiles to a single self-contained container image.
2. **Node.js + NestJS (TypeScript)** — lets a solo dev use *one language* front-to-back, which is a real productivity/continuity argument. Mature, structured, testable. Weaker "batteries-included" story for enterprise auth and migrations than .NET; more assembly of libraries.
3. **Java + Spring Boot** — extremely mature, LTS JDKs, superb migration/testing ecosystem. Heavier runtime and more verbose; overkill for a single-maintainer school app.

**Recommendation: ASP.NET Core 10 LTS.** In a Victorian government school context the identity provider is almost certainly **Microsoft Entra ID**, and .NET has the deepest, best-documented, Microsoft-supported path there (§7). The LTS cadence, first-class EF Core migrations, strong static typing, and mature testing story directly serve the "boring, well-documented, testable, low operational burden" mandate. The one genuine tradeoff versus NestJS is two languages instead of one — acceptable given the Entra ID and migration advantages. **Version the API from day one** (`/api/v1/...`) as the proposal requires.

---

## 6. ORM & migration tooling

**Options**

1. **EF Core 10** *(recommended)* — code-first, versioned migrations checked into source control; `dotnet ef migrations` workflow; **migration bundles** (self-contained executables) for controlled, backup-gated upgrades on a school server; LINQ productivity. EF Core 10 continues the LTS line.
2. **Dapper + DbUp / FluentMigrator** — micro-ORM plus explicit SQL migration scripts. Maximum control and transparent SQL, but more hand-written plumbing and no model snapshot — more solo effort.
3. **EF Core with SQL Server provider** — same ORM, different DB (see §7).

**Recommendation: EF Core 10 with the Npgsql (PostgreSQL) provider.** It gives exactly the *versioned, testable migrations from supported prior versions* that Section 14 demands. Practical guidance for this project:
- Generate a **migration bundle** per release; the upgrade runbook is: *backup → run bundle → smoke test → rollback via restore if needed*.
- Keep the append-only **audit/result-revision** tables that the data model needs; EF handles these as ordinary entities.
- Add a **migration test** (§9) that applies all migrations onto a fresh Postgres container and asserts the schema — catches breakage before a school ever sees it.

---

## 7. Database: PostgreSQL vs SQL Server

**The decision.** Both meet the relational-integrity, transaction, and reporting requirements. The deciding factors for *this* project are **per-school replication cost**, **container-native operation**, and **solo operational burden**.

| Factor | PostgreSQL 17 | SQL Server 2022 |
|---|---|---|
| Licensing per school instance | **$0**, any number of schools | Express is free but capped (10 GB/db, limited RAM/CPU); Standard is paid per-core — costly to replicate across many schools |
| Runs in Linux containers | Yes, first-class, tiny image | Yes (Linux image exists) but heavier; Windows-host Docker story less clean |
| Backup/restore for "restore into clean env" gate | `pg_dump`/`pg_basebackup`/PITR, well-documented, scriptable | Robust (`.bak`, Always On) but tooling assumes more SQL Server expertise |
| Fit with per-school isolation | Trivial to template and stamp out | Feasible but licensing + footprint fight the multi-school goal |
| Microsoft-stack reporting (Power BI) | Supported via connector | Native/tightest |

**Recommendation: PostgreSQL 17.** The proposal's product model is *one codebase, many isolated single-tenant deployments*, explicitly including school-hosted servers and a productisation goal of stamping out a second (and Nth) school. **Zero licensing at any scale** and a clean, small, container-native footprint make Postgres the obvious fit; it removes a recurring per-school cost and licensing-compliance chore from a solo maintainer.

**When SQL Server would win instead:** if a specific school's ICT mandates Microsoft-only data platforms, or requires SQL Server for Power BI/Always Encrypted/existing DBA support. Mitigation: because EF Core abstracts the provider, keeping the data layer **provider-clean (avoid Postgres-only SQL in app code)** preserves an escape hatch to the SQL Server provider per deployment if a school's policy forces it. Treat that as a per-school configuration exception, not the default.

---

## 8. Identity / OIDC

**Options**

1. **Microsoft.Identity.Web** *(recommended)* — Microsoft's higher-level library over the OIDC handler; `AddMicrosoftIdentityWebApp` wires OIDC + cookie handlers with secure defaults, token acquisition/caching, and easy access to group/role claims for **group-to-role mapping**.
2. **`Microsoft.AspNetCore.Authentication.OpenIdConnect`** (plain) — the generic OIDC handler. Works with *any* approved IdP, not just Entra — useful if a school's approved provider is not Entra. More manual wiring.
3. **Duende IdentityServer / Keycloak (self-hosted IdP)** — **not recommended** here: the proposal explicitly wants the app to *not* handle directory passwords and to federate to an approved provider. Running your own IdP adds burden and contradicts the SSO intent.

**Recommendation: Microsoft.Identity.Web**, configured as a **confidential client using authorization-code flow with PKCE** (public clients are no longer recommended for web apps). Rationale:
- Victorian government schools run on Microsoft 365 / Entra ID — this is the path of least resistance and best support.
- Map **Entra security groups → application roles** (Event administrator, Results official, Verifier, Records manager, Read-only, System administrator) via group claims, keeping RBAC config-driven per school.
- **Enforce authorisation on the backend for every mutation** (proposal Section 8 & 14): use `[Authorize(Roles=...)]` / policy handlers on every write endpoint, and cover each with a test (§9).
- Keep the OIDC configuration (authority, client id/secret, group mappings) as **per-school configuration**, not code — matches the configuration-boundary in Section 7.3.
- **Design note:** keep an abstraction seam at authentication so a non-Entra approved IdP could be swapped to the plain OIDC handler without touching business code.

---

## 9. Container / deployment for a per-school isolated instance

**Options**

1. **Docker + Docker Compose, Linux containers** *(recommended)* — Compose is the de-facto single-host multi-container format. One `docker-compose.yml` per school describes: `api`, `db` (Postgres), `proxy` (Caddy/Nginx), and a `backup` sidecar/cron. Reproducible images, repeatable install/upgrade — exactly the "repeatable installation and upgrades" and "installer" outputs the delivery plan calls for. Docker Desktop is **free under the 250-employee / $10M threshold**, which covers a single school; on a server, Docker Engine on a Linux host (or WSL2) avoids Desktop licensing entirely.
2. **Podman + Compose** — daemonless, rootless-by-default, **free at any size**, ~95% Compose-compatible. Strong on Linux hosts and licensing-sensitive orgs; slightly less mature on Windows. A very reasonable swap if a school's ICT prefers it or wants to avoid Docker licensing questions entirely.
3. **Native Windows install (IIS + Windows Service + local Postgres/SQL Server)** — no container runtime needed, familiar to Windows ICT. But it loses reproducibility and clean isolation, and makes upgrades/rollbacks manual — works against the solo-maintainer and per-school-replication goals.

**Recommendation: Docker Compose with Linux containers**, run on the school's approved infra as either a small **Linux VM** or **Windows Server + WSL2**. Ship a **versioned release bundle per school**: pinned image tags, a `.env` for school-specific config (school name, timezone, Entra settings, houses/rules references), named volumes for the DB and backups, and the migration bundle. Front everything with **Caddy** for **automatic TLS certificate issuance/renewal**, which removes a recurring manual chore from a lone maintainer. Provide the upgrade runbook: *pull new tag → backup → run migration bundle → smoke test → rollback = restore + previous tag*. If a particular school's ICT resists Docker (or its licensing), **Podman is a drop-in** with the same Compose file.

**Per-school isolation is preserved** by construction: each school = its own compose project, own DB volume, own backups, own keys/secrets, own Entra registration — matching Section 7.1.

---

## 10. Test tooling

**Backend (ASP.NET Core / EF Core):**
- **xUnit** — standard .NET test framework.
- **Testcontainers for .NET** — spin up a **real Postgres** container per test run; run **migration tests** (apply all migrations to a clean DB) and repository/integration tests against real SQL, not a mock. This directly satisfies the "migration tests from supported prior versions" and "restore into clean environment" acceptance gates.
- **`WebApplicationFactory`** — in-process API host for endpoint tests; write an **authorisation test for every mutation endpoint** (Section 14.1) asserting each role can/can't perform each action.
- Domain unit tests for **points, ties, record-candidate, and invalid-state rules**.

**Frontend / offline:**
- **Vitest + Testing Library** — component and logic unit tests.
- **fake-indexeddb** (with Dexie) — unit-test the **outbox: idempotency, retry-is-no-op, conflict transition, "cannot finalise with pending queue"** logic offline.
- **Playwright** — E2E including an **offline scenario** (toggle network, queue results, reconnect, assert no duplicates) and multi-context concurrent-edit tests, mirroring the runtime smoke tests in Section 14.2.

**Pipeline / supply chain:** dependency and container-image scanning (e.g. Trivy) and secret scanning, per Section 14.1 — lightweight and CI-friendly for a solo maintainer.

---

## 11. How the stack maps to the proposal's hard constraints

| Constraint (proposal) | How the stack satisfies it |
|---|---|
| Solo maintainer, boring/LTS | React + .NET 10 LTS + Postgres — all mainstream, documented to 2028+, heavy AI-assistant coverage, easy handover. |
| Offline event-day entry + queued submissions | Dexie outbox + idempotency keys + explicit sync worker; visible pending/synced/failed/conflict states. |
| Versioned backend API | ASP.NET Core `/api/v1`, backend-enforced authz on every mutation. |
| Relational DB + strong migrations | PostgreSQL 17 + EF Core 10 migrations/bundles, migration tests in CI. |
| OIDC SSO (Entra ID) | Microsoft.Identity.Web, auth-code + PKCE, Entra group → role mapping, per-school config. |
| Per-school isolated, containerised, Windows-friendly hosting | Docker/Podman Compose, Linux containers on Linux VM or WSL2; own DB/backups/keys per school; Caddy auto-TLS. |
| Recoverability / restore into clean env | `pg_dump`/PITR + Testcontainers restore test; documented upgrade/rollback runbook. |
| Avoid school-specific forks | All school variance is config/`.env`/authored data; one image set, many deployments. |

---

## 12. Open questions to resolve in Phase 0 discovery

1. **Confirm the approved IdP** — is it definitely Entra ID, or could a school require a different approved OIDC provider? (Drives §8 abstraction seam.)
2. **Confirm hosting reality per pilot school** — Linux VM available, or Windows Server + WSL2 only? Any ICT prohibition on Docker Desktop licensing terms (→ Podman)?
3. **Any mandated Microsoft data-platform policy** that would force SQL Server for a given school (→ per-school EF provider exception).
4. **Power BI/SharePoint reporting expectations** — confirms whether a read-replica/export path needs early design.
5. **Backup destination & encryption policy** — where off-host copies may live, to finalise the backup sidecar design.

---

### Sources

- [Best Offline-First Tech Stack for 2026 — cssauthor](https://cssauthor.com/offline-first-tech-stack/)
- [Offline-first frontend apps: IndexedDB and SQLite — LogRocket](https://blog.logrocket.com/offline-first-frontend-apps-2025-indexeddb-sqlite/)
- [Dexie.js](https://dexie.org/)
- [Dexie vs idb vs RxDB — BSWEN](https://docs.bswen.com/blog/2026-04-07-indexeddb-libraries-dexie-idb-rxdb/)
- [RxDB as a PWA database](https://rxdb.info/articles/progressive-web-app-database.html)
- [What .NET 10 LTS Means for Enterprise Applications — DEV](https://dev.to/ismcagdas/what-net-10-lts-means-for-enterprise-applications-2cdh)
- [EF Core Migrations in .NET — DevLeader](https://www.devleader.ca/2026/06/20/ef-core-migrations-in-net-managing-your-database-schema)
- [Running Migrations in EF Core 10 — codewithmukesh](https://codewithmukesh.com/blog/running-migrations-efcore/)
- [Postgres vs SQL Server: Complete Comparison 2026 — Bytebase](https://www.bytebase.com/blog/postgres-vs-sqlserver/)
- [PostgreSQL vs SQL Server 2026 — tech-insider](https://tech-insider.org/postgresql-vs-sql-server-2026/)
- [Configure OpenID Connect Web authentication in ASP.NET Core — Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)
- [Microsoft identity platform + Entra ID with ASP.NET Core — Microsoft Learn](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/azure-active-directory/?view=aspnetcore-10.0)
- [Web Authentication with Microsoft Entra ID and .NET — Edana](https://edana.ch/en/2026/07/03/web-authentication-with-microsoft-entra-id-zero-trust-architecture-net-integration-and-security-best-practices/)
- [Top Frameworks and Tools to Build PWAs in 2026 — AlphaBOLD](https://www.alphabold.com/top-frameworks-and-tools-to-build-progressive-web-apps/)
- [React vs Vue 2026 — tech-insider](https://tech-insider.org/react-vs-vue-2026/)
- [vite-plugin-pwa (Vite PWA org)](https://vite-pwa-org.netlify.app/frameworks/svelte)
- [Podman vs Docker 2026 — Last9](https://last9.io/blog/podman-vs-docker/)
- [Docker vs Podman 2026 — daily.dev](https://daily.dev/blog/docker-vs-podman-container-runtime-which-to-use/)
- [Docker License Changes in 2026 — KnowledgeHut](https://www.knowledgehut.com/blog/devops/docker-license-change)
