# Research: athletics carnival statistics worth highlighting

The question: *what athletics carnival stats might we highlight*, on a carnival-day dashboard
and in the end-of-day results book. Scope: a typical Victorian government secondary school,
years 7–12, four houses, place points 4/3/2/1/1/1/1 + participation, novelty as ADR-0005
house-point adjustments. Swimming came first; athletics is the next sport.

*Since this study, several §3 modelling gaps have been built: attempt series (b), high-jump
bar heights with countback (c), the no-mark status (d) and an athletics event template (k).
See [result lifecycle](../architecture/result-lifecycle.md) and [scoring](../architecture/scoring.md).*

This file carries evidence and options only. Every proposal in §5 needs an ADR before it is
built (AGENTS.md: ADR before behaviour change). Third-party claims are from web sources read
2026-08-31 and cited in §6; where a page could not be parsed (SSV's specification PDFs return
binary to the fetcher) the claim is attributed to the search summary and marked as such.

Legend for the "have it?" column: **Have** = computable from data the platform already stores;
**Partial** = the inputs exist, the aggregate does not; **Gap** = needs new data.

---

## 1. Carnival-day dashboard — ranked shortlist

Ranked by value to the person who is actually looking at a screen on the day: the carnival
coordinator first, then students/parents at the venue display (ADR-0004 tier 2).

| # | Stat | What it tells whom | Data needed | Have it? | Privacy / minimisation |
|---|---|---|---|---|---|
| 1 | **House standings, three components visible** (placement / participation / bonus) | Everyone. The one number the day is about; components keep it reproducible (ADR-0005) | `ScoringService.ComputeAsync` as-is | **Have** | House-level aggregate; no student data. Safe on a public screen |
| 2 | **Program progress against the clock** — "expected results in: 61%; running ~12 min behind slot 09:45" | Coordinator. The only stat that changes a decision *during* the day | `ScheduleSlot.Start` (have) + `Result.RecordedAtUtc` per slot's events (have) + `progress.ts` counts (have) | **Partial** — no slot↔actual comparison exists | Aggregate. Safe |
| 3 | **Event completion spine** — per stream/slot: not started / in progress / complete, with outstanding count | Coordinator + officials. Which station is holding up the day; the ADR-0004 finalisation blocker, early | `CarnivalEvent` + entry/result counts (have; `EventResultsScreen` already shows per-heat counts) | **Partial** — exists per event, not as a day-level spine | Aggregate. Safe |
| 4 | **Unsynced capture** — items queued, devices not drained | Coordinator. Tells them the board is *not yet* the truth. Athletics makes this worse than swimming: field stations sit away from wifi for an hour at a time | Outbox counts (have, `OutboxIndicator`) | **Have** — but not on the dashboard | Device-level. Staff view only |
| 5 | **Age champion race** — per division (e.g. "14 Girls"), top 3 on champion points, provisional-badged | Students/parents; it is the thing athletics carnivals are remembered for. Second-highest ask in every comparable product | Champion point table (config, **Gap**) + per-student aggregation across events (`Entry.StudentId` exists; `ScoringService` aggregates to house only) | **Gap** | Names + division label only. Division labels are permitted (ADR-0026 §2); a **per-student** category label is not. Never render a student's M/F/S, and never title a board by anything but the division |
| 6 | **Records set today** (provisional) badged against the standing book | Everyone. The moment worth announcing | `EventLadder` best per division + `GET /records` (both have). ADR-0028 mints only at finalisation — a day-badge is a read-only comparison, not a mint | **Partial** | Positive singling-out; the holder's name is already publishable at PA level (ADR-0004 tier 2) |
| 7 | **Placings tally per house** (1sts / 2nds / 3rds) | Students. Explains *why* a house leads when points are close | `Placement.Rank` (have) | **Have** | Aggregate. Safe |
| 8 | **Participation rate per house** — entrants as a share of the house's eligible roster | Coordinator; also the fairest lever a small house has | Roster snapshot counts per house (have) + distinct entrants (have) | **Partial** | Report as a **rate per house**, never as a named list of who did not compete. A "students with no entries" screen is a staff marshalling tool, not a dashboard tile |
| 9 | **Next up / marshalling call** — the next 2–3 slots per stream and who is called | Officials, students. Athletics runs 4–8 concurrent streams; swimming ran one | `ScheduleStream`/`ScheduleSlot` (have; the model already anticipates "Track plus one per field station") | **Partial** | Names on a call list are PA-equivalent. Configurable name display per ADR-0004 tier 2 |
| 10 | **Field-station state** — athletes remaining, round 2 of 3, bar at 1.35 m | Field officials. Athletics-only; no swimming analogue | Attempt series (**Gap**, §3) | **Gap** | Aggregate per station. Safe |
| 11 | **Relay results** as they land | Everyone. Double points make them the day's swing (ADR-0008 `ScoringWeight` 2) | Have | **Have** | Team name/house, not four names, on a public board |
| 12 | **Personal bests / improvement** | Students. High emotional value, near-zero data in year 1 | Prior marks per student across years | **Gap**, and empty by construction — records "start fresh" (ADR-0028 ruling 1) and a carnival runs once a year | Cross-year per-student performance history is a materially bigger privacy footprint than a single carnival. Needs its own ADR and probably a PIA line |
| 13 | **Wind-legal marks** | Nobody, at a school carnival | Wind gauge + per-attempt readings | **Gap** | No school interhouse carnival runs a gauge. See §3 |
| 14 | **Multi-event / pentathlon totals** | Nobody, at a typical school carnival | Combined-event point tables | **Gap** | Not the school's program |

