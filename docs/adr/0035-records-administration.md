# ADR-0035: Records administration — a record is voided, never deleted

- **Status:** Accepted (2026-09)

## Context

Records are minted by carnival finalisation and by nothing else ([ADR-0028](0028-records-and-district-selection.md)), with no human gate on each record ([ADR-0017](0017-automatic-result-validation.md)). So a mistyped fast time becomes a school record at finalisation, and after finalisation the result is Final and refuses correction. There was no way to repair the book.

Two facts about the model shaped the fix. **Minting appends history**: a new record is a new `SchoolRecord` row chained to the one it beat (`PreviousRecordId`), which is closed as Superseded, so the row a fix would remove has a live predecessor behind it. And **records are school-scoped**, not carnival-scoped: the book spans every finalised carnival.

## Decision

1. **Void, never delete or edit.** A record row gains a void stamp (`VoidedAtUtc`, `VoidedBySubject`, `VoidReason`), the same shape as voided house-point adjustments. Its value, holder, result link and effective period stay as minted. Voiding is a second axis beside `Status`, not a new status: the row still says whether it stood or had been superseded, which is what an auditor wants to know.
2. **`POST /api/v1/records/{id}/void`** takes a required reason, needs `carnivals:administer` (reading the book is `carnivals:view`, but taking something out of a published book is custody), is one-way (`409 SCHOOL_RECORD_ALREADY_VOIDED`), and is audited as `school-record.void` with what stood and what was reinstated.
3. **A voided record is absent from every read of "the current record"**: the book and record detection at finalisation. It cannot be beaten (nobody breaks a record that was never real) and it does not block (a later, slower genuine mark can seed the category).
4. **Voiding the standing record reinstates its predecessor**: the first non-voided ancestor stands again. Leaving the category empty would let the next carnival seed it with a mark slower than the record that legitimately stood, putting a false record in the book by an act meant to remove one. If no live ancestor exists, the category is empty until a finalisation seeds it. Nothing is reinstated if another row already stands; the void runs in a transaction and re-checks just before saving, refusing with `CONCURRENCY_CONFLICT` rather than creating a second standing row.
5. **The administration screen is school-level** (`/school/records`, in the School area), because the book spans carnivals. It is online-only: there is no outbox behind record custody. Its read (`GET /api/v1/records/administration`, `carnivals:administer`) is a separate route from the book, not a flag on it, so `voidedBySubject` never reaches a Viewer. Voided rows stay listed with their reason, who and when; superseded rows are not listed.

Details of minting, voiding and reinstatement: [records](../architecture/records.md#administration-voiding).

## Consequences

- A typo'd record can be withdrawn by an administrator, with a reason, audited, and the record it wrongly superseded comes back.
- **Voiding fixes the record book only.** The Final result, the results book and any district list exported from it still show the bad mark; correcting a finalised result has no path. The two books can disagree by design: one shows the swim as recorded, the other what the school recognises.
- Every future read of "the current record" must also filter out voided rows, or voided records silently return.
- A reinstated record's effective period is reopened, so it is not a faithful log of what the book displayed between carnivals; the audit event holds both halves.
- A void made in error cannot be undone from any screen; the category may stay empty until the next carnival re-mints it.
- Under PostgreSQL's read-committed isolation the re-check narrows, but cannot abolish, the window for two concurrent voids.

## Rejected alternatives

- **Delete the row.** A record is published evidence; deleting it leaves the audit trail pointing at nothing and breaks the chain (the foreign key refuses it anyway).
- **Un-void.** Doubles the state space (what happens to a predecessor that reinstatement moved?) for a case nobody has had.
- **Edit the value in place.** The record would cite a result that does not support it. If the time was wrong, the record was wrong; withdraw it.
- **A `Voided` status value.** Destroys the fact the status column carries.
- **Leave the category empty on void.** Converts a removed false record into a seeded false record at the next carnival.
- **`?includeVoided=true` on the book.** One route with two gates; a Viewer would reach who voided what.
