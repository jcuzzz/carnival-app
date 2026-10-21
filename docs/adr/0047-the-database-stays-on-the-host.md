# ADR-0047: The database stays on the application host, hardened in place

- **Status:** Accepted (2026-09). Amends [ADR-0001](0001-technology-stack.md) (hosting and backup baseline).

## Context

ADR-0001 put PostgreSQL beside the API in one Compose project per school, with a `pg_dump` sidecar as the backup baseline. A security assessment left two high findings on that layer: no encryption at rest at any layer, and no off-host backup copy. That raised the question of whether the database should move off the application host.

Options considered: a single host, hardened; a separate database guest on the same hypervisor; a managed Australian PostgreSQL service; fully cloud hosting; a primary with pgBackRest or a replica.

The forces that decided it: the offline-first client already absorbs a database restart (the outbox, ADR-0002), so separating hosts protects against a failure the client handles while adding a network hop, a certificate and a second machine to operate; a managed database puts the school's internet link on the critical path on carnival day; the departmental pathway rests on "student data never leaves school control"; and the two open findings need encryption and an off-host copy, not separation.

## Decision

1. **The database stays on the application host.** The Compose stack stays portable, so a department-provided or cloud host remains a configuration change later.
2. **Network isolation inside the host.** `db` and `backup` sit on an `internal: true` network that only `api` also joins; the proxy has no route to the database. Port 5432 is never published.
3. **Two database roles.** The owner role runs migrations only. A least-privilege application role, used by every request, holds `CONNECT`, `USAGE` and DML, with `UPDATE` and `DELETE` revoked on `AuditEvents` (the append-only trigger stays as the second lock) and statement, idle-in-transaction and lock timeouts set on the role. The API takes an optional migrations connection string and falls back to the application string when it is absent, so local development is unchanged. `deploy/db/init-roles.sql` is idempotent and is run by the deploy script before and after the API starts.
4. **Backups are encrypted before they rest, and can leave the host.** The sidecar encrypts each dump to an `age` recipient whose private key is held by the school, off the server; plaintext dumps are not kept. An empty recipient stops the sidecar unless encryption is explicitly turned off (for synthetic test instances). A push script copies the ciphertext to an off-host target on a schedule, and a restore-drill script restores the newest dump into a scratch database and compares row counts. The drill is run each term.
5. **Secrets hygiene:** the deploy script enforces mode 600 on `.env` and refuses to deploy without the application role's password.
6. **Disk encryption is the host operator's job**, at the hypervisor or disk layer (LUKS, ZFS native encryption), because it needs root and a reboot policy the application cannot own.
7. **TLS on the API-to-database hop is deferred, not rejected.** On an internal Docker network on one kernel it adds certificate lifecycle without changing the threat model; it becomes mandatory (`SSL Mode=VerifyFull`, pinned CA) the day the database leaves the host.

Procedures (key custody, push target, drill, disk encryption): [self-hosting §7–§9](../admin/self-host.md#7-what-the-stack-does-for-you).

## Consequences

- Backups are encrypted, and off-host once a school supplies a push target; until then the encrypted dumps share the one disk.
- The restore drill is the evidence a records-management review asks for, and the backup's routine test.
- A host failure takes the whole stack down; recovery is the encrypted dump on another machine.

## Rejected alternatives

- **A separate database guest on the same node:** a real blast-radius gain, but more to operate for a failure the client already absorbs. Revisit if a reviewer asks for host separation.
- **Managed or fully cloud PostgreSQL:** cost, carnival-day dependence on the WAN, and it restarts the compliance pathway.
- **pgBackRest or a replica:** point-in-time recovery is the upgrade path if a daily recovery point proves too coarse; not needed now.
