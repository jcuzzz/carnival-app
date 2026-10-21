# Security policy

Carnival App is self-hosted by schools and handles student names and results. Please report vulnerabilities responsibly.

## Supported versions

Only the latest release and the `main` branch receive security fixes.

## Reporting a vulnerability

**Do not open a public issue, pull request or discussion for a security problem.** Use GitHub's private vulnerability reporting: the repository's **Security** tab → **Report a vulnerability**.

Include, as far as you can: what the issue is and where, minimal steps to reproduce, the impact, and a proof of concept as text.

**Test against a local instance only** (the [Linux runbook](docs/admin/linux-runbook.md) and [local development](docs/development/local-dev.md) seed synthetic data). Never test against a school's live instance. If you believe you have found something on a running school system, report it and stop: do not enumerate further, download data, or access student records. If a report involves what you believe is real student information, say so first and include no sample of it.

## What to expect

| Stage | Expectation |
|---|---|
| Acknowledgement | Within 7 days |
| Initial assessment | Within 14 days of acknowledgement |
| Fix for a critical issue | As quickly as practicable, with a release and an advisory |
| Credit | In the advisory, if you want it |

Schools running their own instance are responsible for their deployment, network and identity provider; report issues there to the school directly.

## No bug bounty

This is a volunteer project; there is no bug bounty.
