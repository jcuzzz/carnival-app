**School Sports Event****and Records Platform**

**Project Plan Proposal**

Draft for review  |  Version 0.2  |  27 July 2026

| **Prepared for** | [School leadership / ICT / sports coordination review group] |

|---|---|

| **Prepared by** | [Name / role] |

| **Proposal status** | NOT READY — discovery, policy review and approval required |

| **Proposed decision** | Proceed to a controlled discovery and synthetic-data prototype |



| **Proposal intent**Seek approval to investigate and prototype a secure, school-hostable platform for managing swimming, athletics and related event results. This document is not approval to process real student data or deploy a production service. |

|---|



# Document control

| **Field** | **Detail** |

|---|---|

| Document owner | [To be assigned] |

| Business owner | [Principal nominee / sports program owner] |

| Technical owner | [School ICT / nominated service owner] |

| Current version | 0.2 — governance-gate corrections and ratified Phase 0 decisions (see Revision history) |

| Approval sought | Approval to complete discovery, governance assessment and synthetic-data prototype |

| Excluded approval | No approval is sought at this stage for production use or real student data |

| Next review | [Insert review meeting / date] |



## Revision history

| **Version** | **Date** | **Summary of changes** |

|---|---|---|

| 0.1 | 24 July 2026 | Initial proposal for review. |

| 0.2 | 27 July 2026 | Governance corrections from the verified policy pathway: Privacy Impact Assessment restated as a hard gate (§12); security-assessment pathway corrected — ST4S/Arc has no school-initiated route for an in-house build, so direct engagement with the department's IT Security team is the realistic pathway (§12); termly access-review and quarterly ICT risk-register cadences added (§15); PROV PROS 2206 added as reference [6] (Appendix C). Ratified Phase 0 decisions incorporated: identity is Microsoft Entra ID on the school-managed tenant (§3.2, §8); initial hosting is a Proxmox test environment (§7.2); the pilot is the swimming carnival, Term 1 2027 (§13); school records start fresh with no legacy import (§9.1); reporting direction per ADR-0004 (§3.2). |



# Contents

1. Executive summary

2. Context and problem statement

3. Proposed direction

4. Objectives and success measures

5. Scope

6. Operating and data-authority model

7. Proposed architecture

8. Identity and access

9. Data and records integrity

10. Event-day operation and offline resilience

11. Student import and reconciliation

12. Security, privacy and policy gates

13. Delivery plan

14. Validation and acceptance

15. Operations and support

16. Risks and mitigations

17. Governance and roles

18. Decisions requested

Appendix A. Initial data entities

Appendix B. MVP user journeys

Appendix C. Reference material

# 1. Executive summary

The school currently holds or processes student data across a departmental platform, exported spreadsheets and reconciliation workflows. Sports carnival results are a strong candidate for consolidation because event-day entry, historical records, reconciliation and reporting all depend on consistent student identity and durable result history.

This proposal recommends a standalone, responsive web application with an installable Progressive Web App (PWA), a backend API and a relational database. Staff would authenticate through the school’s approved identity provider. The platform would import a controlled student roster from the departmental source, manage carnival events, capture and verify results, retain revision history and derive ratified school records.

The preferred product model is one maintainable codebase with a separate, isolated deployment for each school. A school could host its instance locally or in an approved cloud environment. Each deployment would have its own database, identity configuration, encryption material, backups and operational ownership.

| **Recommended approval**Approve Phase 0 discovery and Phase 1 synthetic-data foundation work only. Production approval should remain gated on policy, privacy, security, records-management, runtime, backup and field-pilot validation. |

|---|



# 2. Context and problem statement

Current workflows involve exporting student information from the departmental platform into Google Sheets to support reconciliation and reporting. This creates several practical constraints:

Student identity, current enrolment data and historical sports results can become spread across multiple locations.

Spreadsheet workflows are difficult to validate consistently and may not retain a reliable revision or audit history.

Event-day entry is slower when staff must manually type student details or work across separate sheets.

Historical school records can be difficult to compare, verify and reproduce when the underlying data model changes.

A move to SharePoint may improve document management but does not by itself provide a purpose-built transactional results system.

The opportunity is to create a dedicated operational system that remains subordinate to the departmental student system of record while providing the workflows, controls and reporting needed for sports events.

