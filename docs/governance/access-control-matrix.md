# Access control matrix

> **Template.** Sections 1–4 describe how Carnival App enforces access, taken from the authorisation code; sections 5–6 are procedures for your school to adapt.
> Anything in `[square brackets]` is yours to fill in: owners, group names, where records are kept.
> Check it against the version you deploy; the per-endpoint 401/403 matrix is pinned by tests in `api/tests/CarnivalApp.Api.Tests`.

**All authorisation is server-side. UI hiding is not access control**: where a screen and an HTTP status disagree, the status is the control. Sign-in, roles and the audit trail are explained in [Authorisation and audit](../architecture/authorisation-and-audit.md).

---

## 1. The four roles

| Role | Scope |
|---|---|
| **SystemAdministrator** | School configuration, roster imports, role assignment, local accounts, **and** every carnival policy, so a school with one technical staff member is not locked out. A superset, not a peer. |
| **CarnivalAdministrator** | Carnival lifecycle including finalisation, events, schedule, officials, settings, house-point adjustments, records administration, reports and exports, category overrides, plus everything a Recorder can do. |
| **Recorder** | Marshals and records: creates entries and relay teams, places them in a race, heat and lane, captures and corrects results. |
| **Viewer** | Read-only carnival surface. No whole-roster search. |

A person may hold several roles; their rights are the union. Older role names (`EventAdministrator`, `Verifier`, `RecordsManager`, `ResultsOfficial`, `ReadOnlyViewer`) are still accepted as aliases and logged when used (`Identity/PlatformRoles.cs`). **Older audit rows may carry the old names, so an access review must read both vocabularies.**

Roles come from three sources, unioned on every request ([ADR-0048](../adr/0048-in-app-role-assignment.md)):

1. **Identity-provider groups**, mapped to roles by deployment configuration (`Identity__GroupToRoleMappings__<group-id>`).
2. **Bootstrap administrators** listed in configuration (`BOOTSTRAP_ADMIN_UPN`), read on every request and never stored. Provider accounts only.
3. **In-app role assignments** (School → Roles), granted by UPN or local username with a reason and revoked with a reason; rows are never deleted.

A **local account** (School → People, [ADR-0065](../adr/0065-built-in-local-accounts.md)) gets its roles from source 3 alone. A review that reads only one source sees part of the answer.

---

## 2. Policies the server enforces

| Policy | SystemAdministrator | CarnivalAdministrator | Recorder | Viewer |
|---|---|---|---|---|
| `carnivals:view` | allow | allow | allow | allow |
| `carnivals:marshal` | allow | allow | allow | deny |
| `results:submit` | allow | allow | allow | deny |
| `results:correct` | allow | allow | allow | deny |
| `carnivals:administer` | allow | allow | deny | deny |
| `school:administer` | allow | deny | deny | deny |
| `roles:administer` | allow | deny | deny | deny |
| `imports:view` | allow | deny | deny | deny |
| `imports:manage` | allow | deny | deny | deny |
| `accounts:administer` | allow | deny | deny | deny |

---

## 3. Resources and actions

Deny = HTTP 403. Unauthenticated = HTTP 401 on every row below. Routes are under `/api/v1`.

### Carnivals, events, schedule

| Action | Route | SysAdmin | CarnivalAdmin | Recorder | Viewer |
|---|---|---|---|---|---|
| List / read carnivals | `GET /carnivals`, `GET /carnivals/{id}` | allow | allow | allow | allow |
| Create / update a carnival | `POST /carnivals`, `PUT /carnivals/{id}` | allow | allow | deny | deny |
| Change status (open / close / **finalise**) | `POST /carnivals/{id}/status` | allow | allow | deny | deny |
| Apply an event template | `POST /carnivals/{id}/apply-template` | allow | allow | deny | deny |
| Read events | `GET /carnivals/{id}/events`, `.../events/{eventId}` | allow | allow | allow | allow |
| Schedule, generate, update or unschedule events; change status; open all scheduled | `POST`, `PUT`, `DELETE .../events…`, `.../events/{eventId}/status`, `.../events/open-scheduled` | allow | allow | deny | deny |
| Assign officials (advisory data) | `PUT .../events/{eventId}/officials` | allow | allow | deny | deny |
| Raise the bar; set attempts per height (high jump) | `POST .../events/{eventId}/heights`, `PUT .../attempts-per-height` | allow | allow | allow | deny |
| Read the program | `GET /carnivals/{id}/schedule` | allow | allow | allow | allow |
| Streams and slots (create, update, delete, place or remove an event) | `.../schedule/streams…`, `.../schedule/slots…` | allow | allow | deny | deny |
| Read event definitions | `GET /event-definitions` | allow | allow | allow | allow |
| Create, update or deactivate event definitions | `POST`, `PUT`, `DELETE /event-definitions…` | allow | allow | deny | deny |
| List event templates | `GET /admin/event-templates` | allow | allow | deny | deny |
| Preview / apply a template reconcile | `GET`, `POST /admin/event-templates/{id}/reconcile` | allow | deny | deny | deny |

