# Carnival App documentation

Pages marked *(planned)* are not written yet.

## Using it — for school staff

- *(planned)* Setting up a carnival
- *(planned)* Importing the roster
- *(planned)* Carnival day: marshalling and recording results
- *(planned)* House points, records and reports
- *(planned)* Roles and shared devices

## Running it — for whoever hosts it

- [Linux runbook](admin/linux-runbook.md): from a fresh clone to a running carnival, from source or as the full stack
- [Self-hosting](admin/self-host.md): machine, Docker, TLS, sign-in, upgrades, backups
- [Configuration reference](admin/configuration.md): `.env`, configuration keys, school settings, event templates, carnival settings

## How it works — for contributors

- [Architecture overview](architecture/overview.md): components, request pipeline, how a result travels
- [Data model](architecture/data-model.md)
- [Result lifecycle](architecture/result-lifecycle.md): capture, validation, corrections, finalisation
- [Races, heats and lanes](architecture/races-and-lanes.md): heat numbers, lane auto-fill, adding heats, lane swaps
- [Offline outbox](architecture/offline-outbox.md): how no capture is lost when a connection drops
- [Authorisation and audit](architecture/authorisation-and-audit.md): sign-in, roles, policies, the audit trail
- [Scoring and house points](architecture/scoring.md)
- [School records](architecture/records.md)
- [Reports and exports](architecture/reports.md)
- [Roster import](architecture/imports.md)
- [Demo mode](architecture/demo-mode.md)
- [App navigation and carnival context](architecture/app-navigation.md): routes, tabs, the selected carnival, branding, role gating in the UI

## Developing it

- [Local development](development/local-dev.md)
- [Frontend build and deployment](development/frontend-deployment.md)
- [Design system](development/design-system.md): tokens, components, the 44px and icon rules
- [Working with AI agents](development/agent-practices.md)

## Decisions and background

- [Architecture decision records](adr/): why things are the way they are
- [Governance templates](governance/): privacy, security, backup and incident-response documents a school can adapt
- [Research](research/): the studies behind the technology and design choices
