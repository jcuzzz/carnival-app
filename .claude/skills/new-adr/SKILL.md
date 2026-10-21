---
name: new-adr
description: Create an Architecture Decision Record for a significant design decision, or supersede an existing one. Use when a design/behaviour decision is being made or reversed — "should we", "let's switch to", "decide", "ADR" — before implementing the change.
---

# ADR workflow

ADRs are secondary and short; the docs tree (`docs/README.md`) is the primary documentation. See **Documentation** in `AGENTS.md`.

## When

Only for a **significant design decision**: one a future contributor would otherwise reverse, or ask "why is it like this?". Routine changes, fixes and UI tweaks get no ADR; they update the docs tree only.

## Steps

1. Number: the next free `NNNN` in `docs/adr/`. Filename `docs/adr/NNNN-short-kebab-title.md`.
2. Copy `docs/adr/0000-template.md`: **Status**, **Context**, **Decision**, **Consequences**, **Rejected alternatives**. About one page.
3. Status is `Proposed` until the maintainer accepts it in review (usually on the pull request), then `Accepted (YYYY-MM)`. Never mark your own ADR accepted.
4. Write the Decision as the rule that holds, in the present tense. **No implementation notes, file lists, test counts, ruling transcripts, review logs or dated amendment sections.** Implementation detail goes in the architecture page; history goes in git.
5. **A changed decision gets a new ADR** that supersedes the old one. Set the old one's Status to `Superseded by ADR-NNNN` (or `Partly superseded by ADR-NNNN`, saying which part); do not append to it.
6. Substantial option analysis goes in `docs/research/<topic>.md`, linked from Context.
7. **In the same change, update the docs tree**: `docs/architecture/` (how it works), `docs/admin/` (running it) or `docs/guide/` (using it). Link the ADR from that page where it explains why. A decision without its docs update is incomplete.
