# ADR-0061: Demo showcase with a database per visitor

- **Status:** Accepted (2026-10). The API and app parts are built; the template build and demo deployment (§5) are not yet in this repository.

## Context

A public showcase should let each visitor run their own carnival, see only their own data, and exercise the whole product: set up events, run a capture deck, see standings and records. A shared sandbox with a nightly reset would let visitors see and spoil each other's demonstrations.

The code offers no cheap way to isolate visitors inside one database:

- **No resource-scoped authorisation.** Role assignments are deployment-wide and every policy checks the role only; any carnival administrator sees every carnival.
- **Much of the state is school-wide by design**: school settings, records, the event catalogue, the roster, the audit log and role assignments. Row-level isolation would mean threading a tenant through all of these, in the code paths every school depends on.
- **The header identity is unusable in public.** The development handler trusts `X-Dev-*` headers from the request; on an internet host anyone could claim any role.
- **Deletion is restricted on purpose**: role assignments are never deleted and the audit table is protected by a trigger and revoked grants, so per-visitor cleanup by row delete would weaken both.

The product is single-tenant per deployment ([ADR-0001](0001-technology-stack.md)). The cheapest honest isolation keeps it that way and gives each visitor a deployment's worth of data: their own database.

## Decision

1. **One PostgreSQL database per visitor, cloned from a template.** A seeded template database holds a synthetic demonstration school. Provisioning runs `CREATE DATABASE … TEMPLATE …`, a file copy with no seeding on the request path; grants made in the template, including the audit revokes, are copied with it. In demo mode every request is served against the caller's database, resolved per request from the session; no query, policy or entity changes, and a school deployment's database registration is unchanged. The visitor holds `SystemAdministrator` in their own database so every screen can be shown. Singleton caches of database state (school settings, role assignments) are keyed by database; any future one must be too. Per-visitor connection pools are small and release idle connections.
2. **Identity is a session cookie, never the header scheme.** `Identity:Provider=Demo` registers a `DemoSession` handler that reads an opaque token from an `HttpOnly; Secure; SameSite=Strict` cookie and looks up its SHA-256 hash. `SameSite=Strict` is the CSRF control, possible because the demo has no sign-in callback. Startup refuses any mixed configuration: demo identity with dev header auth or any Entra/ADFS value (`DEMO_COEXISTENCE_FORBIDDEN`), demo identity without demo mode, or demo mode without demo identity or on SQLite. A demo host logs a critical "no real authentication" warning at startup.
3. **Sessions, expiry and capacity.** A separate control database holds sessions: token hash, database name, created and last seen. A session ends 24 hours after creation or 2 hours after last use, whichever is first. A cleanup job drops expired sessions' databases and any visitor database with no session; there is no scheduled full wipe, so a clock never cuts a live session short. At capacity (`Demo:MaxSessions`), provisioning evicts the least recently used session idle 30 minutes or more, else answers `503 DEMO_AT_CAPACITY`. Creating and dropping databases runs as a separate provisioner role with `CREATEDB`; the application role keeps the grants of [ADR-0047](0047-the-database-stays-on-the-host.md).
4. **Demo guard.** Every write under `/api/v1/imports` is refused (`403 DEMO_IMPORTS_DISABLED`) by path prefix, so a public host has no way to receive a real student export; the template's generated roster ([ADR-0033](0033-the-real-shape-fixture-is-generated.md)) is the only roster a demo holds. Provisioning is rate-limited per client address and host-wide, and a generous per-address limit covers all requests, sized for a staff room behind one NAT. Responses carry `X-Robots-Tag: noindex, nofollow`, and the demo build's HTML a robots `noindex` meta tag. CSV exports and the audit log stay: they are part of the demonstration and read only the visitor's own database.
5. **Template and deployment.** The template is migrated and seeded when it is built, never at visitor startup, and a demo host runs no startup migrations. A deploy rebuilds the template and ends every visitor session, so a clone is never migrated in place and every visitor is on the deployed schema.
6. **A separate app build.** A demo build shows a landing page with a **Start demo** button when there is no session, says when a previous demo has ended rather than silently provisioning a new one, and has no sign-in, sign-out or role-switch controls.

How it works in detail: [demo mode](../architecture/demo-mode.md).

## Consequences

- The isolation boundary is a database: a query-scoping defect cannot leak between visitors because there is no scoping. The new risks are per-request connection resolution and per-database caches, which a cross-visitor isolation test (Testcontainers, CI only) guards.
- Demo-only code is registered only when `Demo:Enabled=true`; `Identity:Provider=Demo` must never appear in a school deployment, and startup refuses the combinations that would make that dangerous.
- Every deploy ends every visitor session; acceptable for a showcase.
- Free-text fields accept whatever a visitor types, visible only to that visitor and gone within 24 hours.
- Per-address limits key on the full address, so an IPv6 client rotating within its /64 is bounded only by the host-wide limit and capacity eviction.
- Not yet in the repository: the template build script, a demo compose override and a `robots.txt` for the proxy.

## Rejected alternatives

- **Row-level isolation by role assignment**: rests on carnival-scoped authorisation that does not exist, leaves school-wide state shared, and its cleanup conflicts with the audit trigger and the no-delete assignment rule.
- **Real multi-tenancy** (a tenant key on every school-wide entity): correct but large, and it changes every query a school deployment depends on.
- **A stack per visitor** (or a leased pool): total isolation, but each API instance's memory caps a small host at a handful of visitors and needs an orchestrator.
- **A shared sandbox with a nightly reset**: visitors see and spoil each other's demonstrations.
- **The header identity behind a provisioned subject**: any caller can still send any header.
