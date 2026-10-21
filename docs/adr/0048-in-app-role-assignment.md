# ADR-0048: In-app role assignment, layered over Entra groups

- **Status:** Accepted (2026-09). The Roles page's place in the School area is set by [ADR-0055](0055-the-school-area.md).

## Context

A platform role was identity-provider group membership mapped by per-deployment configuration ([ADR-0007](0007-configuration-and-auth-modes.md)) onto the four roles of [ADR-0016](0016-role-model-simplification.md). That works for a school with its own tenant. **Most schools will not have one.** They sign in through a department tenant where a school cannot register an app or create security groups, and where a large tenant often emits no `groups` claim at all. There, everyone authenticates and everyone gets 403.

Two smaller forces point the same way: the termly access review was an exercise recorded outside the platform, although a `RoleAssignments` table with review columns had existed, unused, since the initial schema; and [ADR-0045](0045-school-settings-in-app.md) opened the in-app admin area with role assignment as its next slice.

## Decision

1. **Authentication does not move; authorisation does.** The identity provider stays the only one: sign-in, MFA, conditional access and lockout remain its job. There is no local password, no local credential and no local sign-in path, and this decision is not licence to add one. What becomes local is the answer to "which roles does this already-authenticated subject hold?"
2. **Three sources, merged by union.** The claims transformation grants the roles from the group mapping, from the bootstrap list, and from active `RoleAssignment` rows for the subject. A school that keeps its groups loses nothing; a department-tenant school runs from assignments alone. Union is also the only merge that takes access from nobody on deployment.
3. **Bootstrap comes from configuration and stays there.** `Identity:BootstrapAdministrators` (UPNs or subject ids) grants `SystemAdministrator`, read on every request and never stored. A deployment locked out for any reason is recovered by editing `.env` and restarting, never by a hand-written database change. Bootstrap administrators cannot be revoked in the app; the Roles screen shows them as configuration-derived.
4. **Nobody can revoke their own `SystemAdministrator`** (`409 ROLE_SELF_REVOKE`); another administrator can. The rule is not "the last administrator", because group-derived administrators are visible only in the claims of whoever is signed in, so that count cannot be computed. The bootstrap list is the floor underneath.
5. **Assign by UPN; bind the stable subject id on first sign-in.** Volunteers are set up in the week before a carnival, before they have ever signed in, and a department-tenant administrator cannot look up an object id. On the person's first authenticated request the unbound row is matched by UPN, the stable subject id (`oid`/`sub`) is written, and the binding is audited. From then on the row matches by subject id only, so a UPN rename cannot move a role. A display name never binds or matches.
6. **Effect is immediate.** The transformation runs on every authenticated request, so a grant or revocation applies on the target's next API call inside a live session. Reads go through a cached provider invalidated synchronously on write; a failed read serves the last good list rather than failing every request. A revoked user's queued outbox items are refused when they drain, so offboarding drains the device first.
7. **API and data.** `GET|POST|DELETE /api/v1/admin/role-assignments` under a separate `roles:administer` policy, SystemAdministrator only, kept apart from `school:administer` because granting roles is the line an access review reads on its own. A grant carries a UPN, a role and a reason; a revocation carries a reason and revokes every active row for that role held by that person. Rows are never deleted; revocation stamps who, when and why, so the table is its own history. One active grant per person per role is a filtered unique index. Grants, revocations and bindings are audited (`role-assignment.grant`, `.revoke`, `.bind`).

How roles are resolved and enforced: [authorisation and audit](../architecture/authorisation-and-audit.md).

## Consequences

- A department-tenant school configures a bootstrap administrator, no group mapping, and assigns everyone else in the app.
- The assignment list plus the audit trail is the access-review evidence; an in-app review workflow is a separate decision.
- UPNs and display names stored here are staff and volunteer identifiers, already written to every audit event; nothing else about the person is kept.
- **Residual risks:** a school can now grant `SystemAdministrator` (including roster import) through a screen, so a compromised administrator session escalates further than before; audit detects, it does not prevent. The union is permissive by construction, so a review that reads one source misses half the answer. UPN binding is trust-on-first-use: a UPN reassigned before first sign-in binds the new holder. Revocation mid-carnival strands that person's queued captures.

## Rejected alternatives

- **Local accounts and passwords:** a new credential surface with MFA, lockout, reset and breach obligations a school cannot meet.
- **Entra app roles:** right in a tenant we control, but need admin consent a school in a department tenant cannot obtain.
- **Replacing the group mapping:** a breaking authorisation change for schools whose groups work.
- **Assigning by object id, or requiring a prior sign-in:** administrators cannot look up ids, and two passes per volunteer at the busiest moment is unworkable.
