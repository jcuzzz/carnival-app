# Contributing to Carnival App

Thanks for helping. Issues are the way in for now; see below on pull requests.

## Before you start

- **Bugs:** open an issue with steps to reproduce, using synthetic data only.
- **Features or behaviour changes:** open an issue first. Significant design changes need an ADR (`docs/adr/`, template `0000-template.md`) before code.
- **Never include real student data** in issues, pull requests, screenshots or fixtures.

## Development

Read [AGENTS.md](AGENTS.md); it holds the project's rules, commands and conventions for humans and AI agents alike. Local setup: [docs/development/local-dev.md](docs/development/local-dev.md).

Before proposing a change, check it against the gates:

- `cd api && dotnet test`
- `cd app && npx tsc -b && npm run lint && npm test && npm run build`
- For UI changes: `npm run screenshots` and review the captures; read `docs/development/design-system.md` first.

Keep pull requests focused. Follow the code-comment rule in AGENTS.md.

## Pull requests are not accepted yet

Issues, bug reports and discussion are welcome. Code contributions wait until the project adopts a contributor licence agreement: Carnival App is dual licensed (AGPL and commercial), which needs the maintainer to hold the rights to every line. Until then, describe the change in an issue instead.

## AI-assisted contributions

Welcome. You are responsible for what you submit: review it, test it, and make sure it follows AGENTS.md.