# 3. Proposed direction

## 3.1 Product model

Develop a configurable single-tenant platform. Every school deployment runs the same supported application version but stores its data independently. School-specific rules, branding, houses, event definitions, identity settings and import mappings are configuration or authored data—not code forks.

## 3.2 Core technology direction

| **Layer** | **Proposed approach** | **Reason** |

|---|---|---|

| Client | Responsive web application / installable PWA | Works across laptops, tablets and phones; supports event-day caching and queued submissions. |

| Backend | Versioned API and application services | Centralises validation, authorisation, conflict handling and records logic. |

| Database | PostgreSQL or SQL Server | Provides relational integrity, transactions, reporting and controlled schema migrations. |

| Identity | Microsoft Entra ID (school-managed tenant) via OpenID Connect — ratified | Supports organisational SSO without the application handling directory passwords; group-to-role mapping is configured against the school tenant, not the central departmental tenant. |

| Deployment | Containerised, isolated instance per school | Supports local or approved cloud hosting with repeatable installation and upgrades. |

| Reporting | In-app reports plus PDF/CSV exports; read-only venue publishing page at pilot; SharePoint/Power BI integration deferred (ADR-0004) — ratified | Keeps the transactional source reliable; export files remain the universal adapter for SharePoint/Power BI consumption until integration demand is evidenced. |



## 3.3 Explicit non-goals for the first release

Replacing the department-provided student administration system.

Creating a shared cross-school database or shared student-data tenancy.

Providing parent or public access.

Automating every sport, competition format or interschool process.

Importing unnecessary sensitive, health, family or contact information.

Using direct LDAP password authentication in the application.

# 4. Objectives and success measures

| **Objective** | **Initial success measure** |

|---|---|

| Faster event entry | Staff can find an eligible student and submit a valid result with minimal typing. |

| Reliable student matching | Every imported student is linked by a stable external identifier, with duplicate and missing-ID exceptions surfaced. |

| Durable records | Historical results remain reproducible after student details, event rules or school configuration change. |

| Controlled corrections | Result changes retain who, when, previous value, reason and approval state. |

| Offline resilience | Event entry can continue during temporary network loss and clearly shows pending, synced and failed submissions. |

| Secure access | Permissions are role-based and enforced by the backend, not only hidden in the interface. |

| Portable deployment | A synthetic second-school instance can be deployed without changing application code. |

| Recoverability | A documented backup can be restored into a clean environment and validated. |



# 5. Scope

## 5.1 Minimum viable product

Import a student roster from a defined CSV or spreadsheet export into a staging area.

Validate identifiers, duplicates, required fields, year level, house and competition group.

Create a carnival and configure events, divisions and participation rules.

Search or filter eligible students and create event entries.

Enter times, distances, heights, places and non-result statuses such as DNS, DNF or disqualified.

Queue event-day submissions when temporarily offline and synchronise safely on reconnection.

Verify and finalise results with revision history.

Calculate house points using configurable rules.

Detect potential school records and require explicit ratification.

Produce event, student, house and record reports and controlled exports.

Maintain role assignments, audit events, version information and health status.

## 5.2 Later modules

Cross-country and additional school competitions.

Lane, heat and marshalling workflows.

Certificates, ribbons, labels and presentation exports.

Direct integrations with approved student systems or document platforms.

Public display boards containing non-sensitive event information.

Managed deployment monitoring and upgrade orchestration.

# 6. Operating and data-authority model

The platform must define which system owns each type of information. A convenient interface must not silently become the authority for student identity or official records.

| **Data domain** | **Authority** | **Platform responsibility** |

|---|---|---|

| Current student identity and enrolment | Department-provided student system | Import a controlled snapshot; reconcile but do not independently redefine source identity. |

| Carnival configuration | Sports program owner / authorised event administrator | Store versioned event definitions, scoring rules and eligibility configuration. |

| Event entries and results | Sports results platform | Validate, store, audit, verify and report competition results. |

| School records | Ratified results under school policy | Identify candidates; require authorised ratification before replacing an official record. |

| Published reports and documents | Approved school publishing location | Generate or export controlled outputs; preserve the underlying transactional record. |



| **Authority rule**The client interface requests changes. The backend authorises, validates and commits them. Imported files and user interfaces must never bypass the application’s authority and audit rules. |

