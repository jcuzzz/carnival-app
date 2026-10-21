# ADR-0062: Retention windows for operational data

- **Status:** Accepted (2026-10)

## Context

Three stores hold student names that are operational, not records, and nothing aged them out:

- `IdempotencyKeys` store the 2xx response body against each key; for an entry or relay creation that body includes student display names.
- `ImportBatches.StagedRowsJson` and `ValidationResultsJson` hold a full roster copy. Promotion discards them ([ADR-0032](0032-staged-rosters-are-not-kept.md)); a rejected or abandoned batch kept them indefinitely.
- A `cancelled` outbox row on a device kept the student's name indefinitely, unlike `accepted` and `discarded` rows, which are pruned 7 days after resolution ([ADR-0013](0013-outbox-retention.md)).

None of these is a competition record, so the decision does not wait on the school's records retention schedule, which governs `Students`, `Results`, `SchoolRecords` and `AuditEvents`.

## Decision

1. **Idempotency keys are deleted 30 days after creation**, by a server-side background job. Thirty days is far beyond any device retry window (devices prune their own outbox after 7 days), so a late retry still replays its stored response instead of applying twice.
2. **Staged roster rows are discarded when a batch is rejected, and 7 days after upload for a batch that was never promoted.** Batch metadata (file name, hash, counts, status, importer, reviewer) stays as the import record. A batch whose rows are gone cannot be approved or promoted; the importer uploads the file again.
3. **A `cancelled` outbox row is pruned 7 days after cancellation**, the rule `discarded` rows already follow.

The server job deletes only these operational rows. It never touches the record classes above, and it writes no audit event per deleted row: the rows it removes are not audited entities. How the job runs: [roster import](../architecture/imports.md) and [offline outbox § retention](../architecture/offline-outbox.md#retention); the retention schedule is [retention and disposal](../governance/retention-and-disposal.md).

## Consequences

- No student name survives on the server outside the record classes for more than 30 days, or on a device in a cancelled row for more than 7.
- A batch purged by age is refused on approve or promote (`409 IMPORT_BATCH_EXPIRED`), and the review screen says to upload again.
- A device offline for more than 30 days that then retries a write could apply it twice. Accepted: the device's own outbox prunes the item long before then.
- The record classes remain without a disposal mechanism until a school's schedule exists.

## Rejected alternatives

- **A 7-day idempotency window**: matches the device outbox exactly, but leaves no margin for a device that resolves late.
- **Discard abandoned batches only on reject**: an upload nobody reviews would still keep a roster copy indefinitely.
