# Policy pathway (Victorian government schools)

> **Template.** This summarises the Victorian Department of Education policies that apply when a government school runs a locally hosted application like Carnival App, and the evidence each asks for.
> It is a reading of public policy pages, not legal advice: re-read each page before relying on it, as they are reviewed regularly. Anything in `[square brackets]` is your school's to fill in.
> Schools outside the Victorian government system can use the structure (assessment, privacy, security, access, records) and substitute their own policies.

## Summary

| # | Policy (Policy and Advisory Library) | What it means for a school running Carnival App |
|---|---|---|
| 1 | Software and Administration Systems | The platform processes identifiable student data, so it must be assessed before production use |
| 2 | Privacy Impact Assessments | Treat a PIA as required: non-department software storing personal student information |
| 3 | Information Security Policy | Add the platform to the school's quarterly risk register; review access termly; report incidents immediately |
| 4 | Information Access Guidance | Least-privilege roles, a termly access review, a system-administrator register, a defined offboarding procedure |
| 5 | Records Management Policy | A retention and disposal schedule per entity, derived from PROV PROS 22/06; principal-approved disposal |

---

## 1. Software and Administration Systems Policy

- URL: https://www2.education.vic.gov.au/pal/software-administration-systems/policy
- **Requires:** department-provided systems remain the systems of record for student and school data; any other software that processes personally identifiable, sensitive, health or important operational data must be assessed before adoption. Schools check the **Arc Software Catalogue** for an ST4S report and implement its required actions, or, where no report exists, engage the department's **IT Security team via the Service Desk** for an alternative assessment. Non-compliant, high-risk or non-participating products must not be used. Existing non-department software must be actioned by the end of 2028.
- **For Carnival App:** it processes identifiable student data (name, year level, house, competition group, results), so it is in scope even though it is not a commercial product.
- **Gap:** the policy and its guidance are written around adopting vendor software listed on Arc. Neither describes a procedure for a **school-hosted open-source or locally developed application**. Ask IT Security directly which pathway applies: an ST4S submission, or the alternative assessment.
- **Evidence before production:** written confirmation from IT Security of the pathway; either an ST4S report or a documented alternative-assessment outcome with tracked actions; sign-off recorded against the named business and technical owners.

## 2. Privacy Impact Assessments

- URL: https://www2.education.vic.gov.au/pal/privacy-information-sharing/guidance/privacy-impact-assessments
- **Requires:** a PIA when a school uses software not provided by the department that stores personal, sensitive or health information, or has higher-risk characteristics. School staff (not a vendor) complete the template, in three parts: analysis against the 10 Information Privacy Principles, risk-mitigation actions, and principal and Privacy team endorsement. It goes to the department's Privacy team before implementation.
- **For Carnival App:** in scope. Use `data-inventory-and-ipp-mapping.md` as the input to Part 1.
- **Evidence before production:** the completed PIA, Privacy team comments actioned, Part 3 endorsement recorded, a signed copy retained by the school.

## 3. Information Security Policy

- URL: https://www2.education.vic.gov.au/pal/information-security/policy
- **Requires:** protective measures across governance, information, personnel, ICT and physical domains. Schools run **quarterly** information-security risk assessments using the department's risk register, review system access **at least termly**, ensure **annual** security-awareness training, report incidents immediately, manage third-party arrangements securely and track ICT assets through their lifecycle.
- **For Carnival App:** it sits inside the school's existing quarterly risk-register process, not a parallel one, and its access review runs termly (`access-control-matrix.md` §5).
- **Evidence before production:** a risk-register entry; the termly access-review procedure with an owner; incident contacts and escalation documented and tested (`incident-response-plan.md`); staff holding administrator and official roles included in security-awareness training.

## 4. Information Access Guidance

- URL: https://www2.education.vic.gov.au/pal/information-security/guidance/information-access
- **Requires:** role-based access for school information systems; strict control of system-administrator accounts; access reviewed at least annually and ideally at the end of each term, with sysadmin access validated during offboarding.
- **For Carnival App:** the four roles are role-based and least-privilege in shape (`access-control-matrix.md`). Offboarding needs a defined procedure, especially for casual or volunteer officials.
- **Evidence before production:** the role and permission matrix; the termly review procedure with an owner; a system-administrator register with justifications; a tested offboarding procedure.