|---|



# 7. Proposed architecture

## 7.1 Per-school isolation

Each school receives an isolated deployment boundary:

| **Staff devices**Browser / installed PWACached roster + pending queue | **School application**Reverse proxy / webAPI + workerSchool configuration | **School data**Separate databaseSeparate backupsSeparate keys and logs |

|---|---|---|



## 7.2 Supported hosting modes

| **Mode** | **Characteristics** | **Conditions** |

|---|---|---|

| School-hosted local server | Application and database run on approved school infrastructure. | School owns patching, backup, certificates, monitoring and disaster recovery. |

| School-owned cloud | Application runs in the school or department-approved cloud tenancy. | Cloud service, region, networking and identity must be approved. |

| Managed hosting — future | A service operator hosts a dedicated instance for each school. | Requires formal contracts, privacy, security, availability, incident and support obligations. |



| **Initial hosting decision (ratified)**The synthetic prototype and pilot preparation run in a school Proxmox test environment (Linux virtual machine) — the school-hosted local mode above. Production hosting remains subject to the hosting-approval gate in §12. |

|---|



## 7.3 Configuration boundary

School name, branding and timezone.

Houses, age groups, divisions and eligibility rules.

Event catalogue, scoring rules and school-record rules.

Identity provider and group-to-role mappings.

Student import mapping and required fields.

Retention, export and operational settings approved for that school.

# 8. Identity and access

The application integrates with Microsoft Entra ID on the school-managed tenant using OpenID Connect (ratified). It should not collect or store normal staff directory passwords.

The platform defines four roles (ratified 11 August 2026). An earlier version of this document defined six.

| **Role** | **Indicative permissions** |

|---|---|

| Carnival administrator | Create carnivals, configure events, allocate officials, open or close entry, adjust scoring settings, record and amend results, apply the audited eligibility-category override, and finalise the carnival — the one remaining human act on the result path (§9.2). |

| Recorder | Marshal at the venue: create entries and team entries on the day, remove an entry that has no result, assign race and lane, capture results, and amend any result in the carnival that is not yet final. Cannot finalise a carnival and cannot apply the eligibility-category override. |

| Viewer | View authorised results and reports. |

| System administrator | Manage configuration, student imports and roster snapshots, role mappings, retention and operational settings. Also holds every carnival permission, so that a school whose only technically confident staff member sits in this role is not locked out of running its own carnival. No unrestricted business override by default. |



Event administrator, verifier and records manager collapse into **carnival administrator**; results official becomes **recorder**; read-only viewer becomes **viewer**; system administrator keeps its former scope and gains the carnival permissions described above. The reduction follows from staffing rather than from design preference: most schools do not have a full team of technically confident staff, and one person routinely covers several jobs on carnival day. Group-to-role mapping remains configuration in the school tenant (§7.3), so the school may keep its existing identity groups and map several of them onto one role.

The per-event assignment gate is retired with the same change. A results official could previously submit only for the events they were assigned to; a recorder may now capture for any event in a carnival they can reach. Officials assignment survives as **advisory data** — it records who is expected at an event and drives each user’s own list of events — but it no longer decides whether a submission is accepted. The gate was a coordination aid rather than an access control in substance: submission has always required a named, authenticated user, and every submission is audited.

| **A separation of duties has been removed, knowingly**Until this change the lifecycle in §9.2 required two people and the requirement was enforced, not merely described: result verification and event finalisation were held by the verifier role alone, an event administrator was refused both by an automated test that pinned the refusal, and a carnival could not be finalised until every event had passed a verifier. That control no longer exists. Verification is now automatic (§9.2), which left the verifier role holding nothing distinct from an event administrator, and the roles it separated are collapsed. The decision is recorded in ADR-0017 and ADR-0016 and is stated here rather than left for a reviewer to discover. |

|---|



Two things are worth stating alongside it. The control held only while no individual belonged to two role groups, and role assignment is group membership — configuration, not code — so a school with one capable staff member ended the separation on day one by placing that person in both groups, with no code change, no audit entry and nothing on screen to say so. And the check it protected stood on roughly 150 per-event finalisations in a single day; a check at that volume is signed off in a batch, and a rubber-stamped check is worse than an automated one because it carries the appearance of assurance.

