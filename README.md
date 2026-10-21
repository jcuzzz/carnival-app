# Carnival App

Free, self-hostable software for running school **swimming and athletics carnivals**: import the roster, record results live on any phone, tablet or laptop, verify them, and watch the house points add up. Optional school records track every record broken.

> **Status: pre-release.** The carnival workflow is complete and tested; a school pilot runs in Term 1 2027. Expect breaking changes until 1.0.

## What it does

- **Roster import** from a simple CSV (student ID, names, date of birth, gender, house, year level). Age groups are derived from the date of birth and a configurable cut-off day; the birth date itself is not stored.
- **Carnival setup** from swimming and athletics event catalogues: divisions by age and gender, heats and lanes, field events with attempts, high-jump ladders, relays, novelty events.
- **Live result entry** as a PWA. Results sync immediately; if the connection drops, an outbox holds every capture until it returns, and resubmission is idempotent.
- **Verification workflow**: provisional → verified → final, with an audit trail.
- **House points** with configurable scoring, bonus and novelty points, and a live dashboard.
- **School records** (optional): detection when a mark beats the standing record, ratification, history.
- **Reports**: results book, house totals, district team selection export.
- **Per-school configuration**: houses, colours, branding, scoring, roles. No code changes needed.

## Run it

- **On Linux, from a fresh clone:** [docs/admin/linux-runbook.md](docs/admin/linux-runbook.md), from source with synthetic data in a few commands, or the full Docker stack.
- **Develop on Windows:** [docs/development/local-dev.md](docs/development/local-dev.md).
- **Self-host for a school:** [docs/admin/self-host.md](docs/admin/self-host.md): Docker Compose with PostgreSQL 17, backups and Caddy.

## Documentation

- [Architecture decision records](docs/adr/): one file per decision
- [Design system](docs/development/design-system.md): tokens and component rules
- [Governance templates](docs/governance/): privacy, security, backup and incident-response documents a school can adapt
- [Research notes](docs/research/)

## Licence

Carnival App is free software under the [GNU Affero General Public License v3.0](LICENSE). Any school, organisation or company may use, host and modify it, including as a paid hosted service. If you run a modified version for others over a network, you must offer them its source.

**Commercial licences** are available for organisations that want to integrate Carnival App into a proprietary product without the AGPL's obligations. Contact the maintainer through GitHub. The AGPL edition is the complete product; no features are held back.

## Contributing

Issues and bug reports are welcome. Pull requests are not accepted yet: dual licensing needs a contributor licence agreement first. See [CONTRIBUTING.md](CONTRIBUTING.md).

Security reports: see [SECURITY.md](SECURITY.md).
