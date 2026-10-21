**School Sports Event****and Records Platform**

**Project Plan Proposal**

Draft for review  |  Version 0.1  |  24 July 2026

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

| Current version | 0.1 — initial proposal for review |

| Approval sought | Approval to complete discovery, governance assessment and synthetic-data prototype |

| Excluded approval | No approval is sought at this stage for production use or real student data |

| Next review | [Insert review meeting / date] |



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

| Identity | Approved OpenID Connect identity provider | Supports organisational SSO without the application handling directory passwords. |

| Deployment | Containerised, isolated instance per school | Supports local or approved cloud hosting with repeatable installation and upgrades. |

| Reporting | Application reports plus controlled exports | Keeps the transactional source reliable while allowing SharePoint/Power BI consumption. |



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



## 7.3 Configuration boundary

School name, branding and timezone.

Houses, age groups, divisions and eligibility rules.

Event catalogue, scoring rules and school-record rules.

Identity provider and group-to-role mappings.

Student import mapping and required fields.

Retention, export and operational settings approved for that school.

# 8. Identity and access

The application should integrate with an approved organisational identity provider using OpenID Connect or another approved federation mechanism. It should not collect or store normal staff directory passwords.

| **Role** | **Indicative permissions** |

|---|---|

| Event administrator | Create carnivals, configure events, allocate officials and open or close entry. |

| Results official | Enter and amend provisional results for assigned events. |

| Verifier | Review exceptions, resolve conflicts and finalise results. |

| Records manager | Ratify records and approve historical corrections. |

| Read-only viewer | View authorised results and reports. |

| System administrator | Manage configuration, imports, role mappings and operational settings; no unrestricted business override by default. |



Access must be enforced by the backend for every protected operation. Administrative roles should be tightly limited, logged and reviewed. Shared event devices require an agreed sign-in, lock-screen and session-expiry procedure.

# 9. Data and records integrity

## 9.1 Student and historical snapshots

Current student details may change. Historical competition results must therefore preserve the relevant event-time context, such as display name, year level, house and competition division, alongside the stable student identifier.

## 9.2 Result lifecycle

| **State** | **Meaning** | **Allowed transition** |

|---|---|---|

| Draft / queued | Captured locally or incomplete; not yet accepted by the server. | Submit or discard. |

| Provisional | Accepted and visible for operational review. | Correct, verify or invalidate with reason. |

| Verified | Checked by an authorised reviewer. | Finalise or return to provisional with audit reason. |

| Final | Included in official results and points calculations. | Controlled correction only. |

| Ratified record | Approved as an official school record. | Supersede through an authorised, fully audited process. |



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

| Privacy assessment | Completed school privacy review and Privacy Impact Assessment where required or recommended. |

| Security assessment | Required departmental / ST4S or alternative assessment pathway completed, with actions tracked. |

| Access control | Approved role model, least-privilege mapping, administrator controls, MFA/session approach and access-review procedure. |

| Records management | Approved retention, export, migration, disposal and legal-access process for results and records. |

| Hosting approval | Approved infrastructure, data location, network exposure, certificates, patching, backup and incident responsibilities. |

| Recovery validation | Successful backup restore and documented disaster-recovery procedure. |

| Operational acceptance | Synthetic test, controlled pilot and production readiness sign-off. |



Current Victorian Department policy states that department-provided systems of record must continue to be used for their defined purposes. Additional administration systems handling identifiable or important operational data are subject to software assessment requirements. Department guidance also identifies Privacy Impact Assessments as a mechanism for assessing new software that handles personal or sensitive information, and requires schools to manage access, security risk, records retention and recoverability. See Appendix C.

# 13. Delivery plan

| **Phase** | **Purpose** | **Key outputs** | **Exit status** |

|---|---|---|---|

| 0. Discovery and governance | Confirm owner, workflows, data, policy path and pilot conditions. | Current-state map; data inventory; identity/hosting assessment; requirements; risk register; approval path. | READY FOR SYNTHETIC PROTOTYPE |

| 1. Technical foundation | Establish safe architecture using synthetic data. | Repo governance; deployment baseline; identity test; database migrations; audit framework; import staging; test automation. | VALIDATION PENDING |

| 2. MVP workflow | Implement one end-to-end carnival slice. | Roster import; carnival/events; result entry; verification; points; record candidates; reports; offline queue. | FIELD PILOT CANDIDATE |

| 3. Controlled pilot | Exercise the workflow with nominated staff and synthetic or approved limited data. | Training; field simulation; defect log; connectivity test; restore test; operational procedures. | PRODUCTION REVIEW |

| 4. Production release | Deploy for an approved event. | Approved hosting; production configuration; access groups; monitoring; backup; support plan; sign-off. | COMMIT READY / RELEASE READY |

| 5. Productisation | Prove repeatable deployment for another school. | Configurable branding/rules; installer; second-school synthetic deployment; upgrade/rollback package; support boundary. | REPEATABLE DEPLOYMENT |



## 13.1 Phase 0 discovery tasks

Identify the exact departmental student platform and approved export mechanism.

Collect representative, de-identified export files and current reconciliation reports.

Document swimming and athletics workflows from setup through final records publication.

Confirm event rules, scoring, ties, disqualifications, corrections and record ratification.

List devices, venues, expected network conditions and shared-device practices.

Confirm the approved identity provider and whether staff group information is available.

Determine the policy assessment, procurement and hosting approval pathway.

Nominate a pilot carnival or simulation and the staff who will review it.

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

| Incident response | Named contacts, containment process, evidence preservation, notification pathway and post-incident review. |

| Support | Clear responsibility for application defects, local infrastructure, identity, student exports and event-day escalation. |

| Exit and migration | Documented export format and ability to migrate data without depending indefinitely on one deployment operator. |



# 16. Risks and mitigations

| **Risk** | **Impact** | **Initial mitigation** |

|---|---|---|

| Unapproved handling of student data | Privacy, compliance and reputational harm. | Synthetic data until governance gates pass; minimise imported fields; complete required assessments. |

| Application becomes an unofficial student system of record | Conflicting identity and enrolment data. | Document authority boundaries; controlled snapshot imports; no uncontrolled identity editing. |

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

Confirm the preferred initial hosting environment for a synthetic prototype.

Nominate the first pilot workflow: swimming, athletics or a controlled simulated event.

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

Policy interpretation and approval remain the responsibility of the school and the relevant departmental reviewers. The references above should be rechecked during Phase 0 because policy and guidance can change.

# Review comments requested

Is the proposed product boundary correct?

Are the system-of-record and records-authority assumptions correct?

Which required workflows, exceptions or event rules are missing?

Is the isolated per-school deployment model acceptable?

Who owns production support, backups and security response?

Which policy and approval steps apply before a prototype or pilot?
