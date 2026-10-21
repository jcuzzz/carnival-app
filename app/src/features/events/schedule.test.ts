import { describe, expect, it } from "vitest";
import type { Schedule } from "../../api/types";
import { nextScheduledEventId } from "./schedule";

/**
 * "Next event →" (ADR-0014 §3) resolves from the cached program: next in this
 * slot, else the first event of the next slot in the SAME stream. It is a
 * suggestion — the interesting assertions are the ones where it declines to
 * suggest anything, because none of those states may ever read as an error.
 */

const schedule: Schedule = {
  carnivalId: "c-1",
  streams: [
    {
      id: "pool",
      name: "Pool",
      sortOrder: 0,
      slots: [
        {
          id: "slot-free",
          streamId: "pool",
          sortOrder: 0,
          startTime: "09:45:00",
          endTime: null,
          kind: "Events",
          label: "FREESTYLE EVENTS",
          adjustmentCategory: null,
          eventIds: ["free-13", "free-14"],
        },
        {
          id: "slot-novelty",
          streamId: "pool",
          sortOrder: 1,
          startTime: "11:00:00",
          endTime: null,
          kind: "Item",
          label: "Novelty — staff v student relay",
          adjustmentCategory: null,
          eventIds: [],
        },
        {
          id: "slot-social",
          streamId: "pool",
          sortOrder: 2,
          startTime: "11:30:00",
          endTime: null,
          kind: "Events",
          label: "SOCIAL EVENTS",
          adjustmentCategory: null,
          eventIds: ["social-15"],
        },
      ],
    },
    {
      id: "track",
      name: "Track",
      sortOrder: 1,
      slots: [
        {
          id: "slot-track",
          streamId: "track",
          sortOrder: 0,
          startTime: "10:00:00",
          endTime: null,
          kind: "Events",
          label: "TRACK",
          adjustmentCategory: null,
          eventIds: ["sprint-13"],
        },
      ],
    },
  ],
};

describe("nextScheduledEventId", () => {
  it("follows the listed order inside a slot", () => {
    expect(nextScheduledEventId(schedule, "free-13")).toBe("free-14");
  });

  it("walks off the end of a block into the next block, stepping over item slots", () => {
    // The novelty slot holds no events at all, so it is passed over without a
    // dead end — and without ever being offered as somewhere to enter results.
    expect(nextScheduledEventId(schedule, "free-14")).toBe("social-15");
  });

  it("stops at the end of the stream rather than crossing into another one", () => {
    // Streams run concurrently (athletics: track beside each field site), so an
    // official at the pool is never sent to the track's first event.
    expect(nextScheduledEventId(schedule, "social-15")).toBeNull();
    expect(nextScheduledEventId(schedule, "sprint-13")).toBeNull();
  });

  it("suggests nothing for an unscheduled event or an absent program", () => {
    // Both are ordinary states: an event in no slot is fully enterable and
    // resultable, and offline the schedule may simply not be cached yet.
    expect(nextScheduledEventId(schedule, "not-on-the-program")).toBeNull();
    expect(nextScheduledEventId(undefined, "free-13")).toBeNull();
  });
});
