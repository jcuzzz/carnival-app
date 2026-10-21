---
name: handoff-update
description: End-of-session sync — check every behaviour change made in the session updated its docs-tree page, then update docs/HANDOFF.md (and BACKLOG.md if owned by this session) so the next agent picks up cleanly. Use when wrapping up, committing final work, or when the user says "wrap up", "hand off", or "end session".
---

# Session handoff update

Before ending a work session that changed anything meaningful:

1. **Docs-tree check.** For every change this session that altered behaviour, structure, configuration or operation, confirm the relevant page was updated: `docs/architecture/` (how it works), `docs/admin/` (running it), `docs/guide/` (using it). Fix any that were missed; a change without its docs update is incomplete. If a significant design decision was made, confirm a short ADR exists (the `new-adr` skill) and that no ADR was appended to instead of superseded.
2. **`docs/HANDOFF.md`**: update the state of play, active work (what moved, what is half-done with the exact next step) and anything new a following agent must know. Terse: a pointer document, not a diary. Delete stale lines rather than appending.
3. **`docs/BACKLOG.md`**: tick or add items only if no other agent is working on it concurrently; otherwise list the deltas in HANDOFF.
4. Sync `api/README.md` or `app/README.md` if your work invalidated them.
5. Commit the docs sync as its own commit (e.g. `Docs sync: handoff after <work>`). Do not push unless asked.