What now stands in its place is evidence rather than prevention, and the school should read it that way:

Every capture and every correction is audited with actor identity, role, timestamp, previous and new values, reason and the applicable rule version (§9.3). Where a result is captured offline and transmitted later, both the capturing identity and the transmitting session are recorded.

Every amendment appends to an immutable revision history on the result, so a value can be repaired and the repair is itself permanent evidence rather than an overwrite. The question a reviewer would ask of the removed control — did one person take this result from capture to the record book — is answerable from data the platform already stores.

The student-data boundary that justifies a separate system administrator role has been **tightened**, not relaxed. Read access to student imports, roster snapshots and reconciliation was previously held by the event administrator as well; it is now held by the system administrator alone. A carnival administrator has no access to the import surface at all. The four-role model therefore draws a stricter line around real student data than the six-role model it replaces, which is the boundary a privacy assessment is most likely to probe.

Set against this, the recorder role is materially more capable than the results official it replaces: it can enter a student into an event and place them in a lane, not merely record a value against an entry someone else created, and under the carnival-day model a lane and heat can affect house points. That widening is deliberate — it is what allows marshalling at the venue without a second person present — and audit is its control.

Access must be enforced by the backend for every protected operation. Administrative roles should be tightly limited, logged and reviewed. Shared event devices require an agreed sign-in, lock-screen and session-expiry procedure.

# 9. Data and records integrity

## 9.1 Student and historical snapshots

Current student details may change. Historical competition results must therefore preserve the relevant event-time context, such as display name, year level, house and competition division, alongside the stable student identifier.

The school starts fresh on records (ratified): no legacy record import module is planned. The platform’s first ratified school records are established from its own results, or manually entered and ratified by the records manager if the school chooses.

## 9.2 Result lifecycle

Results are validated automatically at the point of capture and are no longer verified by a person (ratified 11 August 2026). Every submission is checked by the backend against a named, versioned set of rules — the current set is identified as coherence-v1 — and the version that passed the result is recorded on the audit entry for that capture and for every later correction, so the school can always establish which rules a given result was judged against.

The checks are of two kinds, with different consequences. A result that is incoherent under any reading is refused outright, with a machine-readable code returned to the person capturing it: a measured time recorded against a did-not-start, did-not-finish or disqualification; a missing time on a completed swim; a time of zero or less; an entry that does not belong to the carnival. A result that is merely unusual is accepted and flagged, never refused — an implausibly fast time is as likely to be the best swimmer in the school as a typing error, and software that rejects it is a data-loss mechanism aimed at exactly the results that matter most. The person able to settle such a case is standing at the lane, not reviewing a queue afterwards.

| **State** | **Meaning** | **Allowed transition** |

|---|---|---|

| Draft / queued | Captured on the device or incomplete; not yet accepted by the server. | Submit or discard. |

| Provisional | Historical state only. Results accepted before this change remain readable, correctable and included in scoring; new results no longer enter it. | Correct with reason, or move to final with the carnival. |

| Verified | Accepted by the backend after passing the automatic validation rules, and stamped with the rule version that passed it. Visible immediately and counted in provisional standings. | Correct with reason, or move to final with the carnival. |

| Final | Reached only by finalisation of the carnival. Included in the official results, the points calculation and the school record book. | Terminal. Amendment is refused, and a finalised carnival cannot be reopened. |

| School record | A result that betters the standing record is detected automatically and displayed as such. There is no separate ratification act. | Superseded when a better result is finalised. |



**The state is still named verified, and the name no longer implies a person.** Renaming it to validated was considered when this change was made and was deliberately rejected: it is the more accurate word, but it buys one word in a governance document at the price of a data migration and a breaking change across the application. The name is retained as a lifecycle label only. What it now means is that the result passed the named rule set at the moment of capture, and that the rule version which passed it is on the audit record.

**Per-event finalisation is retired.** An event is complete when every entry in it carries a result or a non-result status. Completion is derived from the data rather than stored, it gates nothing, and it is reversible: entries are created at the marshalling point throughout the day, so an event that is briefly complete between heats becomes incomplete again when the next entry is added. That is the intended behaviour. Opening and closing an event remain administrator controls, and a carnival that has been closed can be reopened — closing it early is a recoverable mistake, not a dead end.

