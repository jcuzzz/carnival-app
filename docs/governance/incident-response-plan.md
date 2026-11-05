# Incident response plan

> **Template.** The detection, containment and evidence steps (§4, §5, §7) are specific to Carnival App's stack; roles, contacts and notification routes are your school's to fill in.
> Anything in `[square brackets]` is yours. Fill in the contacts and reporting references before real data is loaded, not during an incident.
> Rehearse it once (a tabletop exercise) before the first carnival that uses real student data.

---

## 1. Scope

Any confidentiality, integrity or availability event affecting the school's Carnival App instance: the Docker Compose stack (`api`, `db`, `backup`, `proxy`) and its host, the PWA on staff and shared devices, the local accounts and the identity-provider app registration used for sign-in, the roster import and CSV export paths, and the backups.

**Out of scope, escalate elsewhere:** anything affecting the student information system, the school network or the identity provider generally. Those follow the school's own incident processes; this plan hands off to them.

---

## 2. Roles

| Role | Who | Responsibility |
|---|---|---|
| Incident lead | `[school ICT contact]` | Runs the response, decides containment, keeps the timeline |
| Decision and notification authority | `[principal]` | Approves service shutdown, data restoration and every external notification |
| Technical responder | `[technical owner of the instance]` | Diagnosis, containment on the host, remediation, evidence capture |
| Records implications | `[records custodian]` | Advises where results or record-book integrity is affected |
| Communications | `[principal or delegate]` | Any message to staff, students or families |

Only `[principal]` (or a delegate named in writing) notifies anyone outside the school.

---

## 3. Severity

| Level | Definition | Examples | Response |
|---|---|---|---|
| **S1: critical** | Confirmed unauthorised access to, or loss of, student data; or the platform is unusable during a carnival | Database exfiltration; ransomware on the host; a lost or stolen device holding an undrained outbox with student names; `district.csv` sent to the wrong recipient | Immediate. Lead engaged within the hour; `[principal]` informed the same day; external notification per §6 |
| **S2: high** | Credible risk of unauthorised access; integrity of the record book in question | Client secret or database password disclosed; an unexpected `SystemAdministrator`; a local System administrator's password or recovery codes lost or disclosed; a setup link sent to the wrong person; audit rows missing or the audit trigger disabled; `DEV_AUTH=true` on a reachable instance; a plaintext dump or VM snapshot copied off the host | Same day. Contain first, then assess |
| **S3: moderate** | Availability or correctness fault with no data exposure | Stack down outside a carnival; failed migration; backups not running (`.last-success` stale); restore drill fails | Next working day |
| **S4: low** | Hygiene finding with no live exposure | Dependency advisory with no reachable path; a device left signed in and recovered | Track in `[the school's issue register]` |

Carnival day raises severity by one level: a fault that is S3 in an ordinary week is S2 at the pool.

---

## 4. Detection

| Source | What it shows |
|---|---|
| `AuditEvents` table | Every write with actor, roles, time, prior and new values, reason; every CSV export with actor, format and row count. **The primary investigative source.** There is no API for it; query the database |
| `GET /health` | Liveness and database reachability |
| Container logs | `docker compose logs api db proxy backup`. Capped at 10 MB × 3 files per service, so **copy them early**; a busy day can rotate evidence away |
| Identity-provider logs | Sign-in and audit logs (for Entra ID, in the tenant). Access and retention: `[school ICT contact]` |
| `SignInAttempts` table | Every local sign-in attempt for 90 days: time, account, client address, outcome (wrong password, wrong code, locked out, recovery code used…). Query the database |
| School → People | Per local account: locked out, last sign-in, and when and from which address its setup link was used |
| Device list | Manage screen (`GET /carnivals/{id}/devices`): pending and parked counts per device, the last official and last-seen time. How a lost device holding captures is spotted |
| Stuck-outbox banner | The app warns on a device whose queue is not draining |
| Backup freshness | `.last-success` in the backup volume; the restore drill fails on a stale dump |
| User report | An official or organiser reporting something wrong. Route to `[school ICT contact]` |
| Vulnerability report in the software | Report it upstream per `SECURITY.md` |

---

## 5. Containment

Work top down; each step is independent and reversible except where noted. Record the time of each action.

1. **Cut access without losing data.** `docker compose stop proxy`: the site stops answering while the database and volumes stay intact. Devices keep capturing into their outboxes and deliver when service returns.
2. **Revoke identity.** Revoke the person's in-app assignments (School → Roles), which applies on their next request. A local account: **Disable** it under School → People, which ends its sessions at their next request (§5a). In the identity provider, disable the account, revoke its sessions and remove it from every mapped group. A group-derived role survives in the platform's **12-hour fixed cookie**, with no server-side session store to revoke. To end every session at once, restart the API with an empty `dp-keys` volume; this signs **everyone** out, so it is a deliberate S1/S2 act.
3. **Rotate the identity-provider client secret**, where a provider is used. Create a second secret → set it in `.env` (`ENTRA_CLIENT_SECRET`, or your ADFS secret variable) → `docker compose up -d api` → delete the old secret. The overlap avoids downtime.
4. **Rotate database credentials.** There are two roles: the owner (`POSTGRES_PASSWORD`, migrations only) and the application role (`APP_DB_PASSWORD`). Change the password in the database (`ALTER ROLE … PASSWORD`) and in `.env`, then restart the affected services; `deploy/db/init-roles.sql` re-runs on every deploy. Port 5432 is not published, so this contains an insider or a host compromise, not a network attacker.
5. **Full stop.** `docker compose down` (**never `-v`**, which deletes the volumes and with them the database, the backups and the evidence). Snapshot the host before any remediation that writes to disk.
6. **Isolate the host** if host compromise is suspected: disconnect its network rather than shutting it down, so memory-resident evidence survives.
7. **Recover data** only after evidence is preserved: restore per `backup-and-recovery.md` §5. Restoring overwrites the current database; copy it first.
8. **Devices.** For a lost or stolen device, check its queue depth in the device list first, then wipe it through `[the school's MDM]`. **A personally owned device cannot be remote-wiped**: record what its outbox may have held (names on `conflict` and `rejected` rows are kept until resolved) and treat it as a disclosure.

