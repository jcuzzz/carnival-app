// @vitest-environment jsdom
import { cleanup, render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import type { CarnivalEvent } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import { EventTiles } from "./EventTiles";
import { queuedProgressByEvent } from "./progress";

/**
 * The carnival landing grid (ADR-0014 §3): assigned events first, everything
 * else below — an ORDERING, never a filter. Nothing here is allowed to make an
 * event unreachable; the server is the only thing that refuses a submission
 * (ADR-0009).
 *
 * Every tile is a plain link into a division (ADR-0018 §2, owner refinement
 * 2026-08-14), so carnival → tile → entry is two taps.
 */

const olivia = { subjectId: "olivia", subjectDisplayName: "Olivia Official" };

function event(over: Partial<CarnivalEvent> & Pick<CarnivalEvent, "id">): CarnivalEvent {
  return {
    carnivalId: "c-1",
    eventDefinitionId: "def-free",
    definitionCode: "SW-50-FREE",
    definitionName: "Freestyle 50m",
    ageGroup: "13",
    category: "F",
    tier: "Competitive",
    scoringMode: null,
    effectiveScoringMode: "Places",
    scoresPoints: true,
    scheduleSlotId: null,
    slotOrder: null,
    status: "Open",
    version: 1,
    entryCount: 0,
    resultCount: 0,
    officials: [],
    ...over,
  } as CarnivalEvent;
}

const events = [
  event({ id: "free-13", entryCount: 6, resultCount: 4, officials: [olivia] }),
  event({ id: "free-14", ageGroup: "14", entryCount: 4, resultCount: 0 }),
  event({
    id: "back-13",
    eventDefinitionId: "def-back",
    definitionCode: "SW-50-BACK",
    definitionName: "Backstroke 50m",
    entryCount: 5,
    resultCount: 5,
  }),
];

beforeEach(cleanup);

describe("EventTiles", () => {
  it("puts the official's assigned events first and still lists everything below", () => {
    render(<EventTiles events={events} subject="olivia" />);

    const mine = within(screen.getByRole("region", { name: "My events" }));
    const all = within(screen.getByRole("region", { name: "All events" }));

    // One tile in the top block: the definition the official is assigned on.
    expect(mine.getAllByRole("link")).toHaveLength(1);
    expect(mine.getByRole("link", { name: /Freestyle 50m/ })).toBeTruthy();
    // …and every definition, including that one, is still in the grid below.
    expect(all.getAllByRole("link")).toHaveLength(2);
    expect(all.getByRole("link", { name: /Backstroke 50m/ })).toBeTruthy();
  });

  it("says on the tile that a 'My events' fraction counts the official's own divisions", () => {
    // The same definition appears twice on this screen with two different, both
    // correct, fractions: "My events" counts the one division Olivia is
    // assigned to (4/6), "All events" counts both (4/10). Only the label tells
    // a reader which question the number answers.
    render(<EventTiles events={events} subject="olivia" />);

    const mine = within(screen.getByRole("region", { name: "My events" })).getByRole("link", {
      name: /Freestyle 50m/,
    });
    const all = within(screen.getByRole("region", { name: "All events" })).getByRole("link", {
      name: /Freestyle 50m/,
    });

    expect(mine.textContent).toContain("4/6");
    expect(mine.textContent).toContain("your divisions");
    expect(all.textContent).toContain("4/10");
    expect(all.textContent).not.toContain("your divisions");
  });

  it("shows no block, and no empty state, for an official with no assignments", () => {
    render(<EventTiles events={events} subject="nobody" />);
    expect(screen.queryByRole("region", { name: "My events" })).toBeNull();
    expect(screen.getByRole("region", { name: "Events" })).toBeTruthy();
  });

  it("summarises each definition's divisions and results-entered progress", () => {
    render(<EventTiles events={events} subject={null} />);

    const freestyle = screen.getByRole("link", { name: /Freestyle 50m/ });
    expect(freestyle.textContent).toContain("2 divisions");
    // 4 of 10 entries across the two divisions carry a result.
    expect(freestyle.textContent).toContain("4/10");
    // One division only → the tile names it rather than counting to one.
    expect(screen.getByRole("link", { name: /Backstroke 50m/ }).textContent).toContain(
      "13 · Girls",
    );
  });

  it("links a tile to a real event route, so a tap is an ordinary navigation", () => {
    render(<EventTiles events={events} subject="olivia" />);
    // "All events" lands on the definition's first division in program order.
    expect(
      within(screen.getByRole("region", { name: "All events" }))
        .getByRole("link", { name: /Freestyle 50m/ })
        .getAttribute("href"),
    ).toBe("#/carnivals/c-1/events/free-13");
  });

  it("lands a 'My events' tile on the official's own division, not on program order", () => {
    // The block holds only their divisions, so the tile cannot open somebody
    // else's — which is the wrong-division capture ADR-0014 §5 guards against.
    const assignedSecond = events.map((e) =>
      e.id === "free-14" ? { ...e, officials: [olivia] } : { ...e, officials: [] },
    );
    render(<EventTiles events={assignedSecond} subject="olivia" />);
    expect(
      within(screen.getByRole("region", { name: "My events" }))
        .getByRole("link", { name: /Freestyle 50m/ })
        .getAttribute("href"),
    ).toBe("#/carnivals/c-1/events/free-14");
  });

  it("badges a Social division so the relaxed tier is legible from the grid", () => {
    render(
      <EventTiles
        events={[event({ id: "social", tier: "Social", effectiveScoringMode: "Social" })]}
        subject={null}
      />,
    );
    expect(screen.getByText("Social")).toBeTruthy();
  });

  it("does not badge a tile Social because its Social twin is listed first", () => {
    // Program order picks the tile's target instance, so a badge taken from it
    // describes one division out of five. The selector carries per-division truth.
    const twin = event({
      id: "free-15",
      ageGroup: "15",
      tier: "Social",
      effectiveScoringMode: "Social",
    });
    render(<EventTiles events={[twin, event({ id: "free-13" })]} subject={null} />);
    expect(screen.queryByText("Social")).toBeNull();
  });

  it("counts a mixed tile's Social entries out of the results denominator", () => {
    // 40 timed swims all recorded + an 8-entry Social twin that owes no times:
    // "40/48" would keep the end-of-day hunt chasing eight swims forever.
    render(
      <EventTiles
        events={[
          event({ id: "free-13", entryCount: 40, resultCount: 40 }),
          event({
            id: "free-15",
            ageGroup: "15",
            tier: "Social",
            effectiveScoringMode: "Social",
            entryCount: 8,
          }),
        ]}
        subject={null}
      />,
    );
    expect(screen.getByRole("link", { name: /Freestyle 50m/ }).textContent).toContain("40/40");
  });

  it("counts captures still in the outbox, so the grid is honest with no uplink", () => {
    // The pool-day case: uplink down since 09:00, so every `resultCount` on the
    // list is a morning number. Four times captured into the outbox since.
    const queued = queuedProgressByEvent(
      ["en-1", "en-2", "en-3", "en-4"].map(
        (entryId, i) =>
          ({
            id: `item-${i}`,
            kind: "result.submit",
            payload: { carnivalEventId: "free-14", entryId },
            actorSubject: "olivia",
            capturedAt: "2026-08-07T04:00:00Z",
            status: "queued",
            attempts: 0,
          }) satisfies OutboxItem,
      ),
    );

    render(<EventTiles events={events} subject={null} queued={queued} />);
    // 4 recorded on 13·F per the server + 4 queued on 14·F, over 10 entries.
    expect(screen.getByRole("link", { name: /Freestyle 50m/ }).textContent).toContain("8/10");
  });
});
