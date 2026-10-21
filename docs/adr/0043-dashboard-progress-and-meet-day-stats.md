# ADR-0043: The Dashboard — progress, standings and meet-day stats

- **Status:** Accepted (2026-08)

## Context

The Dashboard showed standings and a print-program link (ADR-0024) and nothing about how far the day had got. Organisers need to know, on one screen, who is winning, how much is still to record, whether any station has undelivered captures, and whether any record is in play, without the board ever being read as final while a device is offline. Research into athletics carnival stats recommended a small, fixed set and warned against anything that singles students out.

## Decision

1. **A small, fixed set, in order of prominence** ("Standings first" layout):
   - **House standings**, the anchor, with a shared rank for tied houses (below).
   - **Program progress**: results recorded of results expected, events complete, and a "still to record" list of events, each linking to its deck.
   - **Records today**: each eligible division's best mark that beats or equals the standing record, labelled *candidate* (records are minted only at finalisation, ADR-0028). Nothing shows against an empty category.
   - **Participation by house**: competitors on the roster, students entered, and how many of those have a result. Counts only.
   - **Unsynced capture**: devices with undelivered items, stated plainly, for callers who may manage the carnival.

   Nothing negative-singling (DNF/DQ boards, slowest, "did not compete") and no per-student category appears here (ADR-0026).
2. **One server aggregate** feeds progress, participation and records today: `GET /api/v1/carnivals/{id}/progress` (`carnivals:view`, read-only, on the service-worker read allowlist so it works offline). The client overlays its own queued captures and entries, so offline work counts as progress on the device that holds it, as on the event tiles.
3. **Read-mostly stays.** Every element links to an existing route; nothing is captured or edited on the Dashboard. The devices row uses the existing device-status endpoints and their authorisation (ADR-0034); a failed read says so rather than going silent.
4. **House colours appear here, and only here**, per [ADR-0044](0044-configurable-house-colours.md): a swatch beside the name and the bar fill, with the name always present.
5. **Tied houses share the rank.** Standard competition ranking (1, 1, 3, shown as `=1`), computed server-side in the standings so the Dashboard and the printed reports cannot disagree. Who takes a tied shield is the school's decision, made off the platform.
6. **Not built:** age-group leaders and champion points, progress against the clock (ahead or behind the slot times), personal bests, medal tallies, a billboard route.

How the aggregate is computed: [scoring § dashboard progress](../architecture/scoring.md#dashboard-progress).

## Consequences

- One read endpoint and one screen carry the meet-day view; standings and progress refresh when the device's outbox drains.
- "Records today" is honest to the minting rule but empty on a school's first carnival, because the book seeds at the first finalisation.
- Age champions remain a separate decision with its own configuration (a champion-point table per carnival).

## Rejected alternatives

- **Progress on the deck instead:** the deck already carries per-division and per-heat progress; the carnival-wide view belongs beside standings.
- **Per-student stats on the board:** privacy and the negative-singling rule; the results book is their place.
- **Client-side aggregation from the events list:** the schedule order and participation need data the client does not hold; one server read is cheaper and consistent.
- **A meet-day board with the completion spine as the main column, or six equal stat tiles:** denser or flatter than an organiser needs on the day; the spine is the natural revisit for athletics.
