# UI review captures

Screenshots of the screens this project argues about, for a human to look at
after a UI change. One command, from `app/`:

```
npm run screenshots
```

Then open `app/ui-review/index.html` in a browser — that is the review. It is a
plain local page: each screen with its 390 and 820 captures side by side, the
filename under each, and the run's timestamp in the header. No scripts, no
fonts, no network — it renders from the filesystem with the machine offline.

The run seeds its own carnivals through the dev API and writes the PNGs and the
gallery beside this file. Both are git-ignored; this README is not.

## Local-first, and it stays local

This folder never leaves the machine. Nothing in the pass uploads, syncs or
publishes anything, and the gallery is deliberately a file:// page rather than
anything hosted.

Publishing a capture set anywhere external — a claude.ai artifact, a chat
attachment, a ticket, a shared drive — happens **only on the owner's explicit
request**, and only ever with synthetic data. Silence is not consent: if a
capture would be useful to share, ask first.

## What it needs

The same local dev loop the e2e journeys use (docs/development/local-dev.md): the API
on :5080 with the synthetic seed, and Vite on :5173. Both are started for you if
they are not already running — a stack you started yourself is reused.

## What it captures

Every screen at **390px** (the smallest phone the pilot expects) and **820px**
(an iPad in portrait, past the 768px two-column deck breakpoint), full page:

| File | What it shows |
| --- | --- |
| `carnival-tiles-{w}.png` | The carnival landing: "My events" and the grid below it |
| `division-flat-{w}.png` | A division capture page whose selector is one flat row |
| `add-entry-eligible-{w}.png` | The same page with the eligible list narrowed by a search |
| `division-dense-{w}.png` | A division page whose selector is in axis mode (tier/category/age rows) |
| `deck-heat-first-{w}.png` | A division nobody has entered anyone into: the prospective `Heat 1` chip and the add panel below |
| `deck-lanes-{w}.png` | The flat deck marshalled from the rows: the heat chips above, lanes on the metadata lines |
| `deck-auto-fill-{w}.png` | A deck whose carnival auto-fills lanes (ADR-0038): every row arrived laned, including one added from the panel into the heat on screen |
| `deck-heat-empty-{w}.png` | A heat just added with `+` (ADR-0039): selected, empty, with the add panel under it |
| `deck-heats-{w}.png` | Two heats, the second selected, each chip carrying its own `n/N` |
| `deck-swap-confirm-{w}.png` | The deck's two-step Swap, armed (ADR-0030): brand-toned, not the red of Remove |
| `deck-remove-confirm-{w}.png` | The deck's two-step Remove, armed (ADR-0020): the confirm strip in place on the row |
| `deck-undo-window-{w}.png` | A row inside the five-second undo window (state slot + Undo) |
| `deck-settled-{w}.png` | The same row after the window closed (Recorded + Edit) |
| `outbox-{w}.png` | The outbox holding a capture taken offline |
| `dashboard-standings-{w}.png` | The Dashboard (ADR-0043 layout A) with house colours configured — standings beside program progress, then records today, participation and the organiser device row |
| `dashboard-text-only-{w}.png` | The same board at a school that configured no house colours (ADR-0044 §3): every row carried by the house name alone |
| `manage-{w}.png` | Manage — setup links, house points, lifecycle, and Finalise fenced at the foot |
| `manage-checklist-{w}.png` | Manage on a carnival created and left alone (ADR-0054 §1): the ordered setup checklist, only Roster done, Divisions marked as the step to do next |
| `manage-apply-template-{w}.png` | **A region, not a full page** — the Apply-a-template panel on that same carnival: the picker pre-selected from the carnival's type, and the sentence stating what applying will do |
| `event-new-blocked-{w}.png` | "Add an event" on a carnival with no divisions profile (ADR-0054 §3): the blocking notice, both ways out of it, and a submit that cannot fire |
| `carnival-switcher-{w}.png` | The switcher, reached the way an official reaches it: the header carnival chip |
| `deck-parked-{w}.png` | A deck holding a parked capture beside settled rows: the conflict badge as a tap target, a rejection, and a refused lane |
| `outbox-parked-{w}.png` | The recovery surface (ADR-0025): a conflicted result, a conflicted `entry.assign`, and a rejection, each offering only what it may |
| `outbox-discard-armed-{w}.png` | The two-step discard, armed — the confirm strip open over the row it would let go |
| `outbox-discarded-{w}.png` | The same capture after it was let go: `⊘ discarded`, and who let it go and when |
| `outbox-time-edit-{w}.png` | Edit-and-requeue in progress: the corrected-time field on a parked result |
| `school-hub-{w}.png` | The School hub (ADR-0055 §1): one row per school-wide section, each on its own address |
| `school-catalogue-{w}.png` | The event catalogue with no carnival behind it: grouped by sport, with the filters and each definition's blast radius |
| `school-catalogue-new-{w}.png` | "New definition" at rest — the catalogue-only create ADR-0054 §3 took off the carnival |

`index.html` is generated from whatever PNGs are actually on disk (a global
teardown, `scripts/screenshots-gallery.mjs`), so a partial run still opens — it lists
what it captured and nothing it didn't.

## What it is not

Not a test and not a pixel diff. It asserts only enough to know each capture is
of the state it claims to be, and it is excluded from `npx playwright test` — it
has its own config (`playwright.screenshots.config.ts`) so the journey suite
stays exactly the journeys.

Re-running overwrites each file in place. Ids and timestamps differ run to run;
layout does not, because the seed is the same shape every time.
