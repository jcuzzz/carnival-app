# ADR-0032: A promoted import keeps counts, not a roster

- **Status:** Accepted (2026-09). Partly superseded by [ADR-0062](0062-retention-windows-for-operational-data.md) (rejected and abandoned batches).

## Context

The platform's data minimisation is otherwise strong: no full date of birth, no addresses, no contact or health data. Three defaults contradicted it, and none depends on a retention schedule. They are one judgement applied three times: **personal data is kept where it is needed to do a job, and let go when the job is done.**

1. **Every import left a second roster behind.** `ImportBatch.StagedRowsJson` holds every staged row (student key, names, house, home group, year level, age group, category), including rejected rows and departed students, and `ValidationResultsJson` holds per-row issues naming each key. After promotion wrote `Student` and `RosterSnapshot` rows, the copy stayed for the life of the database, one whole roster per import.
2. **The district selection CSV carried a House column.** It is the one export designed to leave the school ([ADR-0028](0028-records-and-district-selection.md)). House is an internal scoring grouping; a district convener has no use for it.
3. **Eligible-student search was open to `Viewer`.** The endpoint searches every student who could be entered in a division, by name. A Viewer never creates an entry.

## Decision

1. **Promotion discards the staged rows and row issues.** Inside the promotion transaction both columns are set to null. The batch keeps its source file name and SHA-256, mapping version, importer, timestamps and reconciliation counts, and the `import.promote` audit event records the actor and summary. That answers what an auditor asks of an import: who imported what, when, under which mapping, with what effect. A batch under review keeps both, because review needs them and a failed promotion rolls back with them intact; rejected and abandoned batches are purged under [ADR-0062](0062-retention-windows-for-operational-data.md). On a promoted batch, `…/reconciliation` answers `409 IMPORT_ROWS_PURGED`, so a successful import never looks like a failed one, and `…/validation` returns the batch with `validation: null`. Details: [roster import](../architecture/imports.md).
2. **The district CSV has no House column**: `Event code, Event, Division, Selection, Name, Year level, Result`. Name, year level and event identify a swimmer unambiguously.
3. **Eligible-student search needs `carnivals:marshal`**, the policy of the roles that create entries. A Viewer keeps the carnival, events, entries, results, standings and records, but can no longer list children who are not in the carnival.

No retention period, scheduled job or de-identification sweep over students, snapshots or audit events is introduced here: those need a records-retention schedule and a custodian's decision. This removes a duplicate that never needed to exist.

## Consequences

- The database holds one current roster plus counts per import, not a full copy per import.
- A promoted batch cannot be re-read row by row. If an import went wrong in a way only the staged rows show, re-export from the student system and stage again; that system is the record of identity and enrolment.
- The app shows `IMPORT_ROWS_PURGED` as a notice, not an error.
- `Viewer` is a genuinely narrower role.
- Rejected and never-promoted batches keep their staged rows indefinitely; nothing purges them.

## Rejected alternatives

- **Redact the staged rows in place.** A half-empty document invites a reader to think the missing fields are a bug; a clear refusal is more honest than a hollow success.
- **Keep the row issues, drop only the rows.** A promoted batch has no structural errors, so what survives would be exactly the personal half.
- **A retention period on staged rows.** Needs a number nobody has set and a job that does not exist, for a problem promotion already solves.
- **Keep House "in case the convener wants it".** Speculative use is not a purpose.
- **A narrowed search for Viewers** (only entered students). That is the entries list, which they already have.