### Entries, races, results

| Action | Route | SysAdmin | CarnivalAdmin | Recorder | Viewer |
|---|---|---|---|---|---|
| **Whole-roster eligible-student search** | `GET .../events/{eventId}/eligible-students` | allow | allow | allow | **deny** |
| List entries | `GET .../events/{eventId}/entries` | allow | allow | allow | allow |
| Create an entry / a relay team | `POST .../entries`, `POST .../team-entries` | allow | allow | allow | deny |
| Remove an entry with no result | `DELETE .../entries/{entryId}` | allow | allow | allow | deny |
| **Category override** ([ADR-0003](../adr/0003-event-division-and-category-overrides.md)) | `POST .../entries/{entryId}/category-override`, and the same field on entry creation (`403 CATEGORY_OVERRIDE_FORBIDDEN`) | allow | allow | **deny** | deny |
| List races | `GET /carnivals/{id}/races` | allow | allow | allow | allow |
| Create / update / delete a race | `POST`, `PUT`, `DELETE /carnivals/{id}/races…` | allow | allow | deny | deny |
| Materialise a division's race; place an entry in a race, heat and lane | `POST .../races/materialise`, `PUT .../entries/{entryId}/placement` | allow | allow | allow | deny |
| List results | `GET .../events/{eventId}/results` | allow | allow | allow | allow |
| Capture a result, an attempt or a high-jump mark | `POST .../entries/{entryId}/results`, `PUT .../attempts/{n}`, `PUT .../jumps/{height}` | allow | allow | allow | deny |
| Correct a result | `POST /results/{resultId}/correct` | allow | allow | allow | deny |

### Scoring, records, reports, devices

| Action | Route | SysAdmin | CarnivalAdmin | Recorder | Viewer |
|---|---|---|---|---|---|
| Standings, progress, adjustments list, carnival settings | `GET .../standings`, `.../progress`, `.../adjustments`, `.../settings` | allow | allow | allow | allow |
| Update carnival settings | `PUT /carnivals/{id}/settings` | allow | allow | deny | deny |
| Create / void a house-points adjustment | `POST .../adjustments`, `POST .../adjustments/{id}/void` | allow | allow | deny | deny |
| Read the record book | `GET /records` | allow | allow | allow | allow |
| Records administration; void a record | `GET /records/administration`, `POST /records/{id}/void` | allow | allow | deny | deny |
| **Reports screen and all three CSV exports** | `GET /carnivals/{id}/reports/results`, `.../standings.csv`, `.../results.csv`, `.../district.csv` | allow | allow | **deny** | **deny** |
| Report this device's sync status (own device only, else `403 DEVICE_ID_MISMATCH`) | `PUT /carnivals/{id}/devices/{deviceId}/sync-status` | allow | allow | allow | allow |
| List devices and their queue counts | `GET /carnivals/{id}/devices` | allow | allow | deny | deny |

Every CSV export writes an audit row (actor, format, row count) in the same transaction as the read, and the three `.csv` routes carry the `bulk-data` rate limit (default 10 requests a minute per signed-in subject). `district.csv` refuses unless the carnival is Finalised.

### Roster import: SystemAdministrator only

| Action | Route | SysAdmin | Everyone else |
|---|---|---|---|
| Upload and stage a roster file (rate-limited) | `POST /imports` | allow | **deny** |
| List batches; read import defaults | `GET /imports`, `GET /imports/defaults` | allow | **deny** |
| Read validation (rate-limited) | `GET /imports/{id}/validation` | allow | **deny** |
| Read reconciliation, which relays staged roster rows (rate-limited) | `GET /imports/{id}/reconciliation` | allow | **deny** |
| Approve / promote a batch | `POST /imports/{id}/approve`, `.../promote` | allow | **deny** |

