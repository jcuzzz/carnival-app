import { describe, expect, it } from "vitest";
import type { CarnivalEvent, ScheduleSlot } from "../../api/types";
import {
  moveWithin,
  outOfOrderSlotIds,
  slotEventOrder,
  slotRequest,
  timeInputValue,
  toWireTime,
  unscheduledEvents,
} from "./builder";

/**
 * The organiser's ordering rules (ADR-0014 §2). Re-ordering is up/down controls
 * — drag-and-drop is an explicit ADR non-goal — so the interesting cases are the
 * ends of the list and an order column that has drifted out of step with what is
 * on screen.
 */

const items = [
  { id: "a", sortOrder: 0 },
  { id: "b", sortOrder: 1 },
  { id: "c", sortOrder: 2 },
];

describe("moveWithin", () => {
  it("swaps an item with its neighbour, writing only the two that moved", () => {
    expect(moveWithin(items, "b", -1)).toEqual([
      { id: "b", sortOrder: 0 },
      { id: "a", sortOrder: 1 },
    ]);
    expect(moveWithin(items, "b", 1)).toEqual([
      { id: "c", sortOrder: 1 },
      { id: "b", sortOrder: 2 },
    ]);
  });

  it("does nothing at either end of the list", () => {
    // The buttons are disabled there, but a disabled button is a courtesy —
    // this is the guarantee, and it must be a no-op rather than a wrapped move.
    expect(moveWithin(items, "a", -1)).toEqual([]);
    expect(moveWithin(items, "c", 1)).toEqual([]);
  });

  it("does nothing for a single item, or for an id that is not in the list", () => {
    expect(moveWithin([{ id: "only", sortOrder: 0 }], "only", -1)).toEqual([]);
    expect(moveWithin([{ id: "only", sortOrder: 0 }], "only", 1)).toEqual([]);
    expect(moveWithin(items, "gone", -1)).toEqual([]);
  });

  it("normalises orders that have drifted, without scrambling the list", () => {
    // Template-seeded slots that were later hand-edited: the stored orders are
    // neither contiguous nor zero-based, but the display order is what the
    // organiser is looking at, so the move is expressed as POSITIONS.
    const drifted = [
      { id: "a", sortOrder: 5 },
      { id: "b", sortOrder: 9 },
      { id: "c", sortOrder: 40 },
    ];
    expect(moveWithin(drifted, "c", -1)).toEqual([
      { id: "a", sortOrder: 0 },
      { id: "c", sortOrder: 1 },
      { id: "b", sortOrder: 2 },
    ]);
  });

  it("leaves duplicate orders resolved rather than ambiguous", () => {
    // Every slot at 0 is a tie the server breaks by start time; one move settles
    // the whole stream into an explicit order instead of preserving the tie.
    const tied = [
      { id: "a", sortOrder: 0 },
      { id: "b", sortOrder: 0 },
    ];
    expect(moveWithin(tied, "b", -1)).toEqual([{ id: "a", sortOrder: 1 }]);
  });
});

describe("slotEventOrder", () => {
  /** Events as the block lists them: the stored order is what matters here. */
  const listed = (...rows: [string, number | null][]) =>
    rows.map(([id, slotOrder]) => ({ id, slotOrder }) as CarnivalEvent);

  it("carries the stored order, so a block re-orders exactly like a stream", () => {
    const block = listed(["free-13", 0], ["free-14", 1], ["free-15", 2]);
    expect(moveWithin(slotEventOrder(block), "free-15", -1)).toEqual([
      { id: "free-15", sortOrder: 1 },
      { id: "free-14", sortOrder: 2 },
    ]);
  });

  it("repairs a block whose order column has drifted, instead of creating a tie", () => {
    // Reachable entirely through the builder's own buttons: a block held P, Q,
    // A; the organiser unscheduled P and Q and placed B and C, which the server
    // APPENDS at max+1. Stored is A=2, B=3, C=4 while the screen shows A, B, C.
    // Reading the order off the screen would make A look already-correct
    // (index 0 === position 0) and write only C=1, B=2 — leaving A and B both
    // at 2 for OrderBy(SlotOrder).ThenBy(EventId) to break by GUID, and C at
    // the top of a block the organiser moved by one place.
    const drifted = listed(["a", 2], ["b", 3], ["c", 4]);
    expect(moveWithin(slotEventOrder(drifted), "c", -1)).toEqual([
      { id: "a", sortOrder: 0 },
      { id: "c", sortOrder: 1 },
      { id: "b", sortOrder: 2 },
    ]);
  });

  it("always rewrites an event carrying no order rather than assuming its position", () => {
    // A listed event always has one from the server, so this is a
    // half-refreshed cache — and guessing right by accident is worse than the
    // extra write, because the guess is what leaves two events tied.
    expect(moveWithin(slotEventOrder(listed(["a", 0], ["b", null])), "b", -1)).toEqual([
      { id: "b", sortOrder: 0 },
      { id: "a", sortOrder: 1 },
    ]);
  });
});