**Explicitly out on privacy grounds**, at any tier:

- Any per-student category (M/F/S) label, anywhere on a client — ADR-0026 §2 is absolute, and
  an athletics dashboard multiplies the surfaces that could leak one (age-champion boards,
  station lists, ribbon lists).
- A "slowest", "most DNFs", "did not compete" or "no entries" board. DNS/DQ/withdrawal lists
  are operational, staff-authenticated, and never on the venue display.
- Anything ranking a *named student* on something they did badly. The dashboard singles a
  student out only for a positive fact (a placing, a champion lead, a record).
- Health/injury/withdrawal reasons, which the platform does not model and must not acquire as
  a "why is this event short" note field.

---

## 2. End-of-day / results-book statistics

The book is ADR-0004 tier 1 (in-app report + PDF-via-print + administrator-only CSV). Athletics
adds these to what the swimming book already prints:

| Stat | Notes | Have it? |
|---|---|---|
| Final house standings, components itemised, adjustment ledger listed | ADR-0005 requires the components stay visible; the athletics day has more novelty/spirit items than swimming | **Have** |
| Full results by event × division, with places | `ResultsBook.ResultRow` | **Have** |
| **Age champions and runners-up per division, with the point breakdown** | The breakdown is what stops the argument. Countback rule printed alongside | **Gap** (needs §3-i) |
| **Records set** — new and, separately, equalled | ADR-0028: equalling does not supersede, so the book is the only place an equalling mark is recognised | **Partial** |
| Best mark per event × division (the record-book seed) | `EventLadder` top row | **Have** |
| District / next-level selection list | `DistrictSelection`, top 2 per division, Competitive only | **Have** |
| **Ribbon / certificate distribution list** — per student, what they collect | Comparable products all ship this; it is the highest-value *staff* report of the day. Administrator-gated, never published | **Partial** (derivable from placings) |
| Participation summary per house and per year level | Rates, not names | **Partial** |
| **Full field-event series** — every attempt, fouls marked | Standard in every athletics results book; needed to *justify* a countback placing | **Gap** (§3-b/c) |
| Relay results with legs | `RelayLeg` exists; the book prints the team name | **Have** |
| Novelty/adjustment ledger with reasons and actors | ADR-0005 audit shape | **Have** |
| Per-student result history | Deliberately not in the book — ADR-0004 §6, "the book is not a navigation surface", and a per-student view is a different privacy question | — |

---

## 3. Athletics modelling gaps vs. the swimming-centric model

**Config/template vs schema is the load-bearing column.** School rules are data (AGENTS.md);
the schema should only grow where athletics has a *shape* swimming does not.