## 5. Records Management Policy

- URL: https://www2.education.vic.gov.au/pal/records-management/policy
- **Requires:** records kept for the minimum periods in the department's school records retention guidance; disposal only after the minimum period **and** with written principal approval; records subject to FOI, legal proceedings or of permanent historical value cannot be destroyed; systems must support retrieval, export and migration; compliance with standards issued by **Public Record Office Victoria (PROV)** under the Public Records Act 1973 (Vic), with permanent records eventually transferred to State Archives.
- **For Carnival App:** results, revisions, the record book, import batches and the audit trail each need a retention class from PROV's **PROS 22/06** (Retention and Disposal Authority for School Records, successor to PROS 01/01), mapped in `retention-and-disposal.md`.
- **Evidence before production:** the retention and disposal schedule per entity; the export and migration procedure (`retention-and-disposal.md` §4); a principal-approved disposal procedure; confirmation of which records, if any, are permanent. School sporting and carnival records are commonly treated as of ongoing historical value; confirm it rather than assume it.
- PROS 22/06 itself: https://prov.vic.gov.au/recordkeeping-government/document-library/pros-2206-school-records. Obtain the full schedule from PROV; the line items for sports and carnival records need to be read there.

---

## Related schemes

### ST4S (Safer Technologies 4 Schools)

ST4S is a national assessment scheme for the security, privacy and child-safety risk of school software, surfaced to Victorian schools through the Arc Software Catalogue. It is built around **supplier submissions** (a Readiness Check, then a full assessment). There is no documented self-service pathway for a school to submit software it hosts itself, so for Carnival App the realistic route is the department's alternative assessment via IT Security. `st4s-evidence-index.md` maps this repository's documents to the questions an assessment asks.

### Locally hosted and open-source applications

No departmental policy page speaks specifically to schools running software they host themselves. Record it in your risk register: *"No documented departmental pathway for school-hosted administration software; the assessment approach is agreed directly with IT Security."*

---

## Checklist by stage

Owners are your school's to assign. Each item needs a person, not the software, to close it.

### Before any real data

- [ ] Ask IT Security (via the Service Desk) which assessment pathway applies: ST4S submission or alternative assessment. Owner: `[principal]`
- [ ] Confirm with the Privacy team that a PIA is required; start Part 1 from `data-inventory-and-ipp-mapping.md`. Owner: `[privacy officer]`
- [ ] Obtain PROS 22/06 from PROV and map a retention class to each entity in `retention-and-disposal.md`. Owner: `[records custodian]`
- [ ] Confirm whether ratified school records are permanent records requiring transfer to PROV. Owner: `[records custodian]`
- [ ] Add the platform to the quarterly ICT risk register; assign the termly access-review owner. Owner: `[school ICT contact]`
- [ ] Name the records custodian, business owner, technical owner and executive sponsor. Owner: `[principal]`
- [ ] Have the role matrix (`access-control-matrix.md`) and audit schema (`data-inventory-and-ipp-mapping.md` §1.3–§1.4) reviewed against the Information Access Guidance and Records Management Policy. Owner: `[privacy officer]` / `[records custodian]`
- [ ] Re-read every policy page above for changes. Owner: `[privacy officer]`

### Pilot

- [ ] PIA Part 3 endorsement obtained (principal and Privacy team).
- [ ] ST4S or alternative-assessment outcome obtained; required actions tracked to completion.
- [ ] Termly access review exercised at least once; offboarding procedure tested.
- [ ] Incident-response contacts and escalation tested (tabletop or live).
- [ ] Backup restore drill run and recorded (`backup-and-recovery.md` §6).

### Production

- [ ] All of the above closed: PIA endorsed, assessment actions complete, retention schedule approved and applied, access review operating, risk-register entry active, restore validated.
- [ ] Written sign-off from the executive sponsor referencing the specific evidence collected.
- [ ] Permanent-record transfer obligations, if any, built into the records lifecycle before go-live.
