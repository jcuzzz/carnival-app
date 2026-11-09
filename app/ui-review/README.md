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
| `manage-checklist-{w}.png` | Manage on a carnival created before any student list (ADR-0066 §4): Students, Events, Program, Open, with Students marked as the step to do next and Events reading "Import the students first"; step 4 shows both opens greyed |
| `manage-open-not-ready-{w}.png` | **A region** — checklist step 4 on that empty carnival (ADR-0066 §5): "Open carnival" and "Open the carnival only" greyed out under "Import the students and add events first." |
| `manage-ready-to-open-{w}.png` | **A region** — checklist step 4, Open, on a Draft carnival (ADR-0066 §5–6): the event and division count, the "Before you open" warnings, and the two opens, enabled |
| `deck-draft-notice-{w}.png` | The Deck of a Draft carnival that has events: "This carnival isn't open yet", with the way to Manage |
| `deck-not-set-up-{w}.png` | The Deck of a carnival not yet set up (ADR-0066 §7): one "Set the carnival up" link to Manage |
| `event-setup-empty-catalogue-{w}.png` | Event setup over an empty catalogue: the standard events, in place |
| `event-setup-no-roster-{w}.png` | Event setup with no saved ages and no student list (ADR-0067 §5): the import notice in place of the standard events |
| `ages-no-roster-{w}.png` | Ages and categories with nothing to narrow: the import notice and no form |
| `event-new-blocked-{w}.png` | "Add an event" with no saved ages and no student list (ADR-0067 §5): the import notice and a submit that cannot fire |
| `event-setup-standard-events-{w}.png` | Event setup on an empty carnival whose student list is in: the ages line and "Add the standard swimming events" as the screen's primary |
| `event-setup-ages-line-{w}.png` | **A region** — the ages line and its "Ages and categories" link on a carnival with saved ages |
| `event-setup-standard-events-open-{w}.png` | **A region** — "Add the rest of the standard events" opened on a carnival that already has events |
| `event-setup-ages-stale-{w}.png` | Saved ages including one no student has (ADR-0067 §6): the warning and "Remove those ages" |
| `event-setup-ages-left-out-{w}.png` | Student-list ages the saved ones leave out: the quiet line, no warning |
| `ages-saved-no-roster-{w}.png` | Ages and categories with saved ages and no student list: the ages read-only, categories and tiers editable |
| `carnival-switcher-{w}.png` | The switcher, reached the way an official reaches it: the header carnival chip |
| `deck-parked-{w}.png` | A deck holding a parked capture beside settled rows: the conflict badge as a tap target, a rejection, and a refused lane |
| `outbox-parked-{w}.png` | The recovery surface (ADR-0025): a conflicted result, a conflicted `entry.assign`, and a rejection, each offering only what it may |
| `outbox-discard-armed-{w}.png` | The two-step discard, armed — the confirm strip open over the row it would let go |
| `outbox-discarded-{w}.png` | The same capture after it was let go: `⊘ discarded`, and who let it go and when |
| `outbox-time-edit-{w}.png` | Edit-and-requeue in progress: the corrected-time field on a parked result |
| `school-hub-setup-{w}.png` | The School hub of a fresh school (ADR-0066 §2): "Set up your school" above the section rows, under the unnamed header |
| `dev-login-{w}.png` | The dev server's sign-in picker: each persona with its roles in words |
| `header-pre-name-{w}.png` | **A region** — the shell header before the school is named: "Carnival App" beside the trophy, on the default navy |
| `header-pre-name-yellow-{w}.png` | **A region** — the same header on a light yellow brand, whose ink is dark |
| `header-named-yellow-{w}.png` | **A region** — a named school's header on the yellow brand: monogram, name, role words and the outbox pill |
| `school-hub-{w}.png` | The School hub (ADR-0055 §1) once the school is set up: no setup list, only one row per school-wide section |
| `carnivals-empty-{w}.png` | The carnival list with no carnivals at a school with no name: "Set up the school first" |
| `import-from-carnival-{w}.png` | The roster import launched from a carnival (ADR-0066 §4): the year from the carnival, the way back to Manage |
| `import-promoted-back-{w}.png` | The same import after the one press (ADR-0066 §8): "Back to" the carnival |
| `import-result-{w}.png` | **A region** — the result line: what the update did to the student list, from the batch's counts |
| `import-houses-required-{w}.png` | An upload refused with `SCHOOL_HOUSES_REQUIRED`: the notice and its link to School settings |
| `school-people-role-{w}.png` | **A region** — People's Add a person form with a role chosen |
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
