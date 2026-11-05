# Built-in accounts

Research behind [ADR-0065](../adr/0065-built-in-local-accounts.md) (accepted 2026-11-04). Where this note and the ADR differ, the ADR wins.

## Question

A school with no Entra ID, ADFS or other identity provider cannot sign in today: outside `Development`, an instance with no provider answers `401` to everything (ADR-0007). A school that does use Entra has no way in when Entra is down or misconfigured. We need sign-in that ships with the product, needs no outside service and no extra install steps, and is strong enough for student data.

## Recommendation

**Local accounts built into the API, with an authenticator app (TOTP) offered and recommended, and one school setting to require it.** Entra or another OIDC provider is an optional addition, not a replacement; beside one, a school setting limits local accounts to System administrators (the default) or opens them to anyone. One sign-in box routes each person to the right method. No email, no SMS, no extra service, no identity settings in `.env`.

- **ASP.NET Core Identity's core services** (`AddIdentityCore`, EF Core stores in the existing database) do the hard parts: password hashing, lockout, ending sessions when an account changes, TOTP and recovery codes. Shared framework plus one MIT package, so the licence check is unaffected.
- **The existing session cookie is unchanged.** A local sign-in creates the same 12-hour cookie an Entra sign-in does, so the captured actor, audit and the idle guard need no change. Role assignment needs one: it accepts only email-shaped UPNs today, so it must also grant to a local username.
- **Not a separate identity server.** Keycloak (about 1.25 GB) or authentik (about 2 GB) would not fit beside the stack on the 4 GB test VM, and would be a second system for a teacher to run and patch.

## How it works

### Install

No identity settings to configure; `SITE_ADDRESS` must already be the name devices use to reach the server, as for any install. At the end of the first deploy, `vm-deploy.sh` sees there is no administrator yet and prints a one-time link:

```
First administrator: open https://<site>/#/setup/Xk3...  (valid 24 hours)
```

Opening it: choose a username and a password, then "Set up an authenticator app (recommended)" or "Skip for now". That person is the first System administrator. Everything after that is in the app.

### Signing in: one box

Like Google, Slack and Auth0's "identifier-first" sign-in, the screen asks only for a username first.

- The name belongs to a local account → ask for the password, then the authenticator code if the account has one.
- Anything else, when Entra (or another provider) is configured → straight to Microsoft, with the name passed along so it is not typed twice.
- No provider configured → every name is a local account.

There is no second button and nothing to explain to staff. Local usernames are plain names (`jsmith`, `backup-admin`), never email addresses, so a local account can never capture an Entra user's sign-in. The cost: someone probing the screen can tell whether a plain name is a local account. Lockout and rate limiting make that knowledge worth little, and the large providers accept the same trade.

### MFA without email or SMS

TOTP needs neither. At enrolment the server shows a QR code holding a random secret (and the same secret as text, for typing in). The person's authenticator app (Microsoft Authenticator, Google Authenticator, the iPhone's Passwords app, and others) stores it. From then on, phone and server each work out the same six-digit code from the secret and the current time. Nothing is sent anywhere, and it works with the self-signed certificate and with no internet on the server.

- **Optional, recommended.** Setup offers it with "Skip for now". Accounts without it carry a reminder on My account; System administrators without it also see one on the School page.
- **School setting "Require an authenticator app"**, off by default. When on, anyone without one must enrol at their next sign-in. A school that answers ST4S at Tier 1, or works to Essential Eight, turns it on.
- **Lost phone:** one of the 10 recovery codes, or an administrator resets the account.

### Adding people

School → People → Add: username and display name. The app shows a one-time setup link and its QR code, valid 24 hours. The administrator passes it on however suits the school: in person, Teams, their own school email. No self-registration.

**Reset** (forgotten password, lost phone): the administrator presses Reset. That clears the password and authenticator, signs the person out everywhere, and gives a new setup link. **Disable** stops sign-in and ends sessions within a minute; accounts are never deleted, so history stays attributable.

### Backup administrator alongside Entra

This is the common pattern in SaaS products: single sign-on is the normal door, and a few administrators keep a password login that does not depend on the identity provider.

