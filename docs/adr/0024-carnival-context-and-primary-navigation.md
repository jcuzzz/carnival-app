# ADR-0024: A selected carnival, and three tabs instead of a button row

- **Status:** Accepted (2026-08). Partly superseded by [ADR-0055](0055-the-school-area.md): roster import is reached from the School area, not from Manage.

## Context

The carnival was held only in the URL, so every session re-established it by navigating a list, and nothing outside the carnival's own screens could name the carnival being worked in. The carnival landing page mixed four kinds of thing in one button row: a read view (standings), a document (the program), an editor (the program builder) and lifecycle transitions, including the irreversible Finalise, which had the visual weight of a link. The standings screen likewise mixed a public leaderboard with the scoring-rules editor and the adjustments panel. A Draft carnival landed on an empty tile grid.

Officials share devices ([ADR-0006](0006-shared-device-and-byo-session-policy.md)), and an administrator records results on the day ([ADR-0009](0009-administrators-can-record-results.md)).

## Decision

### 1. The selected carnival is device-local context

The selected carnival (id, name, date, status) lives in `localStorage` on the device, not on the server: a per-account preference would be fought over by two officials on one shared tablet, and would need an uplink. Selecting a carnival is a different act from switching official.

When the school has exactly one `Open` carnival and nothing is selected, it is selected silently; the header chip always names the selection, so it is visible without being announced.

### 2. `/` resolves to the selected carnival

`/` redirects (replace, not push) to the selected carnival's landing tab. With nothing selected, or when the selection no longer resolves, it falls through to `/carnivals`, which is the switcher. A selection is dropped only when the server answers 404 for it: offline, an error is not evidence that the carnival is gone.

### 3. A carnival chip in the header

The header always shows the selected carnival (name, date, status) and opens the switcher.

### 4. Three carnival-scoped tabs

| Tab | Route | Job |
|---|---|---|
| **Deck** | `/carnivals/:id` | Get to the event being captured. Nothing else. |
| **Dashboard** | `/carnivals/:id/dashboard` | How the carnival is going: standings, progress, the program. Read-only. |
| **Manage** | `/carnivals/:id/manage` | The organiser's macro changes: setup, program builder, scoring rules, adjustments, reports, lifecycle. |

The old `/standings` address redirects to the Dashboard. Manage is visible only to carnival and system administrators — the tab is hidden, not just its buttons — and the server checks every action behind it regardless. Finalise sits alone at the foot of Manage, with its own confirmation, apart from Open/Close.

The Outbox is reached from the persistent header indicator, never from behind the role-gated Manage tab, so queued work is one tap away for every official.

### 5. The landing tab depends on status

| Carnival status | Lands on |
|---|---|
| `Draft` | **Manage** (Deck for anyone who cannot manage, so nobody lands on a refused page) |
| `Open` | **Deck** |
| `Closed`, `Finalised` | **Dashboard** |

Role never chooses between Deck and Dashboard. An empty Deck shows one link, "Set the carnival up", to a manager only; a non-manager on a Draft carnival lands on an empty Deck, which is accepted for a Draft-only state.

### 6. Deep links adopt

Opening any `/carnivals/:otherId/…` address adopts that carnival as the selection (replace, so Back does not resurrect the old one). The selection is a default, never a lock: printed and shared links keep working.

## Consequences

- An official opens the app on the page their carnival needs, and the chrome always answers "which carnival am I affecting".
- Each tab has one job and can grow on its own; Finalise is no longer a sibling of "Program".
- There is now device-local state that can be stale; the adopt rule and the fall-through are where "which carnival?" bugs start.
- A carnival whose status changes under a loaded page leaves the official where they are; only the landing rule reads status.
- Auto-selection does nothing when two carnivals are open at once, so the convenience disappears exactly on a busy week.
- A failed `localStorage` read degrades to "nothing selected"; a failed write keeps the selection for that tab only.
- Entirely client-side: no API change.

## Rejected alternatives

- **Server-side selected carnival** — fought over on shared devices and needs an uplink.
- **Role-shaped landing** — an administrator captures results on the day, so landing them away from the Deck taxes the common case.
- **Announcing auto-selection, or always requiring an explicit pick** — an extra tap on carnival morning with nothing to choose between.