**One human act survives on the result path — finalisation of the carnival — and its purpose is publication, not synchronisation.** Standings are live from the first swim and labelled provisional all day. Finalisation is the point at which the school states that the results are the results, and it exists because two artefacts are derived from them: the school record book and the district selection list. The act moves every accepted result in the carnival to final in a single transaction, is audited with actor and role like any other change, and is refused while any entry still owes a result, identifying the entries that block it. It is not reversible: a carnival cannot be reopened once finalised, which is what makes final a durable statement rather than a working state.

The honest consequence is that no second pair of eyes now falls on an individual result before it counts. A mistyped time can therefore reach the record book. What stands against that is correction with a permanent revision history, so the record is repairable and every repair is visible, and the audit requirements in §9.3, which record actor, role and previous and new values for every capture and every correction. At roughly 150 events run back to back, the realistic alternative was not a human check on every result but a human check on none.



## 9.3 Audit requirements

Actor identity and role.

Timestamp and school timezone.

Action and affected entity.

Previous and new values for material changes.

Reason for corrections or administrative overrides.

Import batch, application version and relevant rule version where applicable.

# 10. Event-day operation and offline resilience

Connectivity at swimming and athletics venues cannot be assumed. The event workflow should be designed around temporary network failure rather than treating it as an exceptional afterthought.

| **Requirement** | **Expected behaviour** |

|---|---|

| Pre-event preparation | Download the authorised carnival configuration, assigned events and minimum student roster required for the device. |

| Offline entry | Create locally identifiable submissions without losing work when the network is unavailable. |

| Visible status | Show pending, synchronising, accepted, rejected and conflict states to the user. |

| Idempotency | A retry of the same submission must not create a duplicate result. |

| Conflict handling | Concurrent changes must be surfaced for resolution rather than silently overwriting data. |

| Device recovery | Define what happens if a device is lost, locks, clears storage or is handed to another staff member. |

| Finalisation control | Events cannot be finalised until outstanding queues and unresolved conflicts are accounted for. |



# 11. Student import and reconciliation

The initial integration should be a controlled file import rather than a direct live connection. This reduces dependency on undocumented interfaces and allows the school to validate the exact fields being copied.

| **Stage** | **Control** |

|---|---|

| Raw intake | Store import metadata and a secure copy or hash of the authorised source file according to policy. |

| Mapping | Transform school-specific column names into a canonical student snapshot. |

| Validation | Check required IDs, duplicate IDs, missing fields, invalid year levels, houses and competition groups. |

| Reconciliation | Show new, changed, inactive, unmatched and rejected records before promotion. |

| Approval | An authorised user approves the validated batch. |

| Promotion | Create a new current roster snapshot without rewriting historical result snapshots. |



## 11.1 Data minimisation

Only fields necessary for event eligibility, identification and reporting should be imported. The first design should avoid addresses, parent information, health records and full birth dates unless a documented business requirement and policy approval exists. Where practical, import a derived competition group rather than the source attribute used to calculate it.

# 12. Security, privacy and policy gates

This project processes identifiable student information and creates school records. It must therefore be treated as a governed school administration system, not only as a software development exercise.

| **Gate** | **Required evidence before production** |

|---|---|

| Business ownership | Named principal delegate, business owner, technical owner and records custodian responsibilities. |

| System-of-record review | Document how the departmental platform remains authoritative and how any required information is exported back or retained in approved systems. |

| Privacy assessment | Completed Privacy Impact Assessment. This is a hard gate, not a discretionary step: the platform is non-department software processing identifiable student data, which department guidance places squarely within PIA scope. Evidence required: completed PIA template (all three parts), Privacy-team review comments actioned, and principal plus Privacy-team endorsement recorded before any real student data is processed. |

| Security assessment | Departmental security assessment completed through direct engagement with the department’s IT Security team (via Service Desk), with the outcome and required actions tracked. ST4S/Arc has no school-initiated submission pathway for an in-house build — the scheme is built around vendor product submissions — so the policy’s “alternative assessment” route via IT Security is the realistic pathway, not a fallback. Written confirmation of the applicable pathway is itself a Phase 0 deliverable. |

| Access control | Approved role model, least-privilege mapping, administrator controls, MFA/session approach and access-review procedure. |

