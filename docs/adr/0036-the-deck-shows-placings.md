# ADR-0036: The deck shows placings

- **Status:** Accepted (2026-09)

## Context

A recorder who has just typed the last time of a heat is looking at the one screen at the venue that knows the finishing order, and it said nothing. Places were rendered only in the results book, which is administrator-only, while the person calling out ribbons stands at the deck.

The gap was in the data first: the deck's results read carried no place. Ranking is server-side by design, in one implementation ([scoring](../architecture/scoring.md#placement-within-a-field)); a client sorting measured values itself would disagree with the board exactly where it matters: ties, non-finishes, social events, countback and measure direction. For a Time event the heat is the placing unit ([ADR-0015](0015-heats-races-and-lanes.md)).

## Decision

1. **The results read carries the place.** `ResultDto` has a nullable `place`, computed by the same ranking that feeds house points and the results book (`HeatPlacings`), so the three cannot disagree. A Time event ranks per heat; any other measure ranks across the whole division. `place` is null where the book prints no place: a Social-mode division, or a result that is not a ranked finish (DNS, DNF, DQ, NM). An event excluded from house points is still placed. No new route or permission.
2. **Every placed row shows its ordinal** ("1st", "2nd", "3rd", "4th"…) as a badge on the row's identity line, with 1st visually distinct so the winner is scannable down a heat. Rows with no place show nothing.
3. **The badge shows only the server's ranking of the server's value.** A just-typed time claims no place until it has drained and the server agrees. The badge is hidden while the row's outbox item is the thing on display (a queued or parked capture or correction), while the row is being edited, and on an entry with no heat in a division that has races (those rows sit under "not in a race", where a "1st" would contradict the row). In a division with no races the heatless pool is the field, and it badges normally.
4. **The badge is live and provisional.** Ordinals reshuffle while a heat's times land; no "heat complete" gate holds them back. The deck re-reads its results (with entries, races and the division) every 60 seconds while the server is reachable, so a ribbons tablet sees other devices' captures; polling is off while unreachable, because the service worker would answer from cache and make the connectivity strip read "connected".

## Consequences

- A row can read "1st" and later "2nd" when a faster time or a correction lands. The badge asserts the current ranking, never an award.
- The badge has no reserved width, so it can shift the identity line when it appears; most rows of a finished heat carry one.
- Places now ride a `carnivals:view` read. They were derivable from the measured values on the same read, so this is not a material widening: the reason for the server-side place is agreement with the book, not secrecy.
- Offline, a cached results body can show a place behind a freshness stamp that looks current. The connectivity strip carries the uplink state; the badge does not restate it.
- The deck's periodic re-read is an exception to the app's otherwise event-driven reads ([ADR-0012](0012-client-data-layer.md)): the event that changes the answer happens on another device.

## Rejected alternatives

- **Client-side ranking** of the values already on the read. Would re-implement ties, social gates, countback and status rules; the first divergence puts a wrong ribbon on a child.
- **Reading the results-book report**, which already carries places. Administrator-only, and the whole carnival where the deck wants one event.
- **First place only.** Same data; showing all placings costs nothing more.
- **Places only once the heat is complete.** Reintroduces per-event completion machinery.
- **A podium panel per heat.** More surface than needed; the row is where the eye already is.