describe("unscheduledEvents", () => {
  const event = (id: string, scheduleSlotId: string | null) =>
    ({ id, scheduleSlotId }) as CarnivalEvent;

  it("is the organiser's worklist: everything in no slot at all", () => {
    const events = [event("a", "slot-1"), event("b", null), event("c", null)];
    expect(unscheduledEvents(events).map((e) => e.id)).toEqual(["b", "c"]);
  });

  it("is empty for a fully placed carnival, and for a carnival with no events", () => {
    expect(unscheduledEvents([event("a", "slot-1")])).toEqual([]);
    expect(unscheduledEvents([])).toEqual([]);
  });
});

describe("slot times", () => {
  it("round-trips the server's TimeOnly through a time input unchanged", () => {
    expect(timeInputValue("09:45:00")).toBe("09:45");
    expect(toWireTime(timeInputValue("09:45:00"))).toBe("09:45:00");
  });

  it("treats an empty field as no time, not as midnight", () => {
    expect(timeInputValue(null)).toBe("");
    expect(toWireTime("")).toBeNull();
    expect(toWireTime("   ")).toBeNull();
  });

  it("drops seconds a browser may add, so the value stays a clock time", () => {
    expect(toWireTime("10:00:30")).toBe("10:00:00");
  });
});

describe("outOfOrderSlotIds", () => {
  /** Slots as the stream lists them — only the id and the start time matter. */
  const timed = (...rows: [string, string | null][]) =>
    rows.map(([id, startTime]) => ({ id, startTime }));

  it("marks a slot whose start time is earlier than the one above it", () => {
    // The typo this exists for: 09:45 entered as 19:45, or one up-tap too many.
    const stream = timed(["a", "09:45:00"], ["b", "14:30:00"], ["c", "10:15:00"]);
    expect([...outOfOrderSlotIds(stream)]).toEqual(["c"]);
  });

  it("marks the discontinuity only, not everything after it", () => {
    // Comparing against the previous TIMED slot rather than the earliest so far:
    // one misplaced slot is one mark, so the mark still points at the mistake.
    const stream = timed(["a", "14:30:00"], ["b", "09:45:00"], ["c", "10:15:00"]);
    expect([...outOfOrderSlotIds(stream)]).toEqual(["b"]);
  });

  it("leaves a stream that runs forwards, or ties, entirely unmarked", () => {
    // Equal times are real: four events at once in the pool (ADR-0014 §4).
    expect(outOfOrderSlotIds(timed(["a", "09:45:00"], ["b", "09:45:00"])).size).toBe(0);
    expect(outOfOrderSlotIds(timed(["a", "09:45:00"], ["b", "10:00:00"])).size).toBe(0);
  });

  it("steps over untimed slots instead of letting them break the run", () => {
    // An announcement with no clock time sits between two timed blocks; it is
    // neither out of order itself nor a reason to stop comparing across it.
    const stream = timed(["a", "09:45:00"], ["gap", null], ["c", "10:15:00"], ["d", "09:00:00"]);
    expect([...outOfOrderSlotIds(stream)]).toEqual(["d"]);
  });

  it("marks nothing in a stream with no times at all", () => {
    expect(outOfOrderSlotIds(timed(["a", null], ["b", null])).size).toBe(0);
    expect(outOfOrderSlotIds([]).size).toBe(0);
  });
});

describe("slotRequest", () => {
  const slot: ScheduleSlot = {
    id: "slot-1",
    streamId: "pool",
    sortOrder: 3,
    startTime: "09:45:00",
    endTime: null,
    kind: "Events",
    label: "FREESTYLE EVENTS",
    adjustmentCategory: null,
    eventIds: ["free-13"],
  };

  it("restates the whole slot, because the server's PUT replaces it", () => {
    // The failure this prevents: retiming a block with {startTime} alone would
    // clear its label — the line the program prints.
    expect(slotRequest(slot, { sortOrder: 0 })).toEqual({
      startTime: "09:45:00",
      endTime: null,
      kind: "Events",
      label: "FREESTYLE EVENTS",
      adjustmentCategory: null,
      sortOrder: 0,
    });
  });

  it("carries no event ids — placement is its own endpoint", () => {
    expect(slotRequest(slot)).not.toHaveProperty("eventIds");
  });
});
