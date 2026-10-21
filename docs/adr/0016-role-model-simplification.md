# ADR-0016: Four roles, not six

- **Status:** Accepted (2026-08). Supersedes [ADR-0009](0009-administrators-can-record-results.md). Depends on [ADR-0017](0017-automatic-result-validation.md), which removed human verification.

## Context

The original six roles (System Administrator, Event Administrator, Results Official, Verifier, Records Manager, Read-only Viewer) described how a carnival is *described*, not how it is *staffed*. Most schools do not have a full team, and staff cover several jobs. The model kept hitting the same seam: an administrator who could create entries could not record results ([ADR-0009](0009-administrators-can-record-results.md)), and a results official bound to assigned events drew a retryable refusal for a lane in a race spanning two divisions, which halted that device's whole queue.

Two roles were thinner than their names: Records Manager could only read, and once [ADR-0017](0017-automatic-result-validation.md) made verification automatic, Verifier had no distinct power left. Marshalling is something a person does at the venue, not an app role.

The verifier step was a real, enforced separation of duties. It held only while nobody was in two roles, and roles come from group membership: a school with one capable staff member puts them in both groups and the property disappears with no code change and nothing on screen.

## Decision

### 1. Four roles

| Role | Holds |
|---|---|
| **SystemAdministrator** | School configuration, roster imports, role assignment; also everything a carnival administrator can do, so a one-technician school is not locked out. |
| **CarnivalAdministrator** | Running carnivals: setup and lifecycle including finalisation, schedule, races, settings, adjustments, records, reports, category overrides, and result capture and correction. |
| **Recorder** | The deck: creating entries and relay teams, placing them in races and lanes (and so heats), capturing results, and correcting any result that is not yet final. |
| **Viewer** | Read only. |

A Recorder may correct any result in the carnival, not only their own: otherwise recorder A's typo is unfixable once A has gone home. The category override stays with carnival administrators: it is the eligibility control, not a marshalling act.

### 2. Nothing replaces the lost separation

The audit trail already answers "did one person take this result from capture to the record book?": every result keeps its capture-time recorder, every correction an immutable revision with its actor, and finalisation its actor and role. No new mechanism is added.

### 3. The per-event assignment gate is retired

A Recorder may capture for any division. Official assignments stay as advisory data: they drive "My events" and say who is expected where, but gate nothing. The gate was a coordination aid, not a security control: capture still needs a named, authenticated Recorder, and every action is audited.

### 4. Policies and compatibility

- Marshalling has its own policy, `carnivals:marshal`, separate from `carnivals:administer`. Explicit race administration (create, rename, schedule, delete) is administrator-only; materialising a division's race while placing swimmers is a marshal act ([ADR-0018](0018-race-first-capture.md)).
- Roles are deployment-wide, not per carnival.
- The retired role names are accepted as aliases (Event Administrator, Verifier and Records Manager → CarnivalAdministrator; Results Official → Recorder; Read-only Viewer → Viewer), and each use is logged, so existing group mappings keep working.
- Audit history is not rewritten: old role names remain readable.

The policy matrix: [authorisation § roles](../architecture/authorisation-and-audit.md#roles).

## Consequences

- A school can staff a carnival: one person runs it, another records, and nobody else is needed for the software to work.
- **Residual risk, stated plainly:** Recorder is more powerful than Results Official was. It can enter any eligible student and place them in a lane, and a lane and heat decide house points. The mitigations are full audit (actor, role, prior and new values, both identities on a queued write) and that a Recorder cannot finalise.
- A live, enforced separation of duties was removed knowingly, and nothing of equal strength replaces it. What is gained is a system a school can staff.
- Whether parent volunteers hold Recorder is a school decision: named sign-in is required ([ADR-0006](0006-shared-device-and-byo-session-policy.md)), so a volunteer needs their own account.

## Rejected alternatives

- **Three roles (Administrator, Recorder, Viewer)**: loses the boundary around imports and role assignment, where real student data lands.
- **Keep six roles and fix only the assignment gate**: the seam had already reappeared once.
- **A same-person rule at finalisation** (block a finaliser who recorded results, with an audited override): at real staffing levels it fires every carnival, so the override becomes a rubber stamp.
- **A self-recorded-results count in the finalisation audit**: precomputes what the stored data already answers, and is wrong unless it also counts corrections.
- **Per-carnival roles**: larger, and nothing needs it yet.
