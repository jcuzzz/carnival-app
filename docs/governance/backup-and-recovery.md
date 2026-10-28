# Backup and recovery

> **Template.** Sections 2–5 describe what Carnival App's backup tooling does; the objectives, custodians, off-host target and drill record are your school's to set.
> Anything in `[square brackets]` is yours to fill in. Operating steps live in [Self-hosting §8](../admin/self-host.md#8-backups); this is the governance view of them.
> Check the defaults below against your `.env` and the version you deploy.

---

## 1. Objectives

| Objective | Target | Basis |
|---|---|---|
| **RPO (recovery point)** | `[24 hours]` by default. The `backup` service runs `pg_dump -Fc` every `BACKUP_INTERVAL_SECONDS` (default 86,400, so daily), and `scripts/deploy/vm-deploy.sh` takes an extra dump before every deploy. Shorten the interval for a tighter RPO | `docker-compose.yml`; `scripts/deploy/vm-deploy.sh` |
| **RTO (recovery time)** | `[measured figure from your latest drill]`. State the duration your restore drill actually takes, not an estimate | §6 |
| Carnival-day tolerance | A database restart or restore is largely invisible to recorders: the app saves each capture to a device outbox and delivers it when the server returns ([ADR-0002](../adr/0002-offline-sync-design.md)). This is why the database stays on the application host ([ADR-0047](../adr/0047-the-database-stays-on-the-host.md)) | |

**What the RPO costs.** Restoring the last dump loses results captured since it was taken, and the audit rows for them. Devices do **not** re-send work the server already accepted. An official can still see their accepted captures in the outbox history for **7 days**, which makes manual re-entry possible, but it is re-entry, not replay. On carnival day, take a deliberate dump before and after the meet.

---

## 2. What is backed up

| Item | In the dump? | Notes |
|---|---|---|
| The whole PostgreSQL database: students, roster snapshots, entries, results, revisions, records, adjustments, audit, import batches, configuration | **Yes** | `pg_dump -Fc` of the single database |
| `dp-keys` (ASP.NET DataProtection keyring) | No | Rebuildable, but losing it invalidates every session cookie and signs every official out. An availability event, not a data loss |
| `caddy-data` / `caddy-config` | No | Rebuildable; regenerating Caddy's internal CA means re-trusting it on every device |
| **`.env`**: school name, database passwords, identity-provider client secret, `SITE_ADDRESS`, backup recipient | **No** | Git-ignored by design; it exists only on the host |
| **`docker-compose.override.yml`**: the group-to-role mapping and any ADFS settings | **No** | Only the example is in the repository |
| Application code, migrations, configuration templates, event templates | n/a | Recreated from the repository and the pinned images |

**Rebuilding the host from backups alone is not possible** without `.env` and `docker-compose.override.yml`. Keep a sealed copy of both in `[the school's credential store]`, held by `[school ICT contact]`.

---

## 3. Encryption and key custody

| Item | How it works |
|---|---|
| Dumps at rest | The `backup` service (`deploy/Dockerfile.backup`: `postgres:17` plus `age`) dumps to a temporary file, encrypts it to `BACKUP_AGE_RECIPIENT` as `carnival-app-<timestamp>.dump.age`, and deletes the plaintext. **It refuses to start without a valid recipient.** |
| Escape hatch | `BACKUP_ENCRYPTION=off` writes plaintext dumps. It exists for a synthetic-data test host only, in the same class as `DEV_AUTH`: **never with real student data**. |
| Key custody | Generate the `age` key pair on a trusted machine, not the server. The **public** key goes in `.env`; the **private** key stays off the server and is copied on only for a restore or drill. **Custodians: `[at least two named people]`. Escrow: `[arrangement]`.** A backup nobody can decrypt is not a backup. |
| Retention | `BACKUP_RETENTION_DAYS` (default **14**). Pruning runs only after a cycle that wrote a dump, and always keeps the newest three, so a run of failed cycles cannot age the last good backup away. `.last-success` in the backup volume records the last good dump. |
| Pre-deploy dumps (`~/pre-*.dump`) | Written by the deploy script with mode 600 under `umask 077`, pruned to the newest five. **Unencrypted.** Treat the deploy account's home directory accordingly. |
| Live database volume and keyring | Not encrypted by the stack. Encrypt the host's disk or the hypervisor storage beneath it ([Self-hosting §9](../admin/self-host.md#9-encryption-at-rest)). **This deployment: `[how the disk is encrypted, and who holds the unlock passphrase]`.** |

---

## 4. Off-host copy

`scripts/deploy/push-backups.sh` copies the encrypted dumps to an rsync-over-ssh destination named by `BACKUP_PUSH_TARGET`; cron it nightly from the deploy account. It pushes **only** `*.dump.age`, never plaintext, fails rather than push a partial copy, and does nothing (with a message) while the target is unset.

**Without a target, the database and every dump share one disk**, and a disk failure, storage fault or ransomware on the host destroys them together.

| Item | This deployment |
|---|---|
| Off-host target (a second host, a school NAS, a backup server) | `[target]` |
| Credentials the deploy account uses to reach it | `[where they are held]` |
| Retention at the target | `[period]` |
| Physical separation from the host | `[different room / rack / site]` |

With a target, the arrangement is two copies on two media with one away from the host. Whether a second copy on the same campus is enough is `[school ICT contact]`'s call.

---

## 5. Restore procedure

**Drill (non-destructive), once a term:** `scripts/deploy/restore-drill.sh <age-identity-file>` decrypts the newest `*.dump.age`, restores it into a scratch database (`carnival_drill`) with `pg_restore --no-owner`, compares row counts with the live tables, prints the wall time, then drops the scratch database and shreds the plaintext. It fails if the newest dump is older than twice the backup interval, or if a restored table falls well short of its live count. Copy the identity file onto the server for the drill and shred it afterwards ([Self-hosting §8](../admin/self-host.md#8-backups)).

**Real restore (destructive):**

1. **Copy first.** Restoring overwrites the live database: take a fresh `pg_dump` (or a hypervisor snapshot, §7) before starting.
2. Choose the dump: the newest good one from the `db-backups` volume, or the relevant `~/pre-*.dump`. Decrypt an `.age` dump with the private key.
3. Restore into a **scratch database first** and check row counts there before touching the live one: `pg_restore --no-owner -d <scratch> <dump>`.
4. Verify: `__EFMigrationsHistory` matches the deployed migrations, and spot counts on `Results`, `Entries`, `CarnivalEvents`, `AuditEvents` look right.
5. Switch over: stop the API (`docker compose stop api`), restore into the live database, start the API, and confirm `/health` reports healthy and an official can sign in. Re-run `deploy/db/init-roles.sql` (the deploy script does this) so the application role's grants are in place.
6. **Record the elapsed time** and the outcome in §6.

---

## 6. Drill schedule and record

**Cadence:** each term, and also before each carnival season, after any change to the backup, encryption or push configuration, and after a migration that changes the schema materially. **Owner: `[patching owner / school ICT contact]`.** A drill that is not recorded did not happen.

Keep one record. If you already fill in the instance record in [Self-hosting §10](../admin/self-host.md#10-instance-record-fill-in-keep-with-your-own-documentation), reference it here rather than maintaining two.

| Date | Scope | Outcome | Duration | Findings |
|---|---|---|---|---|
| `[date]` | `[drill script / full restore]` | `[pass / fail]` | `[mm:ss]` | `[notes]` |

---

## 7. Hypervisor snapshots: a caveat, not a backup

If the host is a VM, snapshots taken before an upgrade are useful: they roll back a bad deploy in seconds. They are **not** a backup arrangement:

1. **A snapshot or a VM backup file is a copy of the whole disk**, student data included. Unless the storage it lands on is encrypted, it is a plaintext copy. Treat each one as a backup for handling, inventory and disposal, and **delete it once the deploy is proven**.
2. **It shares the failure domain.** A snapshot lives on the same storage as the guest, so it survives a bad deploy but not a storage failure.

A snapshot of a running guest captures the database mid-write; rolling back to one is a crash recovery, not a clean restore. Prefer a dump when the point of the copy is the data.

---

## 8. Checklist before real data

| Item | Owner |
|---|---|
| Generate the `age` key pair off the server; set `BACKUP_AGE_RECIPIENT`; confirm `BACKUP_ENCRYPTION` is not `off` | `[technical owner]` |
| Name at least two key custodians and the escrow arrangement | `[principal]` / `[school ICT contact]` |
| Nominate the off-host target, its credentials and retention; cron `push-backups.sh` | `[school ICT contact]` |
| Encrypt the host disk or the storage beneath it; record who holds the unlock passphrase | `[school ICT contact]` |
| Hold a sealed copy of `.env` and `docker-compose.override.yml` | `[school ICT contact]` |
| Run a restore drill, record its duration, and state that duration as the RTO | `[patching owner]` |

## In the code

- `docker-compose.yml` (the `backup` service), `deploy/Dockerfile.backup`
- `scripts/deploy/restore-drill.sh`, `scripts/deploy/push-backups.sh`, `scripts/deploy/vm-deploy.sh`
