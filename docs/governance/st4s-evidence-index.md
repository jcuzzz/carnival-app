# ST4S evidence index

> **Template.** A one-page answer to "what do you have, and where is it?" for a Safer Technologies 4 Schools (ST4S) assessment or a department's alternative assessment.
> The "Provided by the repository" column points at documents in this repository; the "Your school supplies" column is what only your school can produce. Fill in the status column as you go.
> If your school is a Victorian government school, the alternative assessment via IT Security is the likely route for school-hosted software (`policy-pathway.md`).

**Numbering.** The rows are the questions a security and privacy questionnaire asks, not the official ST4S evidence-item numbers. Map them to the current ST4S framework when you prepare a submission: `[ST4S item references]`.

**Tiering.** The data held (name, year level, house, a gender-derived competition category; no date of birth, no contact details) is likely to fall in ST4S's lighter tier. Confirm with the assessor: `[tier]`.

---

## Index

| # | Evidence item | Provided by the repository | Your school supplies | Status |
|---|---|---|---|---|
| 1 | **Data inventory**: every personal-data field, its source and purpose | `data-inventory-and-ipp-mapping.md` §1–§3 | Confirmation that your import mapping matches the example | `[ ]` |
| 2 | **Data flow diagram**: roster in, results out | same, §4 | Host location; identity-provider data residency | `[ ]` |
| 3 | **Privacy principle mapping** | same, §5 (Victorian IPPs) | Answers to the gaps in §5–§6 | `[ ]` |
| 4 | **Privacy impact assessment** | Input only: the data inventory | The PIA itself, written by school staff and endorsed. **A gate before real data** | `[ ]` |
| 5 | **Collection notice** for students and families | — | The notice text, naming the platform | `[ ]` |
| 6 | **Access control matrix**: roles, least privilege, administrator controls | `access-control-matrix.md` §1–§3, derived from the authorisation code and pinned by tests | Reviewer sign-off | `[ ]` |
| 7 | **Authentication and MFA statement** | `access-control-matrix.md` §4: authentication is delegated to Entra ID or ADFS | Confirmation that MFA and conditional access apply to every account holding a role | `[ ]` |
| 8 | **Access review and offboarding procedures** | `access-control-matrix.md` §5–§6 (procedures) | Owners, and a record of each review and offboarding | `[ ]` |
| 9 | **Information security policy** (organisational) | — | The school's own policy; a risk-register entry for the platform | `[ ]` |
| 10 | **Incident response plan**, with notification triggers | `incident-response-plan.md` (stack-specific detection, containment, evidence) | Named contacts, reporting references, a tabletop exercise | `[ ]` |
| 11 | **Business continuity** | The design: captures continue through a server outage and are delivered later ([ADR-0002](../adr/0002-offline-sync-design.md)) | A continuity entry for "the platform is unavailable on carnival day" (paper fallback, who decides) | `[ ]` |
| 12 | **Disaster recovery**: backup, restore, RPO/RTO, dated drills | `backup-and-recovery.md`; `scripts/deploy/restore-drill.sh` | Key custodians, an off-host target, a measured RTO, the drill record | `[ ]` |
| 13 | **Encryption in transit and at rest** | HTTPS single origin with security headers (`deploy/Caddyfile`); `age`-encrypted backups | The TLS certificate source; disk encryption on the host | `[ ]` |
| 14 | **Retention and disposal schedule**, with export and deletion | `retention-and-disposal.md`: per-entity behaviour and a decommission procedure | Retention classes and periods (PROS 22/06 for Victorian government schools); how disposal is applied, since the software has **no server-side destruction mechanism** | `[ ]` |
| 15 | **Patch management**, with a named owner | `secure-development-and-vulnerability-management.md` §4 | The patching owner and arrangements | `[ ]` |
| 16 | **Vulnerability scanning** (dependency, static, container) | Same, §2: `npm audit`, `dotnet list package --vulnerable`, CodeQL, SBOMs, ESLint. **No container image scanning** | — | `[ ]` |
| 17 | **Penetration test report** | Same, §5: indicative scope | The test, if commissioned, or a self-assessment | `[ ]` |
| 18 | **Secure development lifecycle statement** | Same, §1–§2 | — | `[ ]` |
| 19 | **Sub-processor and third-party list** | Same, §7: no CDN, no analytics, no external fonts or scripts | Identity provider, host and backup target details | `[ ]` |
| 20 | **Vulnerability disclosure** | `SECURITY.md` (private reporting for the software) | The school's contact for deployment issues | `[ ]` |
| 21 | **Accessibility statement** | `docs/development/design-system.md`: 44 px targets and icons beside text (each with a few recorded exceptions), a high-contrast outdoor theme, `prefers-contrast` support | A WCAG 2.2 conformance statement, if required; there is no automated accessibility testing | `[ ]` |
| 22 | **Hosting and data location** | [Self-hosting](../admin/self-host.md): school-hosted stack, database on an internal network, single origin on 443 | Where the host is, who runs it, the instance record | `[ ]` |
| 23 | **Assessment pathway confirmation**: ST4S or an alternative assessment? | `policy-pathway.md` | The answer from the department or assessor. **The item that gates the rest** | `[ ]` |
| 24 | **Child safety** | The platform has no student login, no messaging, no photo or video upload, no public sharing and no external content | A deliberate statement to that effect | `[ ]` |

---

## Gates before real student data

Four items decide whether real data can be loaded at all: **the PIA (4)**, **the assessment pathway (23)**, **encryption at rest and an off-host backup (12, 13)**, and **the retention schedule (14)**. The rest make the assessment go well rather than decide whether it can start.
