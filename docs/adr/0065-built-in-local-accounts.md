# ADR-0065: Built-in local accounts, with an optional authenticator app

- **Status:** Accepted (2026-11). Partly supersedes [ADR-0007](0007-configuration-and-auth-modes.md): outside `Development`, a deployment with no identity provider signs in with local accounts instead of authenticating nobody, and local accounts can sit beside Entra or ADFS. Its configuration model, cookie session and provider-agnostic authorisation stand. Partly supersedes [ADR-0048](0048-in-app-role-assignment.md): its rejection of local accounts.

## Context

Sign-in today depends entirely on the school's identity provider. A school without Entra or ADFS cannot use the platform at all: outside `Development`, an instance with no provider answers `401` to everything. A school with Entra has no way in when Entra is down or a group mapping is wrong, short of editing `.env` on the host. ADR-0048 rejected local accounts because of the MFA, lockout, reset and breach obligations they bring; this decision takes those obligations on in the platform, so the school does not have to. The fallback has to be as strong as the school-sector assessments expect (ST4S, Essential Eight) and add no identity settings to the install. The options, the requirements and the patterns other products use are in [research: built-in accounts](../research/2026-11-04-built-in-accounts.md).

## Decision

1. **Local accounts are built into the API**, using ASP.NET Core Identity's core services in the existing database, and produce the same session cookie a provider sign-in does. A local account's subject is `local:<id>` and it carries no UPN, so it can never match a provider user's role assignment or a bootstrap entry. Roles are granted to a local account by its username. Entra, ADFS and later a generic OIDC provider are optional additions. No separate identity server is run.
2. **One sign-in box.** The person types a username. A local account's name asks for a password; any other name goes to the configured provider. Local usernames are plain names, never email addresses.
3. **Beside a provider, a school setting decides who may hold a local account:** System administrators only (the default, the backup administrator) or anyone. With no provider, everyone signs in locally. The rule is checked on every request, so switching to administrators only, or removing a local account's administrator role, signs that person out at their next request.
4. **Accounts are created by administrators, never by self-registration.** Creating or resetting an account produces a single-use setup link, valid 24 hours, which the administrator hands over by whatever channel suits the school; opening the link does not use it, setting the password does. The platform sends no email or SMS. Reset clears the password and authenticator; reset and disable end every session of that account. Accounts are disabled, never deleted.
5. **The authenticator app (TOTP) is optional and recommended.** Every account is offered it at setup, with recovery codes. One school setting, off by default, requires it for everyone, from each person's next sign-in; open sessions run to their end. There is no "remember this device", because devices are shared ([ADR-0031](0031-shared-device-hygiene.md)).
6. **Passwords** are at least 15 characters (the single-factor minimum, since the authenticator is optional), with no composition rules or expiry. They are checked against a bundled offline list of common and breached passwords and against the school's own words (its name, house names, the username), and hashed with PBKDF2-HMAC-SHA512 at OWASP's work factor. Ten failed attempts from one address lock the account at that address for 15 minutes (the Windows security baseline's figures); failures are counted per account and address, so an attacker cannot lock an account out from everywhere, and successes are never throttled, so staff behind one shared school address can sign in. Password checks run a few at a time, sized from the host's processors, so a password-guessing attack slows sign-in and nothing else. None of these figures is a setting.
7. **The first administrator and recovery come from the host.** The first deploy prints a setup link for the first System administrator, with or without a provider, so every school starts with a local backup administrator; a host command prints a new one and clears a lockout when nobody can sign in. Shell access to the host is the trust anchor, as `.env` already is. `BOOTSTRAP_ADMIN_UPN` stays, for provider accounts.
8. **Account changes are audited; sign-in attempts are kept 90 days** in their own table, outside the permanent audit trail, as a further operational store under [ADR-0062](0062-retention-windows-for-operational-data.md)'s sweep.
9. **Writes under `/api` must come from the app's own origin**, checked on every unsafe request, since a password form makes cross-site sign-in requests worth refusing.

The development header sign-in and the demo's own provider are unchanged; one end-to-end suite signs in through real local accounts so the production path is exercised.

## Consequences

- A school can install and sign in with no identity provider and no identity settings.
- A school on Entra survives an Entra outage or misconfiguration through its backup administrator, and by default Entra's MFA and conditional access still cover everyone else.
- The platform now owns password and MFA policy for local accounts. The governance pack, which says MFA belongs to the identity provider, has to be rewritten. With the authenticator setting off, an install does not meet ST4S's top tier or Essential Eight; a school that needs either turns it on.
- Staff usernames and 90 days of sign-in addresses become new staff data in the data inventory.
- After the 60-minute idle sign-out, a local user retypes their password, and their code if enrolled; an Entra user is often signed straight back in by Microsoft.
- Passkeys are left for later: browsers refuse them on the self-signed certificate a fresh install uses.
- Follow-up: generic OIDC (Google Workspace, Okta and others) plugs into the same sign-in box.

## Rejected alternatives

- **A bundled identity server (Keycloak, authentik, ZITADEL):** 1.25–2 GB of memory each, more than the 4 GB host has spare, and a second system for a school to run and patch. It remains possible as a school's own provider.
- **Local accounts only when no provider is configured:** leaves an Entra school with no way in when Entra fails.
- **A separate "use a local account" button:** a second visible door to explain to staff; the single box routes without it.
- **Local accounts in development and screenshots, replacing the header sign-in:** every run would sign in with a password and code, and switching roles while developing would mean signing out and in. One end-to-end suite covers the real path instead.
- **Mandatory MFA:** a school would have to enrol a phone for every recorder and helper before its first carnival; the setting covers schools that must mandate it.
- **A "System administrators only" level for the authenticator setting:** keeps the setting a single switch; a school protecting administrators turns it on for everyone.
- **Email or SMS for setup and reset:** needs a mail server or SMS gateway a self-hosted install does not have. TOTP needs neither.
- **ASP.NET Core Identity's ready-made endpoints (`MapIdentityApi`):** opens self-registration and issues bearer tokens to the browser.