| # | Item | What it is | Config/template or schema | Notes |
|---|---|---|---|---|
| a | Distance and height measures | `MeasureType.Distance` / `.Height` already exist; `Placement.HigherIsBetter` already inverts direction; `ResultValueParser` already accepts plain decimals | **Neither — have it** | The only work is display precision/units and a positional entry mask for `4.32` the way ADR-0041 did for time. App-level |
| b | **Best of N attempts** | Three trials per athlete is the school norm; SSV secondary rules say "three rounds"; Hy-Tek allows up to six; some products offer a bonus final for the top N | **Schema** (+ config for N and for whether a final round runs) | `Result` holds one `MeasuredValue`. Needs an attempt series — `ResultAttempt(ordinal, value?, foul, pass)` or a JSON series on `Result` — with the best mark derived, not typed |
| c | **Progressive high-jump bar heights** | Attempts are per *height*, not per trial; three consecutive failures eliminate; the bar is never lowered for a returning athlete | **Schema** (attempts at heights, O/X/P per height) + **config** (start height, increment) | The countback tie-break (fewest failures at the tied height, then fewest overall) **cannot be expressed by `Placement.Rank`** as it stands. This is the single largest athletics modelling item |
| d | **Foul / pass / no-mark** | A foul is not a DNF; an athlete who fouls all three has *no mark* (NM) — ranked below every mark but present, entered, and not a no-show | **Schema** | `ResultStatus` is `Ok/Dnf/Dns/Dq`. NM is a fifth state, or a derived one from an all-foul series. Do not overload DQ |
| e | Wind readings | 2.0 m/s tailwind is the legality threshold for sprints ≤200 m, hurdles, LJ, TJ. An over-wind mark still scores; it just cannot ratify a record | **Schema if ever** — recommend **not for the pilot** | No school interhouse carnival runs a gauge, and the SSV-level record tables the school compares against do not print wind. If added: nullable per result/attempt + a record-eligibility filter in `RecordDetection` |
| f | **Implement weights per age/category** | Shot/discus/javelin masses change by age and category (e.g. Little Aths U15 shot: 3.0 kg girls / 4.0 kg boys; discus 1.0 kg both). SSV record entries name the implement in the event title — *"Discus (1.0kg)"* | **Config/template** | Cleanest fit with what exists: **one `EventDefinition` per implement spec** (`AT-SHOT-3KG`, `AT-SHOT-4KG`), each declaring the age bands that contest it. `RecordCategoriesJson` is keyed by (definition, band, category), so sharing one definition across two masses would merge incomparable marks into one record. No schema change |
| g | Hurdle heights/spacings per age | Same shape as (f) | **Config/template** | SSV has removed hurdles from *secondary interschool*; the school may still run them interhouse. Model as separate definitions if heights differ |
| h | **Track vs field concurrent scheduling** | Athletics runs one Track stream plus one per field station, all at once | **Neither — have it** | `ScheduleStream` was built for exactly this; the swimming template's own comment says "athletics would declare Track plus one per field station". Gap is the *clash view*: an athlete due at two stations. SSV's rule ("the bar will not be lowered" on return) is the real-world handling — a display problem, not a model problem |
| i | **Age champion** | Points per placing accumulated per student within a division; countback on ties | **Config** (point table + tie-break rule, riding the carnival's versioned rule set like `ScoringRules`) + **compute-on-read** | No new table. `ScoringService` aggregates to `SnapshotHouseCode`; the same loop keyed on `Entry.StudentId` produces this. Note the champion table is usually **not** the house table (school examples use 8/6/4/2; ours is 4/3/2/1) — two independent tables, both config |
| j | Rounds (heats → semi → final) | Athletics track at school level is usually timed finals, as swimming is | **Open question, not a build** | ADR-0015 §1 already treats each heat as its own competition. If the school ever runs a real final, that is a decision, not a refactor to pre-empt |
| k | Athletics event template | `config/event-templates/athletics.json` does not exist | **Config** | The main deliverable, and pure data: definitions (sprints, middle distance, jumps, throws, 4×100 relay), age bands (single-age, as swimming was corrected to), the concurrent stream/slot program, tags (`discipline:Sprint`, `surface:Track`/`Field`) |
| l | Age groups | Age as at 31 December, SSV convention | **Have** | The roster import already derives exactly this |
| m | Multi-class / all-abilities divisions | SSV secondary contests multiclass bands (12–14, 15–16, 17–20) | **Open question** | Expressible as age bands, but the *category* axis is M/F/S; a multiclass axis is a third dimension. Not pilot scope, and it carries a disability-data question that belongs to the PIA, not to a template |
| n | Field-event flights | A flight is a subdivision of a field event's entrants | **Have** | Structurally identical to a `Race`/heat |
| o | Relays | 4×100 m, house teams, double points | **Have** | ADR-0008 maps across unchanged |

---

## 4. What comparable products highlight — and what they get wrong

**What they highlight** (union across AthleticsCarnival, Houseplay, ResultsHQ, Sports Tracker,
Hy-Tek):

- Live house leaderboard, house-coloured, with a projector mode.
- **Age/grade champion computed continuously** — Houseplay markets "the current champion boy or
  girl for each grade" as a headline; AthleticsCarnival lists "age group champions by year
  level". This is the athletics feature schools buy on.
- **Record broken → celebrate immediately** ("automatically tracks and celebrates").
- **Schedule health**: AthleticsCarnival shows whether the carnival is "ahead, on time or behind
  schedule" and ticks events off as they finalise. Nobody else surfaces this; it is the best
  idea in the set.
- **Ribbon handout tracking** — per-student 1st/2nd/3rd collection lists.
- Field events as first-class: three attempts with foul detection, a bonus final for the top N,
  implement weights for shot/discus/javelin/hammer, bar heights and clearances for high jump.
- Novelty points awarded straight to a house (the ADR-0005 shape, independently arrived at).
- Exports with a **"privacy name or full name"** toggle.
- Hy-Tek: a full field series (up to 6 horizontal attempts, up to 40 heights for vertical), wind
  per jump, "Calc Best" deriving the best mark rather than trusting a typed one.

**What they get wrong:**

| Mistake | Why it matters here |
|---|---|
| Spectacle before capture — the billboard is the product, the deck is an afterthought | The platform has no billboard by design. The deck is where a carnival is won or lost |
| Champion and record announced from unverified data with no provisional marking | Ours must badge provisional (the standings already do) and must not "celebrate" a mark that a correction will withdraw |
| House colour as the *only* categorical encoding | Colour-vision and greyscale-print failures; house colour is optional and always beside the house name (ADR-0044) |
| "Privacy name" as an export checkbox rather than a publishing boundary | ADR-0004 tier 2 defines the boundary once, per report. A per-export toggle is a setting someone forgets |
| Gender printed per athlete in exports and on rosters | Straight breach of ADR-0026 §2. Division label only |
| "Best **or average** of attempts, your choice" (Houseplay) | Average is not an athletics rule. Offering it invents a competition format and corrupts records |
| Paper as the offline story; single-machine or online-only architecture | Already the platform's differentiator (ADR-0002/0025/0029) |
| Nothing in the AU school set does field-event countback properly | Countback is where a hand-run carnival actually produces disputes. Doing (c) correctly is a genuine differentiator |

Also checked: **Compass and Schoolbox have no sport-carnival module** — the Compass↔Schoolbox
integration is SIS/LMS only. For a school on those systems, there is no incumbent to integrate with or displace.

---

## 5. Recommended for the dashboard ADR — six, and no more

1. **House standings with the three components visible.** Already built; it is the anchor and it
   stays the largest thing on the screen.
2. **Program progress against the clock** — share of expected results captured, plus ahead/on
   time/behind versus the schedule's slot start times.
3. **Event completion spine** — per stream/slot: not started / in progress / complete, with the
   outstanding count, tappable through to the deck.
4. **Unsynced capture** — queued items and undrained devices, stated plainly, so nobody reads the
   board as final while a field station is still offline.
5. **Age champion race per division** — top three on champion points, provisional-badged,
   division label only, never a per-student category.
6. **Records set today** — provisional badge against the standing book, new and equalled
   distinguished.

**Not for the pilot** (each defensible later, none needed to run a carnival):

- Personal bests and "biggest improver" — no data in year 1, and cross-year per-student history
  is a separate privacy decision.
- Wind readings and wind-legality — no gauge exists.
- Multi-event/pentathlon totals — not a typical school program.
- Medal/ribbon tally as a dashboard headline — put it in the book and the staff handout report.
- Any negative-singling stat: DNF/DQ boards, "did not compete" lists, slowest-time boards.
- Big-screen billboard and motion. House colour exists only as an optional, dashboard-only
  addition beside the house name (ADR-0044).
- Per-student participation tables on the venue display; house/year-level rates only.
- Live district-selection projections — selection is a staff judgement, published after the day.

---

## 6. Sources

All accessed **2026-08-31**. Items marked *(search summary)* could not be parsed directly by the
fetcher (SSV serves its specifications as binary PDFs) and are recorded from the search result's
own extract — re-verify before citing in an ADR.

- School Sport Victoria, Track and Field (secondary landing, age groups):
  https://www.ssv.vic.edu.au/sports/track-and-field
- SSV, Track and Field Secondary Competition Rules (age as at 31 Dec; max 5 events excluding
  relays; hurdles removed from secondary; three rounds in all field events except high jump;
  field countback on second- then third-best trials; bar not lowered for a returning athlete;
  relay team composition; 10 points for an individual first, 20 for a relay first):
  https://www.ssv.vic.edu.au/resources/track-and-field-secondary-competition-rules
- SSV, Track and Field Technical Specifications (Primary and Secondary) — implement weights,
  hurdle specs *(binary PDF; not parsed)*:
  https://assets-au-01.kc-usercontent.com/3dd1ec17-9445-025e-c552-0f8615d28923/467f5d64-58ac-4ef3-b85a-fe6f8467fd49/TrackandFieldTechnicalSpecificationsPrimaryandSecondary%20%283%29.pdf
- SSV, General Conditions of Competition 2026 *(search summary — age as at 31 Dec)*:
  https://www.ssv.vic.edu.au/Downloads%20Library/General%20Conditions%20of%20Competition.pdf
- SSV, State Secondary Track & Field records/results *(search summary — record rows carry event
  with implement mass, e.g. "Discus (1.0kg)", plus holder, school, year)*:
  https://www.ssv.vic.edu.au/state/Documents/StateSecondaryTrackandFieldResults2025.pdf
- Little Athletics Australia, Throwing Implements Specifications *(search summary — per-age,
  per-category masses)*:
  https://www.littleathletics.com.au/wp-content/uploads/2023/09/Throwing-Implements-Specifications-2023-2024.pdf
- Little Athletics Victoria, Throwing Implements Specifications:
  https://lavic.com.au/wp-content/uploads/2020/06/2020-21-Throwing-Implements-Specifications.pdf
- Beecroft Zone PSSA, Athletics (8/6/4/2 place points for track finals, field and relays; age
  champion on total points; field countback on next-best; high jump on fewest failures):
  https://sites.google.com/education.nsw.gov.au/beecroftzonepssa/carnivals/athletics
- Annandale State School, Athletics Guidelines *(search summary — age champion on highest
  individual points; tied students both awarded)*:
  https://annandaless.eq.edu.au/SupportAndResources/FormsAndDocuments/Documents/Athletics%20Guidelines%20Annandale%20Parent.pdf
- AthleticsCarnival (AU iPad app) — App Store listing: real-time house leaderboard, age-group
  champions by year level, projector mode, records celebrated, three-attempt field tracking with
  foul detection, bonus final for top N, implement weights, high-jump bar heights, ribbon
  distribution, ahead/on-time/behind schedule, CSV/PDF export with privacy-name option:
  https://apps.apple.com/au/app/athleticscarnival/id6761138246
- Houseplay (AU) — live billboard, champion boy/girl per grade, bonus points, records, multi-
  attempt events with "best or average" selection:
  https://houseplay.com.au/
- Sports Tracker (AU) — live event scoring by event code; standards or place-based meets:
  https://sportstrackerapp.com/
- ResultsHQ / Timing Solutions — house points and age champion carnival setup *(HTTP 403 to the
  fetcher; referenced from search results only)*:
  https://support.timingsolutions.com.au/hc/en-us/articles/900007134443-ResultsHQ-House-Points-Age-Champion-Carnival-Setup
- Hy-Tek Track & Field Meet Manager 6, Field Series — up to 6 horizontal attempts, up to 40
  heights per flight for vertical events, wind per jump, "Calc Best":
  https://hytek.active.com/User_Guides_HTML/TFMM6/fieldseriesctrl_fselection.htm
- Run Lap Tap, "How to Organise an Athletics Carnival in 2026" — concurrent track/field
  scheduling, three attempts, best mark:
  https://www.runlaptap.app/blog/organise-athletics-carnival-guide
- NCAA, "Wind and scoring in track and field, explained" — 2.0 m/s legality threshold; over-wind
  marks still score but cannot ratify:
  https://www.ncaa.com/news/trackfield-outdoor-women/article/2024-06-25/wind-and-scoring-track-and-field-explained
- Olympics.com, high jump rules — elimination on three consecutive failures; tie-break on fewest
  failures at the height, then fewest overall, then jump-off:
  https://www.olympics.com/en/news/high-jump-rules-regulations-athletics-sports
- Schoolbox/Compass integration announcement (evidence that neither carries a carnival module):
  https://schoolbox.education/company-news/schoolbox-compass-integration/
- PB Hero / The PE Geek — personal-best tracking framed as on-device-only to sidestep consent;
  useful as the privacy framing for any future PB feature:
  https://thepegeek.com/2026/04/bhero-free-personal-best-tracker-for-young-athletes-the-pe-geek/
