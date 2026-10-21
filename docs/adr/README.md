# Architecture decision records

ADRs record *why* a significant design decision was taken: its context, the decision, its consequences and the alternatives rejected. They are not a description of the current system: the [docs tree](../README.md) describes how it works today, and where an ADR and that page disagree, the page (and the code) wins. A decision that changes an earlier one gets a new ADR that supersedes it. New ADRs follow [`0000-template.md`](0000-template.md).

| ADR | Title | Status |
|---|---|---|
| [0001](0001-technology-stack.md) | Technology stack | Accepted; amended by [0047](0047-the-database-stays-on-the-host.md) (hosting, backup) and [0049](0049-images-are-built-in-ci-and-pulled-by-digest.md) (image build, deployment) |
| [0002](0002-offline-sync-design.md) | Offline sync and idempotency design | Accepted; finalisation gate replaced by [0017](0017-automatic-result-validation.md) and [0034](0034-organiser-queue-visibility.md) |
| [0003](0003-event-division-and-category-overrides.md) | Event divisions and competition category overrides | Accepted; amended by [0023](0023-open-category-eligibility-and-one-entry-per-event.md); partly superseded by [0026](0026-the-student-category-is-server-side-only.md) |
| [0004](0004-reporting-and-publishing.md) | Reporting and publishing direction | Accepted (tier 1 built; tiers 2 and 3 are directions) |
| [0005](0005-novelty-and-bonus-house-points.md) | Novelty and bonus house points | Accepted |
| [0006](0006-shared-device-and-byo-session-policy.md) | Shared-device and BYO session policy | Accepted; partly superseded by [0031](0031-shared-device-hygiene.md) and [0034](0034-organiser-queue-visibility.md) |
| [0007](0007-configuration-and-auth-modes.md) | Configuration/administration model and authentication modes | Accepted; partly superseded by [0061](0061-demo-showcase-per-visitor-instances.md) |
| [0008](0008-team-and-relay-events.md) | Team and relay events | Accepted |
| [0009](0009-administrators-can-record-results.md) | Administrators can record results (staffing flexibility) | Superseded by [0016](0016-role-model-simplification.md) |
| [0010](0010-offline-entry-creation.md) | Offline entry creation (client-supplied entry ids) | Accepted |
| [0011](0011-api-error-contract.md) | API error contract (machine-code error bodies) | Accepted |
| [0012](0012-client-data-layer.md) | Client data layer — TanStack Query for reads, generated API types | Accepted; outbox-for-every-write rule narrowed by [0022](0022-event-and-division-management.md) |
| [0013](0013-outbox-retention.md) | Outbox retention and index-scoped live queries | Accepted |
| [0014](0014-result-entry-navigation-and-schedule-model.md) | Result-entry navigation and the carnival schedule model | Accepted; partly superseded by [0018](0018-race-first-capture.md) and [0058](0058-one-age-per-division.md) |
| [0015](0015-heats-races-and-lanes.md) | Heats, races and lanes — the physical unit of capture | Accepted; partly superseded by [0018](0018-race-first-capture.md), [0019](0019-retire-cross-division-combined-races.md), [0021](0021-remove-the-lane-filter.md) and [0039](0039-the-heat-is-the-context.md) |
| [0016](0016-role-model-simplification.md) | Four roles, not six | Accepted; supersedes [0009](0009-administrators-can-record-results.md) |
| [0017](0017-automatic-result-validation.md) | Results are validated automatically, not verified by a person | Accepted |
| [0018](0018-race-first-capture.md) | Race-first capture — the division selector, and combined races | Accepted; partly superseded by [0019](0019-retire-cross-division-combined-races.md), [0020](0020-flatten-lane-handling.md) and [0039](0039-the-heat-is-the-context.md) |
| [0019](0019-retire-cross-division-combined-races.md) | Retire cross-division combined races; keep single-division race materialisation | Accepted |
| [0020](0020-flatten-lane-handling.md) | Flatten lane handling — inline lane editing, a multi-lane filter, no assign mode | Accepted; §1 superseded by [0021](0021-remove-the-lane-filter.md); amended by [0030](0030-lane-swap.md); §3 replaced by [0037](0037-add-race.md), [0039](0039-the-heat-is-the-context.md) and [0042](0042-condensed-deck-header.md) |
| [0021](0021-remove-the-lane-filter.md) | Remove the lane filter | Accepted |
| [0022](0022-event-and-division-management.md) | Event management — the catalogue gets a screen, and the division profile becomes data | Accepted; partly superseded by [0026](0026-the-student-category-is-server-side-only.md), [0027](0027-the-division-profile-gets-its-own-table.md), [0058](0058-one-age-per-division.md) and [0059](0059-adding-a-catalogue-event-is-one-press.md) |
| [0023](0023-open-category-eligibility-and-one-entry-per-event.md) | Open-category students swim Boys, Girls or Open — and enter each event once | Accepted; §1 partly superseded by [0026](0026-the-student-category-is-server-side-only.md) |
| [0024](0024-carnival-context-and-primary-navigation.md) | A selected carnival, and three tabs instead of a button row | Accepted; partly superseded by [0055](0055-the-school-area.md) |
| [0025](0025-outbox-recovery.md) | Outbox recovery — what a person may do to a parked capture | Accepted |
| [0026](0026-the-student-category-is-server-side-only.md) | An Open division is open to everyone, and a student's category never leaves the server | Accepted |
| [0027](0027-the-division-profile-gets-its-own-table.md) | The division profile gets its own table | Accepted; age axis amended by [0058](0058-one-age-per-division.md) |
| [0028](0028-records-and-district-selection.md) | Record-candidate detection and the district selection export | Accepted; partly superseded by [0035](0035-records-administration.md), [0043](0043-dashboard-progress-and-meet-day-stats.md) and [0058](0058-one-age-per-division.md) |
| [0029](0029-a-stuck-outbox-is-loud.md) | A stuck outbox is loud on the device | Accepted |
| [0030](0030-lane-swap.md) | Swapping two swimmers' lanes | Accepted |
| [0031](0031-shared-device-hygiene.md) | Shared-device hygiene | Accepted |
| [0032](0032-staged-rosters-are-not-kept.md) | A promoted import keeps counts, not a roster | Accepted; partly superseded by [0062](0062-retention-windows-for-operational-data.md) |
| [0033](0033-the-real-shape-fixture-is-generated.md) | The real-shape roster fixture is generated, never sanitised | Accepted |
| [0034](0034-organiser-queue-visibility.md) | Organiser queue visibility | Accepted |
| [0035](0035-records-administration.md) | Records administration — a record is voided, never deleted | Accepted |
| [0036](0036-the-deck-shows-placings.md) | The deck shows placings | Accepted |
| [0037](0037-add-race.md) | Add race | Partly superseded by [0039](0039-the-heat-is-the-context.md) and [0042](0042-condensed-deck-header.md) |
| [0038](0038-auto-fill-lanes.md) | Auto-fill lanes, and one lane control | Accepted; partly superseded by [0039](0039-the-heat-is-the-context.md) |
| [0039](0039-the-heat-is-the-context.md) | The heat is the context | Accepted; partly superseded by [0042](0042-condensed-deck-header.md) and [0053](0053-field-attempts.md) |
| [0040](0040-design-system.md) | The design system — tokens, school branding as configuration, one plain type/icon system | Accepted; partly superseded by [0044](0044-configurable-house-colours.md) and [0045](0045-school-settings-in-app.md) |
| [0041](0041-deck-direction-a.md) | The deck in direction A — seconds-first time entry, one row-actions menu, the lane block | Accepted |
| [0042](0042-condensed-deck-header.md) | The condensed deck header — three pickers, entries above the fold | Accepted |
| [0043](0043-dashboard-progress-and-meet-day-stats.md) | The Dashboard — progress, standings and meet-day stats | Accepted |
| [0044](0044-configurable-house-colours.md) | Configurable house colours | Accepted; reverses part of [0040](0040-design-system.md) |
| [0045](0045-school-settings-in-app.md) | School settings administered in-app (admin GUI, slice 1) | Accepted |
| [0046](0046-in-app-colour-picker.md) | In-app colour picker (react-colorful), native input retired | Accepted |
| [0047](0047-the-database-stays-on-the-host.md) | The database stays on the application host, hardened in place | Accepted |
| [0048](0048-in-app-role-assignment.md) | In-app role assignment, layered over Entra groups | Accepted |
| [0049](0049-images-are-built-in-ci-and-pulled-by-digest.md) | Images are built in CI and pulled by digest | Accepted |
| [0050](0050-reconcile-template.md) | Reconciling a changed event template onto an existing catalogue | Accepted; amended by [0054](0054-setting-up-a-carnival.md) |
| [0051](0051-outbox-ack-narrowing.md) | The stored outbox ack is narrowed per kind | Accepted |
| [0052](0052-athletics-measures-field-capture-and-record-eligibility.md) | Athletics — field measures, mark capture and record eligibility | Accepted; partly superseded by [0053](0053-field-attempts.md), [0056](0056-high-jump-heights-ladder.md) and [0058](0058-one-age-per-division.md) |
| [0053](0053-field-attempts.md) | Field attempts — a configurable series per athlete, the best mark derived | Accepted; high jump superseded by [0056](0056-high-jump-heights-ladder.md) |
| [0054](0054-setting-up-a-carnival.md) | Setting up a carnival — a guided path from "New carnival" to the first result | Accepted; §3 partly superseded by [0055](0055-the-school-area.md), [0057](0057-age-bands-from-the-roster.md) and [0059](0059-adding-a-catalogue-event-is-one-press.md) |
| [0055](0055-the-school-area.md) | The School area — one place for everything school-wide | Accepted |
| [0056](0056-high-jump-heights-ladder.md) | High jump — the heights ladder, captured as the card grid | Accepted |
| [0057](0057-age-bands-from-the-roster.md) | Age bands come from the roster and are picked, not typed | Accepted; §3 superseded by [0058](0058-one-age-per-division.md) |
| [0058](0058-one-age-per-division.md) | One age per division — the merged age band is removed | Accepted |
| [0059](0059-adding-a-catalogue-event-is-one-press.md) | Adding a catalogue event to a carnival is one press | Accepted |
| [0060](0060-the-competition-year-is-chosen-at-import.md) | The competition year is chosen at roster import | Accepted |
| [0061](0061-demo-showcase-per-visitor-instances.md) | Demo showcase with a database per visitor | Accepted (API and app built; template build and demo deployment not in this repository) |
| [0062](0062-retention-windows-for-operational-data.md) | Retention windows for operational data | Accepted |
| [0063](0063-standings-list-every-house.md) | Standings list every house | Accepted |