This is the boundary the role split defends: `CarnivalAdministrator` gets nothing on the import surface, not even the read-only review.

### School configuration and roles

| Action | Route | SysAdmin | Everyone else |
|---|---|---|---|
| Read / update school settings (name, houses, colours, branding, the two sign-in settings) | `GET`, `PUT /school/settings` | allow | deny |
| List / grant / revoke in-app role assignments | `GET`, `POST`, `DELETE /admin/role-assignments` | allow | deny |
| List / add local accounts; reset, disable, enable, clear a lockout | `GET`, `POST /admin/local-accounts`; `POST /admin/local-accounts/{id}/reset`, `/disable`, `/enable`, `/clear-lockout` | allow | deny |

Nobody can revoke their own `SystemAdministrator` assignment (`409 ROLE_SELF_REVOKE`), or reset, disable, enable or clear the lockout of their own local account (`409 ACCOUNT_SELF_CHANGE`); another administrator can.

### Signed in, no role required

`GET /me`, `GET /version`, `GET /school/houses`.

For a signed-in **local account** only (a provider session is refused `403 NOT_A_LOCAL_ACCOUNT`): `GET /account`, `POST /account/password`, `POST /account/authenticator`, `POST /account/authenticator/confirm`. Each acts on the caller's own account, and each write re-checks the current password or the code.

### Anonymous endpoints (the complete list)

| Route | Why |
|---|---|
| `GET /health` | Liveness and readiness probe; also the idle guard's reachability probe |
| `GET /api/v1/auth/methods`, `POST /api/v1/auth/identify` | The sign-in box: whether a provider is configured, and whether a typed name signs in with a password or at the provider. Not on a demo host |
| `POST /api/v1/auth/local` | Local sign-in. Lockout per account and address; password checks limited host-wide. Not on a demo host |
| `POST /api/v1/auth/setup/inspect`, `POST /api/v1/auth/setup/complete` | Setup links: read a link without using it; set the password and use it. Not on a demo host |
| `GET /api/v1/auth/logout` | Sign-out, for any session. Not on a demo host |
| `GET /api/v1/auth/login` | OIDC sign-in; mapped only when Entra ID or ADFS is configured |
| `GET /api/v1/branding` | School name and brand colour, so the sign-in screen can be painted before a session exists. Carries nothing else |
| `/api/v1/demo/sessions` | Demo hosts only; never on a school deployment |
| `GET /openapi/*` | Development environment only |

Everything else is protected by a **fail-secure fallback policy**: a route that names no policy still requires a signed-in caller, so a new endpoint cannot be anonymous by omission (`IdentitySetup`). A test enumerates the endpoints so each anonymous route stays an explicit choice.

---

## 4. Authentication, MFA and sessions