| Records management | Approved retention, export, migration, disposal and legal-access process for results and records. |

| Hosting approval | Approved infrastructure, data location, network exposure, certificates, patching, backup and incident responsibilities. |

| Recovery validation | Successful backup restore and documented disaster-recovery procedure. |

| Operational acceptance | Synthetic test, controlled pilot and production readiness sign-off. |



Current Victorian Department policy states that department-provided systems of record must continue to be used for their defined purposes. Additional administration systems handling identifiable or important operational data are subject to software assessment requirements; the department’s published assessment pathway is written around vendor products in the Arc Software Catalogue, so for a school-built application the pathway must be agreed directly with the department’s IT Security team. Department guidance treats a Privacy Impact Assessment as the mechanism for assessing new software that handles personal or sensitive information, and requires schools to manage access, security risk, records retention and recoverability. See Appendix C.

# 13. Delivery plan

| **Phase** | **Purpose** | **Key outputs** | **Exit status** |

|---|---|---|---|

| 0. Discovery and governance | Confirm owner, workflows, data, policy path and pilot conditions. | Current-state map; data inventory; identity/hosting assessment; requirements; risk register; approval path. | READY FOR SYNTHETIC PROTOTYPE |

| 1. Technical foundation | Establish safe architecture using synthetic data. | Repo governance; deployment baseline; identity test; database migrations; audit framework; import staging; test automation. | VALIDATION PENDING |

| 2. MVP workflow | Implement one end-to-end carnival slice. | Roster import; carnival/events; result entry; verification; points; record candidates; reports; offline queue. | FIELD PILOT CANDIDATE |

| 3. Controlled pilot | Exercise the workflow with nominated staff and synthetic or approved limited data. | Training; field simulation; defect log; connectivity test; restore test; operational procedures. | PRODUCTION REVIEW |

| 4. Production release | Deploy for an approved event. | Approved hosting; production configuration; access groups; monitoring; backup; support plan; sign-off. | COMMIT READY / RELEASE READY |

| 5. Productisation | Prove repeatable deployment for another school. | Configurable branding/rules; installer; second-school synthetic deployment; upgrade/rollback package; support boundary. | REPEATABLE DEPLOYMENT |



| **Pilot target (ratified)**The first field pilot is the swimming carnival, Term 1 2027 (February–March). Phases 1–3 are scheduled against this date; build order favours times-based events, lanes and heats. |

|---|



## 13.1 Phase 0 discovery tasks

Identify the exact departmental student platform and approved export mechanism.

Collect representative, de-identified export files and current reconciliation reports.

Document swimming and athletics workflows from setup through final records publication.

Confirm event rules, scoring, ties, disqualifications, corrections and record ratification.

List devices, venues, expected network conditions and shared-device practices.

Confirm the approved identity provider and whether staff group information is available.

Determine the policy assessment, procurement and hosting approval pathway.

Nominate a pilot carnival or simulation and the staff who will review it — resolved: the swimming carnival, Term 1 2027; reviewing staff to be confirmed.

# 14. Validation and acceptance

## 14.1 Automated and static validation

Database migration tests from supported prior versions.

Import validation and reconciliation test fixtures.

Role and authorisation tests for every mutation endpoint.

Result, points and record-rule tests, including ties and invalid states.

Offline queue idempotency, retry and conflict tests.

Export and report reproducibility tests.

Dependency, secret and container-image scanning appropriate to the environment.

## 14.2 Runtime smoke tests

Create a carnival and assign staff roles.

Import a synthetic roster containing duplicates, changes and inactive students.

Enter results concurrently from several devices.

Disconnect and reconnect devices with queued results.

Attempt duplicate submissions and conflicting corrections.

Finalise events and verify house points and record candidates.

Restore a backup into a clean deployment and reproduce final reports.

## 14.3 Production acceptance rule

| **Release gate**The backend and data model are not production-ready until the runtime pilot, backup restore, access review and policy actions pass. A successful development demo is not sufficient evidence. |

|---|



# 15. Operations and support

| **Operational area** | **Minimum requirement** |

|---|---|

| Monitoring | Application health, database health, disk capacity, failed jobs, certificate expiry and backup status. |

| Backups | Encrypted scheduled backups, retention policy, off-host copy where approved, and periodic restore testing. |