| Product | What it does when the identity provider fails |
|---|---|
| Slack | Workspace and org owners can bypass SSO and sign in with email and password, "even if the IdP is having issues" |
| Salesforce | Recommends not putting system administrators on SSO, so they can always log in and turn SSO off |
| Google Workspace | Super administrators sign in to the Admin console with their Google password, never redirected to the third-party IdP |
| Atlassian Guard | A separate authentication policy without SSO, holding at least one organisation admin; break-glass account protected by 2FA and monitored |
| GitHub Enterprise | Owners download SSO recovery codes in advance; each code bypasses SSO once, for 24 hours |
| Zoom | An explicit list of users allowed to keep email-and-password sign-in while SSO is enforced |

Ours follows Slack and Atlassian. The school creates a local account (`backup-admin`), gives it System administrator, turns on its authenticator, and keeps the password and recovery codes in the school's safe or password manager. The sign-in box routes that name to a password and everyone else to Microsoft. By default, beside a provider, only System administrators may hold a local account, so Entra's MFA and conditional access still cover everyone else. A school setting opens local accounts to anyone, for example carnival-day helpers with no school account.

**Last resort** when nobody can sign in at all: on the host, `docker compose exec api dotnet CarnivalApp.Api.dll accounts setup-link <username>` (name to be settled) prints a fresh setup link and clears any lockout, creating the account as a System administrator if needed. It is the same mechanism the install uses. Shell access to the host is the trust anchor, as `.env` already is.

## Security, kept small

| Area | Rule | Why |
|---|---|---|
| Password | At least 15 characters, any characters, no "must contain a symbol" rules, no expiry. Checked against a bundled offline list of common and breached passwords and the school's own words (name, houses, the username) | 15 is the single-factor minimum (ST4S A3, ISM 0421, NIST 800-63B rev 4). It matters because the authenticator is optional |
| Storage | Identity's PBKDF2-HMAC-SHA512, raised to 220,000 iterations (about 240 ms per sign-in on the test VM); old hashes upgrade on next sign-in | OWASP; ST4S A2 |
| MFA | TOTP offered to every account, recommended, enforceable by one school setting. No "remember this device" (devices are shared, ADR-0031) | ST4S A4 (offered: Tier 2; mandated: Tier 1) and A19 (an administrator can enforce MFA) |
| Guessing | 10 wrong attempts from one address locks the account for 15 minutes from that address (an administrator or the host command can clear it); failed attempts only are rate-limited per address and username, so staff behind one school address are not throttled. Wrong password and locked get the same answer | ST4S A2; an attacker cannot keep the backup admin locked out from everywhere |
| Sessions | The existing 12-hour cookie. Reset, disable and password change end sessions within a minute | ST4S A11 |
| Setup links | Single use, 24 hours, only a hash stored | ST4S A11 |
| Cross-site requests | Writes under `/api` must come from the app's own origin (`Sec-Fetch-Site`/`Origin` check). Also closes a gap that exists today | Login CSRF; sibling subdomains count as same-site for `SameSite=Lax` |
| Audit | Account changes in `AuditEvents`. Sign-in attempts (success, failure, lockout) in their own table, kept 90 days (ADR-0062) | ST4S PF55 |

Authenticator secrets and password hashes live in the database, so the existing encrypted backups cover them. They are **not** additionally encrypted with Data Protection: the `dp-keys` keyring is not backed up (`docs/governance/backup-and-recovery.md`), so a restore to a new host would silently break every enrolled authenticator.

### What the build must handle

Found by an independent design review of ADR-0065 (2026-11-04):

- **Deploy smoke test:** `vm-deploy.sh` expects `/api/v1/auth/login` to answer `302`, which exists only with OIDC; a no-provider install fails it. Test the local sign-in endpoint instead, and do not print a setup link while `SITE_ADDRESS` is `localhost`.
- **Sign-out:** `/auth/logout` exists only with OIDC, so a local user on a no-provider install would get a `404` and keep the cookie; with Entra, it would also send a local user to Microsoft's sign-out. Always map it; end the provider session only for provider sessions.
- **Security stamp validation** runs only for local sessions (an Entra session has no local user behind it), and disable rotates the stamp.
- **Setup links** survive link previews and safe-link scanners: a `GET` never uses them up. The address and time of use are audited and shown to the administrator.
- **Recovery codes** are stored hashed (Identity stores them as plain text). A restore brings back accounts disabled and codes used since the dump; the restore runbook re-applies those changes.
- **Unknown usernames** run a dummy hash, so timing does not reveal which names exist; the attempts table records them as "unknown", never the typed text, which is sometimes a password.
- **TOTP replay:** remember the last accepted time step per account so a code seen over a shoulder cannot be reused within its window.
- **Role assignment and bootstrap:** grants accept a local username; a local username that matches a `BOOTSTRAP_ADMIN_UPN` entry is refused.
- **Neutral examples:** the role-grant error still gives a `vic.edu.au` address as its example; use `name@example.org`.
- **Blocklist provenance:** record the bundled list's source and licence for the licence check.