| Control | How it works |
|---|---|
| Sign-in methods | **Local accounts** are built in ([ADR-0065](../adr/0065-built-in-local-accounts.md)), and **Microsoft Entra ID or on-premises ADFS 2019+** can sit beside them ([ADR-0007](../adr/0007-configuration-and-auth-modes.md)). With no provider, everyone signs in locally; beside one, by default only System administrators may hold a local account (the backup administrator), and a school setting can open it to everyone. **This deployment uses: `[local accounts only / Entra ID / ADFS]`; local accounts are open to `[System administrators only / everyone]`.** A development header scheme exists for local development; it is registered only in the `Development` environment and must stay off on any shared instance (`DEV_AUTH=false`). |
| Flow | One sign-in box: a local username asks for its password (and code); any other name goes to the provider. Provider sign-in is a confidential web app, authorisation code flow. Both end in a server-side cookie session. **No token ever reaches JavaScript.** An unauthenticated `/api` request answers 401, not a redirect. |
| **MFA for local accounts** | **The platform provides it.** Every local account is offered an authenticator app (TOTP, RFC 6238) at setup and on My account, with ten single-use recovery codes. It is **optional by default**; the school setting *Require an authenticator app* (School → School settings → Sign-in) makes everyone without one set it up at their next sign-in. **A school that must meet Essential Eight or ST4S's top tier turns it on.** No "remember this device". **This deployment: required `[yes / no]`, decided by `[name]`, `[date]`.** |
| **MFA for provider accounts** | **The identity provider's.** MFA, conditional access, device compliance, password policy, lockout and risk detection for Entra or ADFS accounts belong to the provider; the platform trusts the resulting ID token. **Confirmation that MFA and conditional access apply to every provider account holding a Carnival App role: `[school ICT contact]`, `[date]`.** Your privacy impact assessment should cite it. |
| Local passwords | At least 15 characters, no composition rules, no expiry; refused if on a bundled list of about 100,000 common and breached passwords or built from the school's name, house names or the person's names. Hashed with PBKDF2-HMAC-SHA512 at 220,000 iterations. |
| Local lockout | 10 failed attempts (password or code) for one account from one address within a rolling 15 minutes lock that account at that address until the oldest of them is 15 minutes old. Only failures count, so staff behind one school address are not throttled. A locked, disabled or unknown account gets the same answer as a wrong password. An administrator or the host command clears it. Password checks run a few at a time for the whole host. |
| Local account lifecycle | Created by a System administrator (School → People), never by self-registration. A single-use setup link, valid 24 hours, sets the password; the platform sends no email or SMS. Reset clears the password and authenticator; reset and disable end every session of the account at its next request. Accounts are disabled, never deleted. The first administrator, and recovery when nobody can sign in, come from a command on the host. |
| Role source | Group mapping, bootstrap administrators and in-app assignments, unioned (§1); a local account has in-app assignments only. Group IDs match exactly, including case: a mis-cased Entra object ID grants nothing. Unmapped groups are logged and grant nothing. |
| Session cookie | `__Host-carnival_session`, 12 hours, **fixed (not sliding)**, `HttpOnly`, `Secure`, `SameSite=Lax`. Sized for a carnival day including setup and pack-up. A **local** session is checked against its account on every request (disabled, reset, password changed, signed out, or no longer allowed ends it); a provider session is not. |
| Cross-site requests | Every write under `/api` must come from the app's own origin (`Sec-Fetch-Site`, else `Origin`); others answer `403 CROSS_SITE_REQUEST_REFUSED`. |
| Idle guard | After 60 minutes without a tap or key press, the app probes `/health`. If the server answers, it signs out. If not, it covers the screen with a privacy overlay and re-probes every five minutes; it does not sign out while unreachable, because the cached roster could not be fetched again. The server has no idle timeout of its own. |
| Sign-out purge | Clears the cached rosters and the service worker's cached API reads. A failed clear is retried at the next start, before anyone can sign in. **The outbox is never cleared**: queued captures belong to the device and keep the official who made them. A local sign-out also ends the session on the server. |
| Shared-device attribution | The official signed in at capture is sent with each queued write (`X-Captured-Actor`), so a handover never re-attributes queued work. The audit row records both the captured actor and the session that delivered it. |
| Account audit | Local-account changes (create, reset, disable, enable, lockout clear, setup, password change, authenticator enrolment, recovery-code use, host-issued links) are in `AuditEvents`. Sign-in attempts (time, account, client address, outcome; never the typed name) are kept 90 days in `SignInAttempts`. |
| Database access | Two PostgreSQL roles ([ADR-0047](../adr/0047-the-database-stays-on-the-host.md)): the owner role runs migrations only; requests run as an application role with data access but no DDL, and with `UPDATE`/`DELETE` revoked on `AuditEvents`. A database trigger also refuses changes to audit rows. `db` and `backup` sit on an internal Docker network the proxy cannot reach; port 5432 is not published. |
| Transport | Single origin behind Caddy on 443, with security response headers (nosniff, Referrer-Policy, Permissions-Policy, a `default-src 'self'` CSP with `frame-ancestors 'none'`). **HSTS is not sent** in any TLS mode. **This deployment's certificate: `[Caddy internal CA / school-issued / Let's Encrypt]`.** |

### Known access-control limits

1. **Provider group-membership changes take effect at the next sign-in.** Group claims are written into the cookie at sign-in and a provider session is not re-checked, so removing someone from a mapped group, or disabling their provider account, can take up to the **12-hour** cookie lifetime to bite. An **in-app** revocation, and any change to a local account, applies on the person's next request. To end every session at once, restart the API with an empty `dp-keys` volume, which signs everyone out.
2. **The development header scheme would let anyone assume any role** if it were enabled on a reachable host. It is refused outside the `Development` environment, but `DEV_AUTH` stays the highest-consequence configuration switch: check it is `false` at every review.
3. **With Caddy's internal CA**, every device must trust its root certificate, and users can learn to click through warnings. A school-issued or public certificate avoids this.
4. **The platform holds no access-review record.** `RoleAssignments` keeps grant and revocation history (who, when, why), but its review columns are unused, so the review record (§5) lives outside the platform.
5. **A database reader can defeat a local account's second factor.** Authenticator keys are stored unencrypted and recovery codes as unsalted hashes, so anyone who can read the database or a decrypted backup can produce codes. Keep database access and the backup private key to the named custodians.
6. **Local sessions:** a person cannot list or end their own other sessions except by changing their password; there is no "end everyone's sessions" short of the `dp-keys` restart; sign-out is a link (a GET), so another site can sign someone out.
7. **Without the authenticator setting, a stolen local password is enough to sign in to any local account without an authenticator app.** Beside a provider, only the local accounts (by default the System administrators) are exposed this way; enrol an authenticator on every local System administrator account.

---

## 5. Termly access review: procedure

If your school is a Victorian government school, the department's Information Security policy and Information Access guidance ask for access reviews at least termly, with system-administrator accounts scrutinised. Other schools should set a cadence: `[cadence]`.

**Owner: `[school ICT contact]`, with `[principal]` endorsing the system-administrator register.**

Each term, before the first carnival of the term:

1. List everyone holding a role, from all three sources: the members of each mapped group (`[group names]`), the bootstrap administrators in `.env`, and the active rows on School → Roles. List every local account on School → People, including those with no role.
2. For each person confirm: still employed or engaged at the school, still performing that carnival role, and the role is the least one that lets them do it.
3. Scrutinise every `SystemAdministrator` line by line: the role holds the roster-import surface and the whole carnival surface. Record a justification per person in the system-administrator register. Confirm each local System administrator account has an authenticator app (School → People) and that the backup administrator's password and recovery codes are where the register says.
4. Check the group-to-role mapping in `docker-compose.override.yml` names only the four current roles, and check `docker compose logs api` for unmapped-group warnings and retired-name alias lines.
5. Spot-check the audit trail for actions by anyone no longer on the list (`AuditEvents.ActorSubject` and `ActorRole`; remember the older role names).
6. Record the review date, the reviewer and every change made in `[where the school keeps access-review records]`, referenced from `[the school's ICT risk register]`.

## 6. Offboarding: procedure

Triggered by a staff member leaving, changing role, or the end of a volunteer's engagement. **Owner: `[school ICT contact]`.**

1. **Drain their devices first.** Queued work that syncs under their session is refused once their role is gone. The device list on the Manage screen (`GET /carnivals/{id}/devices`) shows pending and parked counts per device.
2. Revoke their in-app assignments (School → Roles) and remove them from every mapped group.
3. Disable a local account under School → People, which ends its sessions at their next request; disable or delete an identity-provider account per the school's standard offboarding.
4. **Sign out their devices.** A group-derived role on a provider account survives in the cookie for up to 12 hours. On any shared device they used, sign out in the app (which also clears the cached rosters and API reads), or leave it idle for 60 minutes with the server reachable.
5. If the person held `SystemAdministrator` or administered the host, confirm they hold no copy of the `.env`, the database passwords, the identity-provider client secret or the backup private key; rotate anything they held (see `incident-response-plan.md` §5).
6. Record the offboarding date and the steps completed alongside the termly access review.

## In the code

- `api/src/CarnivalApp.Api/Carnivals/CarnivalAuthorization.cs`, `School/SchoolAuthorization.cs`, `Identity/RoleAuthorization.cs`, `Identity/LocalAccounts/LocalAccountAuthorization.cs`, `Imports/ImportEndpoints.cs` (`ImportAuthorization`): the policy tables
- `api/src/CarnivalApp.Api/Identity/`: provider selection, group-to-role transformation, role assignment; `Identity/LocalAccounts/`: local sign-in, sessions, lockout, setup links, the host command
- `api/src/CarnivalApp.Api/Http/SameOriginWrites.cs`, `Http/RateLimiting.cs`
- `deploy/db/init-roles.sql`, `deploy/Caddyfile`, `docker-compose.yml`
