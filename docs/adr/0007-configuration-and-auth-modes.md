# ADR-0007: Configuration/administration model and authentication modes

- **Status:** Accepted (2026-07). Partly superseded by [ADR-0061](0061-demo-showcase-per-visitor-instances.md): the public demo has its own sign-in provider; header sign-in is never enabled on a shared deployment.

## Context

School name, timezone, houses, the event catalogue, scoring rules, novelty categories, the import mapping and identity settings are all school-specific, and school-specific rules must be configuration or data, never code forks. Separately, the platform must authenticate against whatever the school runs: Entra ID in the cloud, or on-premises ADFS, which many schools still use for line-of-business software. And a browser app must sign in without exposing tokens to JavaScript.

## Decision

### Configuration is data, in layers

- **Deployment configuration** (`.env`, an optional compose override file, an optional mounted `appsettings.School.json`) holds infrastructure settings: database, identity provider, timezone, site address, and the first-start seed for the school's name, houses and colours.
- **Database-resident data** holds everything a school changes in the life of an instance (school settings, houses, the event catalogue, carnival scoring rules, role assignments), edited in the app by administrators, audited, and versioned where it affects results ([ADR-0045](0045-school-settings-in-app.md), [ADR-0048](0048-in-app-role-assignment.md), [ADR-0055](0055-the-school-area.md)). The earlier layer seeds the later one and never stops working as the bootstrap.
- A portable export and import of the whole configuration is a direction, not built.

### The identity provider is a per-school configuration choice

- `Identity:Provider` (`Entra`, `Adfs`, `DevAuth`, `Demo`) selects explicitly; unset, it is inferred from which of the `AzureAd` or `Adfs` sections is populated. `Demo` is never inferred.
- **Entra ID** uses Microsoft.Identity.Web (including group-claim overage handling). **ADFS 2019+ or another OIDC provider** uses the standard OpenID Connect handler, authorisation-code flow.
- **Authorisation is provider-agnostic.** Both providers feed the same group → role transformation and the same policies. The one difference, the group claim's type and value shape, is absorbed by configuration: `Identity:GroupsClaimType` and `Identity:GroupToRoleMappings`.
- **Development header sign-in** exists only in the `Development` environment with no provider configured. Anywhere else, an instance with no provider authenticates nobody and answers `401`.

### Sign-in is a server-side cookie session

- The API is a confidential OIDC client. The PWA signs in through a full-page navigation to `/api/v1/auth/login`; the callback sets an http-only session cookie; the app reads `/api/v1/me` for subject, display name and roles. No token reaches JavaScript.
- **API calls never redirect.** An unauthenticated or forbidden `fetch` under `/api` gets a JSON `401` or `403`, not a redirect to the provider, so the app can tell "signed out" from "offline". Browser navigations still redirect.
- `returnUrl` is honoured only for app-local paths, so the login endpoint is not an open redirect.

How it works: [authorisation, roles and audit](../architecture/authorisation-and-audit.md), [configuration reference](../admin/configuration.md).

## Consequences

- A second school can bring its own identity provider by configuration.
- Switching provider is a configuration change, not a code change.
- The weak schemes cannot reach a school deployment by misconfiguration: header sign-in needs the `Development` environment, and the demo provider refuses to start beside a real one.

## Rejected alternatives

- **MSAL.js public client** (tokens in the browser, the API as a JWT-bearer resource): more attack surface for less security, when the backend is already a cookie-based confidential client.
- **Header sign-in on a synthetic shared deployment** as a demo mode: anyone who can reach the host can claim any role; replaced by the demo's own provider.
