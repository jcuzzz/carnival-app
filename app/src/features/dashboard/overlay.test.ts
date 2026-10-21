import { describe, expect, it } from "vitest";
import { COMPLETE, IN_PROGRESS, overlayQueued, stillToRecord } from "./overlay";
import type { CarnivalProgress, EventProgress } from "../../api/types";
import type { QueuedProgress } from "../events/progress";

/**
 * The overlay must agree with `sumProgress` on the same carnival: the Dashboard
 * and the event tiles are two readouts of one number, and a device holding
 * captures is the case they are both built for (ADR-0014 §1, ADR-0043 §2).
 */

const event = (over: Partial<EventProgress> & { carnivalEventId: string }): EventProgress => ({
  definitionName: "Freestyle 50m",
  divisionLabel: "13 Boys",
  slotId: null,
  slotLabel: null,
  stream: null,
  startTime: null,
  sortOrder: 0,
  expected: 0,
  recorded: 0,
  status: "notStarted",
  ...over,
});

function progressOf(...events: EventProgress[]): CarnivalProgress {
  return {
    carnivalId: "c-1",
    provisional: true,
    events,
    participation: [],
    recordsToday: [],
    totals: {
      events: events.length,
      eventsComplete: events.filter((e) => e.status === "complete").length,
      eventsInProgress: events.filter((e) => e.status === "inProgress").length,
      eventsNotStarted: events.filter((e) => e.status === "notStarted").length,
      expected: events.reduce((sum, e) => sum + e.expected, 0),
      recorded: events.reduce((sum, e) => sum + e.recorded, 0),
    },
  };
}

const held = (entries: number, results: number): Map<string, QueuedProgress> =>
  new Map([["ev-1", { entries, results }]]);

describe("overlayQueued", () => {
  it("counts an offline add in the denominator as well as its result", () => {
    const overlaid = overlayQueued(
      progressOf(event({ carnivalEventId: "ev-1", expected: 3, recorded: 1, status: IN_PROGRESS })),
      held(1, 1),
    );
    expect(overlaid.events[0]).toMatchObject({ expected: 4, recorded: 2, status: IN_PROGRESS });
    expect(overlaid.totals).toMatchObject({ expected: 4, recorded: 2, eventsInProgress: 1 });
  });

  it("reopens a complete event that an offline add has left owing a time", () => {
    const overlaid = overlayQueued(
      progressOf(event({ carnivalEventId: "ev-1", expected: 4, recorded: 4, status: COMPLETE })),
      held(1, 0),
    );
    expect(overlaid.events[0]).toMatchObject({ expected: 5, recorded: 4, status: IN_PROGRESS });
    expect(overlaid.totals.eventsComplete).toBe(0);
    expect(stillToRecord(overlaid.events)).toHaveLength(1);
  });

  it("leaves a Social event complete — its entries owe no time (ADR-0014 §4)", () => {
    const overlaid = overlayQueued(
      progressOf(event({ carnivalEventId: "ev-1", expected: 3, recorded: 0, status: COMPLETE })),
      held(1, 0),
    );
    expect(overlaid.events[0]).toMatchObject({ expected: 4, recorded: 0, status: COMPLETE });
    expect(stillToRecord(overlaid.events)).toHaveLength(0);
  });

  it("never records more than it expects, and leaves an untouched event alone", () => {
    const untouched = event({ carnivalEventId: "ev-2", expected: 2, recorded: 0 });
    const overlaid = overlayQueued(
      progressOf(
        event({ carnivalEventId: "ev-1", expected: 2, recorded: 1, status: IN_PROGRESS }),
        untouched,
      ),
      held(0, 5),
    );
    expect(overlaid.events[0]).toMatchObject({ expected: 2, recorded: 2, status: COMPLETE });
    expect(overlaid.events[1]).toBe(untouched);
  });
});