### 5a. Local accounts

| Situation | Do |
|---|---|
| **A backup administrator's password, phone or recovery codes are lost, or may be known to someone else** | Another System administrator **resets** the account under School → People: that clears the password, authenticator and recovery codes, ends every session at its next request, and gives a new setup link. With no other administrator, run on the host: `docker compose exec -T api dotnet CarnivalApp.Api.dll accounts setup-link <username>`, which does the same and prints the link. Have the owner set a new password and authenticator, and store the new recovery codes. Check the audit trail and `SignInAttempts` for use of the account since the loss |
| **A person is locked out from inside the school network** | Lockout is per account and address, so someone guessing from the school's shared address locks the account there only. The owner signs in from another network (a phone's mobile data), or a System administrator presses **Clear lockout**, or the host command above clears it (it also resets the account). Look in `SignInAttempts` for the failures' address and times: a lockout nobody caused by mistake is a guessing attempt |
| **A setup link may have leaked** (wrong recipient, a shared chat, a screenshot) | **Reset** the account (or issue a new link with the host command): the new link revokes the old one. If People shows the old link as used and the owner did not use it, treat the account as compromised: disable it, then reset it |
| **An unexpected local System administrator** | Disable the account, revoke its role, and check the audit trail for `local-account.create`, `role-assignment.grant` and `local-account.setup-link` rows (actor `host` means the host command, so the host itself may be compromised) |

---

## 6. Notification

| Recipient | Trigger | Timing |
|---|---|---|
| `[principal]` | Any S1 or S2 | Immediately |
| `[the school's system or department security contact]` | Any information security incident | `[timeframe]` |
| `[privacy regulator]` | Where the incident meets its notification threshold | `[timeframe]`; `[principal]` sends |
| Affected students and families | Where `[principal]` judges notification appropriate | As advised |
| Carnival App maintainers | A suspected vulnerability in the software itself | Privately, per `SECURITY.md` |

**If your school is a Victorian government school:** the department's Information Security policy requires schools to report information security incidents to the department immediately (through the Service Desk / IT Security), not after the investigation. Where an incident meets the threshold of OVIC's information security incident notification scheme under the Victorian Protective Data Security Framework, OVIC is notified on department advice. The Commonwealth Notifiable Data Breaches scheme (Privacy Act 1988) does not apply to a Victorian government school; use the department's and OVIC's processes, not the OAIC's. Record the exact references here: Service Desk reporting route `[reference]`; OVIC notification form and timeframe `[reference]`.

Other schools (Catholic, independent, other states) record their own obligations here, including whether the Notifiable Data Breaches scheme applies: `[obligations]`.

---

## 7. Evidence preservation

- **The audit table is append-only in the database, not just in the application.** A trigger refuses `UPDATE` and `DELETE` on `AuditEvents`, and the application role has those privileges revoked. Audit rows survive deletion of the entities they describe, because references are soft (type and id), never foreign keys.
- **Never run `docker compose down -v`** during an incident, and never prune volumes.
- **Copy container logs to a file immediately**: they rotate at 10 MB × 3.
- **Snapshot the host before remediating.** A snapshot is a full copy of the disk, plaintext unless the storage is encrypted; store and delete it like a backup.
- Export the relevant `AuditEvents`, `ImportBatches`, `Results` and `ResultRevisions` rows to a file held with the incident record.
- Preserve identity-provider sign-in logs before their retention window closes, and export the relevant `SignInAttempts` rows before they reach 90 days.
- Keep a timeline: detection time and source, each containment action with its time and actor, every decision and who made it.

---

## 8. Post-incident review

Within `[ten]` working days of closure, `[school ICT contact]` and the technical responder write a short review: what happened, how it was detected, how long each phase took, what data was involved, what fixed it, and what changes follow. Add it to `[the school's ICT risk register]` and update this plan if a step proved wrong or missing. If the cause is a defect in Carnival App, report it upstream (`SECURITY.md` for anything security-relevant).

---

## 9. Preparation checklist

| Item | Owner |
|---|---|
| Name the incident lead, decision authority and communications delegate (§2) | `[principal]` |
| Record the external reporting references and timeframes (§6) | `[school ICT contact]` |
| Out-of-hours contact path for a carnival-day S1 | `[principal]` |
| Confirm MDM coverage of the shared carnival devices | `[school ICT contact]` |
| Name who holds the backup administrator's password and recovery codes, and who has shell access to the host | `[school ICT contact]` |
| Run one tabletop exercise and record it | `[school ICT contact]` + `[technical owner]` |
