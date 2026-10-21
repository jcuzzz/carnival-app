# ADR-0042: The condensed deck header — three pickers, entries above the fold

- **Status:** Accepted (2026-08). Builds on [ADR-0041](0041-deck-direction-a.md).

## Context

On the deck, about 350px of header (back link, title, meta line, gender row, age chip row, heat chip row, helper text, an "Entries" heading and a summary card) preceded the first swimmer. Officials need to see entries quickly, on a tablet and on a phone. Four layouts were compared: the existing stack, three tight rows, one scrolling selector row, and a single combined division picker; the chosen shape splits the selection into one picker per decision.

## Decision

1. **The title row absorbs the chrome.** Back-to-carnival is a 44px arrow button on the title line (icon-only, a named exception to the icon rule, named "Back to carnival"). The event name, Open badge and the "n of m recorded" progress line share that row; freshness ("updated hh:mm") sits beside it. The separate meta line, "Entries" heading and summary card are gone, and with them the program slot and start time: "Next event →" and the Program screen carry program position.
2. **One selector row of pickers**, each a Base UI menu: **category** (Boys, Girls, Open), **age** (each item carrying its division's `n/m`) and **heat** (each item carrying `n/m` and a completion tick, with **"+ New heat" as the last item**; its refusal reason shows as the item's disabled text). A **tier** picker appears only on a definition that has both tiers. An axis with a single value renders as a static label, and the per-division count, tick and `aria-current` land on the finest axis that offers a choice. Every item is a real link, so each tap changes the page (ADR-0018). Non-Time events show no heat picker ([ADR-0053](0053-field-attempts.md)).
3. **One picker open at a time**; selection semantics are unchanged underneath: the same division routes (ADR-0022) and the same `?heat=` URL state and heat ordinals (ADR-0039).
4. **Helper text is conditional**, shown only when its condition holds.
5. **Target:** about 110px from the nav to the first swimmer, all targets at least 44px, tokens only.

## Consequences

- About 240px returned to the entries; roughly 128px nav-to-first-swimmer at tablet width. At 390px the pickers wrap and the figure is about 190px.
- Heat switching becomes two taps; division switching stays two.
- The always-visible per-heat progress of the old chip row moves inside the heat picker; the current heat's count stays on the closed button. Revisit if marshals miss the at-a-glance sibling-heat state.
- The deck no longer shows the event's program slot or start time.

## Rejected alternatives

- **Three tight rows:** every selector visible, but about 150px and worse wrapping on a phone.
- **One scrolling selector row:** heats can sit off-screen exactly when a marshal needs them.
- **A single combined division picker:** switching age re-asks gender; one control per decision is clearer.
- **Heat chips beside the pickers:** does not fit on a phone.
