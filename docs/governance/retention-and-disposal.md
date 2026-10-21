# Retention and disposal

> **Template.** Sections 1–2 describe how long Carnival App keeps each kind of data, taken from the code; the retention classes and periods are your school's to set.
> Anything in `[square brackets]` is yours to fill in. The software sets no retention periods for records: it keeps them until someone removes them. Only three operational stores age out automatically, on fixed windows (§2).
> If your school is a Victorian government school, the periods come from PROV **PROS 22/06** (Retention and Disposal Authority for School Records); obtain it from PROV.

If your school is a Victorian government school, the department's Records Management policy allows disposal only after the minimum retention period **and** with written principal approval, and records subject to an FOI request, legal proceedings, or of permanent historical value cannot be destroyed even then. School sporting and carnival records are commonly treated as of ongoing historical value: **confirm it, do not assume it.** Other schools apply their own records obligations: `[records policy]`.

---

## 1. Entity by entity

| Entity | What it holds | Behaviour in the software | Retention class | Disposal action once the class is known |
|---|---|---|---|---|
| `Students` | STKEY, surname, first name, preferred name, active flag | Written on promotion; **never deleted or de-identified**. A departed student is marked inactive and kept | `[class]` | Delete or de-identify students departed longer than the period |
| `RosterSnapshots` | Year level, house, home group, derived age group, derived M/F/S category, per import | Never rewritten, never deleted | `[class]` | Likely follows the entries and results that reference them |
| `ImportBatches` (metadata) | Source file name, SHA-256, mapping version, importer, reviewer, counts, status | Kept indefinitely | `[class]` | Import and reconciliation records |
| `ImportBatches` staged rows and row issues | A second copy of the roster, including rejected rows and departed students | **Discarded at promotion** (in the promotion transaction) **and on rejection**; a batch never promoted loses them **7 days after upload** (server retention sweep) | n/a: operational | Automatic |
| `Entries` / `RelayLegs` | Event-time snapshot: display name, year level, house, age group, category; the student link | Kept indefinitely; an entry with no result can be removed | `[class]` | Follows the result |
| `Results` | Value, attempts, status, lifecycle, capturing official, device | Kept indefinitely; never deleted, only corrected | `[class]` | The core competition record |
| `ResultRevisions` | Prior and new values, actor, role, reason | Append-only; never updated or deleted by the application | `[class]` | Should share the result's class |
| `SchoolRecords` | Record book: holder name, value, void details | Kept indefinitely; superseding adds a row, voiding never deletes | `[class]` | **Most likely candidate for permanent retention**; confirm |
| `HousePointsAdjustments` | House, points, category, reason, actor; **no student** | Kept indefinitely; voided, never deleted | `[class]` | Follows the carnival |
| `AuditEvents` | Actor, roles, time, action, entity, prior and new values, reason | **Append-only, enforced by a database trigger**; no application path deletes one | `[class]` | The trigger refuses any deletion sweep: disposal needs a documented, approved, out-of-band step |
| `IdempotencyKeys` | Payload hash, **stored response body** (for an entry or relay creation, includes student display names), actor, device | Deleted **30 days after creation** (server retention sweep) | n/a: operational | Automatic |
| `RoleAssignments` | Staff UPN, subject, role, reason, grant and revocation history | Never deleted; revocation stamps who, when and why | `[class]` | Access-authorisation records |
| `CarnivalEventOfficials` | Assigned official's subject and display name | Kept indefinitely | `[class]` | Follows the carnival |
| `DeviceSyncStatuses` | Device id, last official, counts, app version; **no student data** | Last write wins per device per carnival; never pruned | `[class]` | Operational telemetry; delete with the carnival |
| `SchoolConfigurations` | Name, timezone, houses, branding, settings; no personal data | One row, updated in place | n/a | — |
| Device outbox (IndexedDB) | Captured entries and results, with student names | `accepted`, `discarded` and `cancelled` pruned **7 days** after resolution or cancellation; an accepted row's name stripped **1 hour** after acceptance; `queued` and `syncing` never pruned; **`conflict` and `rejected` keep the name until a person resolves them** | n/a: the server holds the record | See §2 |
| Device roster cache (IndexedDB) | Eligible students for an event | Pruned after **72 hours**; cleared on sign-out or handover | n/a | — |
| Service worker `api-reads` cache | Allowlisted carnival-day GET responses | At most 24 hours and 200 entries; cleared on sign-out | n/a | — |
| Database backups | Encrypted `pg_dump` of everything above | `BACKUP_RETENTION_DAYS` (default 14), always keeping the newest three; off-host copies per your target's retention | Inherit the class of what they hold | See `backup-and-recovery.md` |
| Pre-deploy dumps on the host | Unencrypted full dump taken before each deploy | Mode 600, pruned to the newest five | n/a | Delete on decommission |

---

## 2. What the software does and does not do

**Built:**

