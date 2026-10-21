# ADR-0028: Record-candidate detection and the district selection export

- **Status:** Accepted (2026-08). Partly superseded: [ADR-0035](0035-records-administration.md) adds voiding (§6's "no records administration" no longer holds); [ADR-0043](0043-dashboard-progress-and-meet-day-stats.md) adds the dashboard's "records today" strip; [ADR-0058](0058-one-age-per-division.md) removes merged age bands, so a record category is always a single age.

## Context

[ADR-0017](0017-automatic-result-validation.md) makes carnival finalisation the one human act, and says it exists to produce two artefacts: the record book and the district selection list. Neither existed. A `SchoolRecord` entity predated [ADR-0016](0016-role-model-simplification.md) and ADR-0017 and still assumed a records manager ratifying each candidate, a role and an act both of those ADRs removed.

The standing constraints:

1. Records start fresh: no legacy import; the first finalised carnival seeds the book.
2. An Open (`S`) division produces no school record ([ADR-0023](0023-open-category-eligibility-and-one-entry-per-event.md) §6).
3. Tier is read from the result's own division (Competitive only), never from the catalogue blob.
4. No new human act beyond finalisation, and no records manager.
5. [ADR-0015](0015-heats-races-and-lanes.md): the **event** is a ranking across all heats, used for records and district selection; the **heat** is the placing unit for points.

## Decision

### 1. Records are minted by finalisation, and by nothing else

Detection runs inside the finalisation transaction, after results move to Final. The finaliser is the actor; there is no endpoint, button or per-record approval.

For each record category the carnival contested — (event definition, age, category), on a Competitive division in M or F whose age and category are listed in the definition's `RecordCategoriesJson` — the best result on the event ladder **seeds** an empty category or **supersedes** the standing record when strictly better in the measure's own direction. Equalling does not supersede. Superseding writes a new row and closes the old one (`Superseded`, `EffectiveTo`); nothing is overwritten. A tie for a new record mints one row, chosen by the ladder's deterministic order (rank, holder name, entry id).

Age-group relays mint records, with the team entry's name as holder. The all-ages relay division (age `OPEN`) does not: a book indexed by age has no shelf for it.

### 2. Status and approval fields, reconciled with ADR-0017

A minted record is `Ratified` from birth: finalisation is the ratifying act, once per carnival. `Candidate` and `RejectedCandidate` stay in the enum unused rather than being repurposed. The approval fields keep a restated meaning: `ApprovedBySubject` is the finaliser, `ApprovedAtUtc` the finalisation time, `ApprovalNote` the finalisation reason. Each mint writes a `school-record.mint` audit event; the finalisation's audit event carries `recordsSet`.

### 3. One event ladder

`EventLadder` ranks each division across all heats, and is the single source for both record detection and the district list, so a swimmer cannot be on the district list and missing from the record book. It reuses the heat placing's ranking and higher-is-better rule, so field measures rank correctly. The record-categories blob is read **failing closed**: a missing record can be set next carnival, a false one sits in the book.

### 4. District selection export

`GET /carnivals/{id}/reports/district.csv`, on the audited, administrator-gated reports surface ([ADR-0004](0004-reporting-and-publishing.md)). It requires a **Finalised** carnival (`409 CARNIVAL_NOT_FINALISED`): the file leaves the school and feeds a selection process that cannot see later corrections.

- Rows: the top two of each Competitive division's event ladder, individual entries only. A tie at second gives three rows; the school breaks ties, not the platform. Open-category divisions are included, because an Open entrant cannot also be in Boys or Girls and would otherwise vanish from selection.
- Columns: Event code, Event, Division, **Selection**, Name, Year level, Result. `Selection`, not `Place`, because the heat's place pays points and the event's selection does not. No house, birth date, student id or category.
- Cells are written with the shared CSV writer, which neutralises formula injection.

### 5. The record book and the live badge

- `GET /api/v1/records` returns the standing book school-wide (optional `?sport=`), on the view permission: a record book is public within the school. It is cached by the service worker so the badge works at the venue. `category` is the division's, never a student's; `higherBetter` and the formatted value are computed by the server.
- A Competitive result strictly better than the standing record badges on the deck row and in the results book, compared on the device. The badge is inert while the book is empty, which is the whole first carnival.
- The Reports screen prints the standing book for the carnival's sport and, once finalised, the records set at this carnival (identified on the device by the record's date and carnival name).

### Not built

- A record-beating confirmation prompt at capture: it cannot fire while the book is empty.
- Legacy record import, a records CSV, a venue publishing page.

## Consequences

- Finalisation produces both artefacts it exists for, and does more work in the same transaction: if minting fails, nothing is finalised.
- Two rankings are now visible to users; the `Selection`/`Place` split keeps them apart.
- The single-standing-record rule is enforced in code, not by an index, so two simultaneous finalisations contesting one category could both mint. Every read takes the same row (best value, earliest `EffectiveFrom`, id), so the book still names one record.
- With no human gate, a mistyped fast time becomes a record at finalisation, after which the result is Final and cannot be corrected. Voiding ([ADR-0035](0035-records-administration.md)) is the repair.
- A carnival dated before the standing record but finalised after it clamps the old row's `EffectiveTo` so the period is never negative.

## Rejected alternatives

- **Per-record ratification** — ADR-0017 abolished it.
- **Rename `Ratified`** — a data migration for a word.
- **District list from heat places, or from the record book** — two rankings that could disagree.
- **Include relays in the district list** — school teams, not district material.

See [records](../architecture/records.md) and [reports: district team selection](../architecture/reports.md#district-team-selection).
