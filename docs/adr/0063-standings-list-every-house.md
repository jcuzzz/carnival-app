# ADR-0063: Standings list every house

- **Status:** Accepted (2026-10)

## Context

House standings listed only houses with a placed result, a non-zero participation total or a live adjustment, while the participation card beside them listed every house. Early in the day the two cards disagreed, and a house that had not scored yet was simply missing from the leaderboard.

## Decision

Standings list **every configured house** from the start of the carnival, at 0 until it scores. Ordering and shared ranks are unchanged: houses level on points, including all at 0, share a rank. The participation card lists the same configured houses, and the standings CSV and the results book's standings follow the same list. How standings are computed: [scoring § standings](../architecture/scoring.md#standings).

## Consequences

- The leaderboard and the participation card always show the same houses.
- A house with no entries at all still appears, at 0, which is what an organiser expects on the day.
- A house removed from the school configuration after it scored still appears in that carnival's standings while it has scoring rows; history is not rewritten.
- A finalised carnival lists the houses that took part (an entry, relay team or adjustment in it) plus any with scoring rows, so houses configured later do not appear in its final standings or participation card.