- **Staged rosters are discarded at promotion and on rejection** ([ADR-0032](../adr/0032-staged-rosters-are-not-kept.md)). The batch keeps its counts, file name and hash, and the `import.promote` or `import.reject` audit row, not the rows. Reading the reconciliation of such a batch answers `409 IMPORT_ROWS_PURGED`.
- **A server retention sweep for operational data** ([ADR-0062](../adr/0062-retention-windows-for-operational-data.md)). A background job in the API runs at start-up and then daily, on a school instance with PostgreSQL (not on a demo host or the SQLite development database). It deletes idempotency keys 30 days after creation, and discards the staged rows of any batch not promoted within 7 days of upload; approving, promoting or reading the reconciliation of such a batch answers `409 IMPORT_BATCH_EXPIRED`, and the importer uploads the file again. It touches no record class and writes no audit event per row; each run logs its counts. The windows are fixed in code.
- **The source export file is never stored**, only its name and SHA-256.
- **No full date of birth is stored.** It is read at import to derive the age group.
- **Device-side retention**: the 7-day outbox sweep (accepted, discarded and cancelled rows), the 1-hour name strip on accepted rows, the 72-hour roster cache, and the sign-out purge with a retry if it fails.
- **Encrypted backups with a retention window** and a scripted restore drill.
- **The audit table is append-only below the application**, not merely by convention.

**Not built:**

- **No retention or destruction for the record classes.** No de-identification sweep: nothing in `Students`, `RosterSnapshots`, `Entries`, `Results`, `SchoolRecords` or `AuditEvents` is ever deleted by the software. Until a school's schedule exists and the software supports applying it, disposal is a manual, approved database operation.
- **One device-side retention exception:** a `conflict` or `rejected` outbox row keeps the student's name until a person resolves it, which is why resolving parked items is a privacy task as well as a queue task. Name it in your PIA.

---

## 3. Export and disposal on decommission

The procedure for retiring an instance or migrating to another platform. **Written `[principal]` approval is required before step 5.**

**Export (do all of it before deleting anything):**

1. For each carnival, sign in as a carnival administrator and export `results.csv` and `standings.csv`; for any carnival that fed a district team, export `district.csv`. Each export writes its own audit row.
2. Export the record book and the audit trail. There is no CSV endpoint for the audit trail; take it from the database, for example
   `docker compose exec -T db psql -U <owner> -d <db> -c "\copy \"AuditEvents\" TO STDOUT CSV HEADER" > audit.csv`.
3. Take a final `pg_dump -Fc` of the whole database: the complete, restorable record, and what to keep if any class is still open.
4. Hand exports 1–3 to `[records custodian]`, who files them against the retention classes and confirms whether anything is a permanent record requiring transfer (for Victorian government schools, to PROV / State Archives). **Transfer anything permanent before step 5.**

**Disposal (only with written `[principal]` approval, once minimum retention has passed and no FOI request or legal hold applies):**

5. Stop the stack and delete its state: `docker compose down -v` removes `db-data`, `db-backups`, `dp-keys`, `caddy-data` and `caddy-config`.
6. Delete every other copy: the off-host encrypted dumps, the `~/pre-*.dump` files on the host, and any hypervisor snapshots or VM backups of the host (a full copy of the disk, plaintext unless the storage is encrypted).
7. Destroy the backup private key, so any missed ciphertext copy is unrecoverable.
8. Delete the host or VM and reclaim its storage; if the disk is being repurposed, have `[school ICT contact]` securely erase it.
9. Remove the identity-provider app registration and the groups mapped to Carnival App roles; remove any deploy keys or registry tokens the host used.
10. Wipe or re-enrol the shared carnival devices through MDM, so no outbox or roster cache survives. A personally owned device cannot be remote-wiped: ask its owner to sign out, which clears the roster and API caches, and to resolve or discard any parked outbox items.
11. Record the disposal: what was destroyed, when, by whom, under whose approval, against which retention class. File it with the school's records.

---

## 4. Open items for your school

| Item | Owner |
|---|---|
| Obtain the retention schedule (PROS 22/06 for Victorian government schools) and map a class to every entity above | `[records custodian]` |
| Confirm whether ratified school records are permanent records requiring transfer | `[records custodian]` |
| Approve the resulting schedule | `[principal]` |
| Decide how the schedule is applied until the software supports it (a manual, approved, recorded database operation) | `[records custodian]` + `[technical owner]` |
| Name the device-side retention exception (names on parked outbox rows) in the PIA | `[privacy officer]` |
| Record the fixed operational windows (idempotency keys 30 days, unpromoted staged rosters 7 days, resolved device outbox rows 7 days) in the PIA | `[privacy officer]` |

## In the code

- `api/src/CarnivalApp.Api/Imports/RosterImportService.cs`: promotion, rejection and the staged-row purge
- `api/src/CarnivalApp.Api/Data/RetentionSweep.cs`: the server retention sweep and its daily job
- `app/src/sync/retention.ts`, `app/src/db/roster.ts`, `app/vite.config.ts`: device-side retention
- `docker-compose.yml` (the `backup` service), `scripts/deploy/vm-deploy.sh` (pre-deploy dumps)
- `api/src/CarnivalApp.Api/Migrations/20260822112310_ProtectAuditEventsFromUpdateAndDelete.cs`: the audit trigger