| Updates | Versioned releases, release notes, migration pre-check, backup before upgrade, rollback procedure and pilot rollout. |

| Logging | Structured operational and audit logs with approved retention and restricted access; no unnecessary sensitive payloads. |

| Access reviews | Role assignments and system access reviewed at least once per term, per departmental Information Access Guidance — including system-administrator accounts — with a defined offboarding/revocation step for departed staff and event-day officials. |

| Risk register | Platform entry maintained in the school’s existing ICT risk register and reviewed on the department’s quarterly information-security risk-assessment cadence; the platform must not create a parallel risk process. |

| Incident response | Named contacts, containment process, evidence preservation, notification pathway and post-incident review. |

| Support | Clear responsibility for application defects, local infrastructure, identity, student exports and event-day escalation. |

| Exit and migration | Documented export format and ability to migrate data without depending indefinitely on one deployment operator. |



# 16. Risks and mitigations

| **Risk** | **Impact** | **Initial mitigation** |

|---|---|---|

| Unapproved handling of student data | Privacy, compliance and reputational harm. | Synthetic data until governance gates pass; minimise imported fields; complete required assessments. |

| Application becomes an unofficial student system of record | Conflicting identity and enrolment data. | Document authority boundaries; controlled snapshot imports; no uncontrolled identity editing. |

| No documented departmental pathway for in-house-built administration software | Assessment approach cannot follow a published self-assessment procedure; timeline risk on the §12 security gate. | Engage the department’s IT Security team directly (via Service Desk) early in Phase 0; obtain written confirmation of the applicable pathway; track the outcome as gate evidence. |

| Venue connectivity failure | Delayed or lost event results. | Offline-first queue, visible sync state, idempotent submissions and pre-event simulation. |

| Incorrect results or school records | Loss of trust and difficult historical correction. | Validation, verification states, revision history and explicit record ratification. |

| Single developer dependency | Support and continuity risk. | Repository documentation, automated tests, repeatable deployment, runbooks and named technical ownership. |

| School-specific code forks | Upgrade and support fragmentation. | Configuration and adapter boundaries; second-school deployment test before product claims. |

| Unsafe database migration | Data loss or downtime. | Versioned migrations, backup pre-check, restored-copy rehearsal and rollback plan. |

| Local hosting assumed to be automatically secure | Unpatched or unrecoverable service. | Explicit ownership of patching, TLS, monitoring, backups, access and disaster recovery. |

| Scope expands into full student management | Project becomes unmanageable and duplicates mandated systems. | Maintain sports-results boundary and staged module approvals. |



# 17. Governance and roles

| **Role** | **Accountability** |

|---|---|

| Executive sponsor / principal delegate | Authorises the initiative, risk treatment, production use and resourcing. |

| Business owner | Owns sports workflows, requirements, acceptance and operational outcomes. |

| Records custodian | Confirms record status, retention, correction and disposal requirements. |

| Privacy / security reviewers | Advise on assessments, data minimisation, hosting, access and risk treatment. |

| Technical owner | Owns architecture, deployment, updates, backups, monitoring and technical documentation. |

| Product developer | Implements approved scope, tests changes and maintains migration and release evidence. |

| Event coordinators and officials | Validate real workflows, complete training and report operational issues. |



## 17.1 Change-control principle

Changes should be separated into implementation, content/data, competition rules, user interface, deployment/tooling and documentation. Behaviour or schema changes require migration impact, test evidence and documentation updates. Production changes should follow: document decision → implement → static validation → runtime smoke test → documentation sync → controlled release.

# 18. Decisions requested

The review group is asked to decide the following:

Approve or reject the proposed standalone, per-school isolated deployment direction.

Approve Phase 0 discovery using no real student data beyond authorised de-identified samples.

Nominate the executive sponsor, business owner, technical owner and records custodian.

Confirm the departmental system that remains authoritative for student identity and enrolment.

Confirm the policy, privacy, security and software-assessment pathway that must be completed.

Confirm the preferred initial hosting environment for a synthetic prototype — resolved: Proxmox test environment (Linux virtual machine) on school infrastructure.

Nominate the first pilot workflow: swimming, athletics or a controlled simulated event — resolved: swimming carnival, Term 1 2027.

