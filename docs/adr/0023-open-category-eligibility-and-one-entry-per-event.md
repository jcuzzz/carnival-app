# ADR-0023: Open-category students swim Boys, Girls or Open — and enter each event once

- **Status:** Accepted (2026-08). Amends [ADR-0003](0003-event-division-and-category-overrides.md)'s category-eligibility rule. Partly superseded by [ADR-0026](0026-the-student-category-is-server-side-only.md) §1: an Open (S) division admits every student, not only S students.

## Context

[ADR-0003](0003-event-division-and-category-overrides.md) left open whether S-category (Open) students get their own divisions or are folded into Boys and Girls. The school does both: it runs genuine Open divisions, and lists Open-category students in Boys and Girls as well. It also holds that a student has only one result per event.

Category eligibility was a strict equality test, so an S student matched only an S division. Entry uniqueness was per division only (`(CarnivalEventId, StudentId)`), so once an S student became eligible in several divisions of one event nothing stopped several entries and several results for one stroke. Entries also arrive through the offline outbox ([ADR-0010](0010-offline-entry-creation.md)), so any rule that guards only the online path guards nothing.

## Decision

### 1. S is eligible in Boys, Girls and Open

A student clears the category axis when the division has no category, **or** the division is S, **or** the effective category equals the division's, **or** the effective category is S. M and F students still fail each other's divisions (`CATEGORY_INELIGIBLE`). The rule lives once, in `EligibilityService`, so individual entries, the eligible-student search, relay legs and override re-validation all inherit it.

A category override to S therefore widens eligibility (Boys, Girls and Open) rather than moving it. That is the same rule applied uniformly; it remains an audited administrator act with a reason, and §3 caps the student at one entry regardless.

### 2. Dual listing mints no category override

Entering an S student into a Boys or Girls division writes no `CategoryOverride`; the entry's effective category stays S. The override is an exceptional, administrator-only act with a required human reason and an audit row; minting one automatically would fabricate an actor and a reason, force marshals' offline adds through an administrator, and put an "override" badge on a routine row until the badge means nothing.

`effective = override ?? snapshot` answers only *what the roster says about the student*. **Anything about a contest — placings, house points, reports, exports, school records — groups by the division's category, never the entry's effective category.**

### 3. One entry per student, per event definition, per tier

A student may hold at most one entry across the divisions of one event definition, at one tier, in one carnival. The rule is keyed on definition and tier rather than on the category axis because the age axis can also produce two eligible divisions (an `OPEN`-age relay division beside the age-band ones).

It does **not** cross the tier: a swimmer may contest both the Competitive and the Social version of a stroke, which is what the school runs and what the template generates.

The entry is refused, not the result: the conflict surfaces at sign-up, where a marshal can fix it, rather than on the deck.

### 4. Two places refuse

- The eligible-student list excludes students already entered in a sibling division (same carnival, definition and tier), so the common case never reaches a refusal.
- The server refuses regardless with **`ENTRY_IN_OTHER_DIVISION` (409)**, naming the existing entry's division. The cached roster is browsed offline and cannot see another device's queued entry, so the list is a convenience and the check is the rule. `ENTRY_EXISTS` is not stretched: it means "this division already holds this student", and shipped codes are never repurposed.
- The client classifies it as a **conflict**, not a retry (a retry would stop the drain and block every later capture on the device) and not a rejection (behind it is a real swimmer a marshal queued).

### 5. Relays

Team entries have no student, so §3 does not apply to them. §1 reaches relay legs unchanged: an S student may swim a leg for a Boys, Girls or Open house team. A swimmer may not be legged into two teams of the same definition, tier and age that differ only by category (`ENTRY_IN_OTHER_DIVISION`); swimming both an age-band relay and the `OPEN`-age relay ([ADR-0008](0008-team-and-relay-events.md)) is allowed.

### 6. Open divisions keep no school records

An Open-category division runs for participation and produces no school record; records are kept for Boys and Girls only. The template's `["M","F"]` record categories are therefore correct, and record detection skips S divisions by design.

## Consequences

- An S student is offered in every division they can contest, and a school runs Open divisions by adding S to its carnival's division profile ([ADR-0022](0022-event-and-division-management.md)); the template is unchanged.
- The sibling rule is application-level: it spans two tables, so it cannot be a unique index. Two devices draining at once can both pass the check and commit one entry each in sibling divisions; the outcome is two entries for a person to untangle, accepted for a one-minute window on a rare case.
- Override badges stay meaningful because only deliberate overrides set them.

## Rejected alternatives

- **Mint an override per dual-listed entry** — see §2.
- **Enforce one result per event at result time** — surfaces mid-carnival to a recorder who cannot fix it, after both races may have been swum, and refuses a coherent result for a reason unrelated to the result ([ADR-0017](0017-automatic-result-validation.md)).
- **Two entries with one marked non-scoring** — no per-entry scoring flag exists, it still gives two results for one stroke, and a non-scoring swimmer would still shift heat placings.
- **Delete the category check** — makes every division whole-school and removes the guard against mis-taps on a long roster.

See [data model: eligibility](../architecture/data-model.md#eligibility).