### Left out on purpose

- **Email and SMS:** not needed for MFA or setup links. Could come later for self-service reset.
- **Online breached-password check:** the bundled list covers it without outbound traffic from a school DMZ.
- **Passkeys:** .NET 10 supports them, but browsers refuse them on a self-signed certificate. A later addition.
- **Configurable password rules, a separate identity server, a "System administrators only" level for the authenticator setting.** Generic OIDC is separate work.

## The test VM (192.168.0.65)

Checked over SSH on 2026-11-04: Debian 13, 2 vCPU (Xeon E5-2407 v2), 3.8 GiB, 16 GB disk; Docker installed, fresh clone at `8272a76`, no `.env`, nothing running. Compose limits already total 2.8 GiB. PBKDF2-HMAC-SHA512 at 220,000 iterations measured about 240 ms.

Proving it there: deploy with no identity provider configured, follow the printed setup link, add a second account, reset it, turn on "Require an authenticator app", and run a scripted password-guessing loop against the sign-in endpoint to confirm lockout and the address limit hold while `/health` stays responsive.

## Sources

- [ST4S Supplier Guide 2025.1 v1.0](https://st4s.edu.au/wp-content/uploads/2026/04/Safer-Technologies-4-Schools-Supplier-Guide-2025.1-v1.0-1.pdf): criteria A2–A4, A11, A19, PF55
- [ASD Essential Eight maturity model and ISM mapping](https://www.cyber.gov.au/publication/essential-eight-maturity-model-and-ism-mapping)
- [OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
- NIST SP 800-63B rev 4, via secondary summaries ([MIT CSAIL](https://tig.csail.mit.edu/excerpts/pass-requirements/), [Enzoic](https://www.enzoic.com/blog/nist-sp-800-63b-rev4/)); check the NIST text before quoting a number
- Break-glass patterns: [Slack SAML SSO](https://slack.com/intl/en-au/help/articles/203772216-SAML-single-sign-on), [Salesforce SSO setup](https://developer.salesforce.com/docs/atlas.en-us.196.0.securityImplGuide.meta/securityImplGuide/security_sso_setup.htm), [Google Workspace super administrator SSO](https://knowledge.workspace.google.com/admin/apps/super-administrator-sso), [Atlassian ACCESS-1836](https://jira.atlassian.com/browse/ACCESS-1836) and [break-glass accounts](https://support.atlassian.com/ja/organization-administration/docs/using-a-break-glass-account-for-emergency-admin-access/), [GitHub SSO recovery codes](https://docs.github.com/en/enterprise-cloud@latest/organizations/managing-saml-single-sign-on-for-your-organization/accessing-your-organization-if-your-identity-provider-is-unavailable), [Zoom community: SSO exceptions](https://community.zoom.com/t5/Zoom-Meetings/Require-SSO-Login-for-all-but-a-few-logins/m-p/43274)
- [Auth0: identifier-first login](https://auth0.com/docs/login/universal-login/identifier-first)
- [ASP.NET Core passkeys](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/passkeys) (.NET 10)
- [`PasswordHasher` source](https://github.com/dotnet/aspnetcore/blob/main/src/Identity/Extensions.Core/src/PasswordHasher.cs) (default 100,000 iterations)
- [Red Hat build of Keycloak sizing](https://docs.redhat.com/ko/documentation/red_hat_build_of_keycloak/26.0/html/high_availability_guide/concepts-memory-and-cpu-sizing-); [authentik requirements](https://shop.zimaspace.com/pages/authentik-hardware-requirements) (secondary)