Confirm whether productisation for other schools is an endorsed future objective or only an architectural consideration.

| **Proposed resolution**Proceed with Phase 0 discovery and a synthetic-data technical foundation. Return to the review group with confirmed requirements, risk treatment, policy pathway, architecture decision records and an MVP delivery estimate before any production build or real student-data processing. |

|---|



# Appendix A. Initial data entities

| **Entity** | **Purpose / important fields** |

|---|---|

| School configuration | School identifier, name, timezone, branding, houses and approved settings. |

| Student | Internal ID and stable external student ID; minimal current identity fields. |

| Roster snapshot | School year, year level, house, competition group, import batch and effective date. |

| Import batch | Source metadata, mappings, validation results, reviewer, approval and promotion status. |

| Carnival | Type, date, venue, status, rule version and responsible owner. |

| Event definition | Sport, measure type, units, divisions, eligibility and record categories. |

| Carnival event | Scheduled instance, heat/lane settings, assigned officials and status. |

| Entry | Student-event participation, lane/heat and eligibility snapshot. |

| Result | Measured value, place, status, points, lifecycle state and current revision. |

| Result revision | Immutable prior/new value, actor, timestamp and reason. |

| School record | Category, ratified result, previous record, approval and effective period. |

| Role assignment | Identity subject/group, role, scope, effective dates and reviewer. |

| Audit event | Security and business activity evidence separate from normal application logs. |



# Appendix B. MVP user journeys

| **Journey** | **Expected outcome** |

|---|---|

| Administrator prepares carnival | Creates carnival, selects rule set, imports and approves roster, configures events, assigns officials and publishes the event-day package. |

| Official enters a result | Opens assigned event, searches eligible students, records result/status, sees validation, submits and receives accepted or queued status. |

| Verifier resolves an issue | Reviews duplicates, missing entries, unusual values and conflicts; corrects with reason; verifies event completeness. |

| Records manager ratifies a record | Reviews a final result against the applicable category and historical record, confirms evidence and explicitly ratifies or rejects it. |

| Coordinator finalises carnival | Confirms all queues are synchronised, events are final, points are reconciled and authorised reports are generated. |

| Technical owner restores service | Restores a known backup into a clean environment, validates schema/application compatibility and confirms reports and audit history. |



# Appendix C. Reference material

**[1] Victorian Department of Education — Software and Administration Systems Policy**

https://www2.education.vic.gov.au/pal/software-administration-systems/policy

Defines mandatory systems of record and assessment expectations for additional software and administration systems.

**[2] Victorian Department of Education — Privacy Impact Assessments**

https://www2.education.vic.gov.au/pal/privacy-information-sharing/guidance/privacy-impact-assessments

Guidance for assessing privacy impacts of initiatives or software handling personal, sensitive or health information.

**[3] Victorian Department of Education — Information Security Policy**

https://www2.education.vic.gov.au/pal/information-security/policy

Requires information-security risk management, access controls and protection of school information.

**[4] Victorian Department of Education — Information Access Guidance**

https://www2.education.vic.gov.au/pal/information-security/guidance/information-access

Role-based and need-to-know access guidance, including review and administration expectations.

**[5] Victorian Department of Education — Records Management Policy**

https://www2.education.vic.gov.au/pal/records-management/policy

Covers creation, safeguarding, retention, disposal, retrieval, export and migration of school records.

**[6] Public Record Office Victoria — Retention and Disposal Authority for School Records (PROS 2206)**

https://prov.vic.gov.au/recordkeeping-government/document-library/pros-2206-school-records

The PROV instrument that sets the actual retention and disposal periods that the department’s Records Management Policy [5] defers to. Line-item retention detail is not reliably retrievable by automated means; the records custodian must obtain the schedule directly from PROV and map its retention periods to the Appendix A data entities during Phase 0.

Policy interpretation and approval remain the responsibility of the school and the relevant departmental reviewers. All references were verified live and current on 24 July 2026; they should be re-fetched at Phase 0 kickoff because policy and guidance can change.

# Review comments requested

Is the proposed product boundary correct?

Are the system-of-record and records-authority assumptions correct?

Which required workflows, exceptions or event rules are missing?

Is the isolated per-school deployment model acceptable?

Who owns production support, backups and security response?

Which policy and approval steps apply before a prototype or pilot?
