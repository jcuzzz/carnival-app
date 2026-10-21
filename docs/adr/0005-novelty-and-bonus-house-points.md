# ADR-0005: Novelty and bonus house points

- **Status:** Accepted (2026-07)

## Context

Novelty events, "best dressed", "loudest house" and similar awards contribute to the day's house score, but must never become competition results, school records or entries in a student's history. The result lifecycle and the record book are records-grade; novelty points are discretionary, awarded to a house rather than a student, and matter only for the day's totals. Forcing them through `Result` would corrupt the lifecycle and create fake record candidates; leaving them outside the system would make the published house totals unreproducible.

## Decision

- **A separate entity, `HousePointsAdjustment`**, references a carnival and a house, never a student, entry or result. It cannot reach the record book or a student's history because no foreign key can.
- **Each adjustment is an individually recorded row**: house, points, category, a required reason, the capturing actor and role, and a timestamp, each mirrored to the audit trail.
- **Points may be negative.** A deduction is a valid adjustment with the same reason and audit.
- **Categories are per-carnival configuration**, not code, and carry no point caps; the administrator's discretion and the audit trail are the control. The default list (Novelty 1–3, Best Dressed House, Loudest House) is an example, freely editable.
- **Carnival administrators award and void**, enforced on the server (`carnivals:administer`); anyone who can view the carnival can list them. Recording results does not confer the right to award points.
- **No edit in place.** A wrong adjustment is voided with a reason and recorded again; a voided row stays listed and stops counting.
- **Finalisation freezes them**: no award or void once the carnival is finalised.
- **Totals keep the components visible**: a house's total is placement points + participation points + bonus, shown separately so the score is transparent and reproducible.
- **Capture goes through the device outbox** with an idempotency key and the captured actor, like a result.

How adjustments are scored: [scoring § bonus and novelty adjustments](../architecture/scoring.md#bonus-and-novelty-adjustments).

## Consequences

- Novelty scoring is first-class and auditable without contaminating the result pipeline; the separation is structural, not a convention reports must remember.
- House totals combine two sources, and every display must handle a negative bonus component.
