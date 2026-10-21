# ADR-0001: Technology stack

- **Status:** Accepted (2026-07). Hosting and backup amended by [ADR-0047](0047-the-database-stays-on-the-host.md); image build and deployment by [ADR-0049](0049-images-are-built-in-ci-and-pulled-by-digest.md).

## Context

The platform needs a PWA that tolerates a dropped connection, a versioned backend API, a relational database, single sign-on against the school's identity provider (Entra ID, or on-premises ADFS), and isolated per-school deployments. It is maintained by one person, so operational burden and long-term support matter more than novelty. The option analysis is in [tech-stack-options.md](../research/tech-stack-options.md).

## Decision

| Layer | Choice |
|---|---|
| Frontend | React 19 + TypeScript + Vite, a single-page app with no server-side rendering. PWA through vite-plugin-pwa (Workbox): the app shell is precached; an allowlist of carnival-day API reads is cached network-first ([ADR-0031](0031-shared-device-hygiene.md)). |
| Device store | Dexie over IndexedDB, with a hand-written outbox keyed by client-generated idempotency keys ([ADR-0002](0002-offline-sync-design.md)). Sync logic stays in testable app code, not a third-party sync engine. |
| Backend | ASP.NET Core 10 (LTS), a versioned JSON API under `/api/v1`, with authorisation enforced on the server for every write. |
| Persistence | EF Core 10 code-first migrations targeting PostgreSQL. The domain model avoids provider-specific column types and generates identifiers (UUIDv7) in application code; PostgreSQL-only SQL is confined to migrations that need it (the audit table's append-only trigger, some data migrations). Local development uses SQLite through `EnsureCreated`, never the migrations. |
| Database | PostgreSQL 17: no licence cost and container-native, which makes one isolated instance per school cheap. |
| Identity | A server-side OIDC confidential client with a cookie session: Microsoft.Identity.Web for Entra ID, the standard OpenID Connect handler for ADFS ([ADR-0007](0007-configuration-and-auth-modes.md)). Both feed one provider-neutral group → role mapping. |
| Deployment | Docker Compose, one compose project per school (its own database, backups and keys), Caddy as reverse proxy and TLS terminator, and a backup sidecar in the baseline from the first deployable build: recoverability is an acceptance gate, not an add-on. |
| Testing | xUnit with WebApplicationFactory (per-endpoint authorisation) and Testcontainers (real PostgreSQL, skipped where Docker is absent); Vitest with fake-indexeddb for the outbox; Playwright for end-to-end journeys. |

How the pieces fit: [architecture overview](../architecture/overview.md).

## Consequences

- Mainstream, long-supported technology with large documentation and hiring pools reduces the single-maintainer risk.
- Two languages (TypeScript and C#) rather than one, accepted for the maturity of .NET's OIDC and migration tooling.
- A second production provider (for example SQL Server) would need equivalents for the PostgreSQL-only migration steps; the model itself does not prevent it.
- Caddy's automatic public ACME needs a publicly reachable host; most school servers are not, so the default is Caddy's internal CA or certificates the school supplies ([self-hosting](../admin/self-host.md#tls)).

## Rejected alternatives

- **SSR frameworks** (Next.js and similar): server rendering adds nothing to an authenticated, device-cached app and complicates offline behaviour.
- **Sync engines** (RxDB, PouchDB-style replication): general bidirectional sync for an append-mostly workload, with the critical logic inside a black box.
- **Self-hosted identity providers**: another security-critical service for a school to run, when it already has Entra ID or ADFS.
- **Native Windows installs, and SQL Server as the default database**: higher licensing and operational cost than containers on Linux.
