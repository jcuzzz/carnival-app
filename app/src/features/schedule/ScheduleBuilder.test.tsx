// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { placeScheduleEvent, updateScheduleSlot } from "../../api/resources";
import type { CarnivalEvent, Schedule } from "../../api/types";
import { ScheduleBuilder } from "./ScheduleBuilder";

// Writes are direct calls (organiser task at a desk, not deck capture — no
// outbox), so the endpoints are stubbed and the assertions are about what the
// builder ASKS FOR: a re-order must restate the whole slot, because the server's
// slot PUT replaces rather than patches.
vi.mock("../../api/resources", () => ({
  createScheduleSlot: vi.fn(),
  createScheduleStream: vi.fn(),
  deleteScheduleSlot: vi.fn(),
  deleteScheduleStream: vi.fn(),
  placeScheduleEvent: vi.fn(),
  removeScheduleEvent: vi.fn(),
  updateScheduleSlot: vi.fn(),
  updateScheduleStream: vi.fn(),
}));

/**
 * The organiser's builder (ADR-0014 §2). What matters here is not that a button
 * calls an endpoint — the endpoints are tested server-side — but the three
 * things the screen is responsible for being honest about:
 *
 *  - re-ordering is up/down controls, disabled at the ends of a list;
 *  - deleting a slot removes a line of the PROGRAM, and its events survive;
 *  - what is still unscheduled, because that is the organiser's real worklist.
 */

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
    scheduleSlotId: "slot-free",
    slotOrder: 0,
    status: "Open",
    version: 1,
    entryCount: 0,
    resultCount: 0,
    officials: [],
    ...over,
  } as CarnivalEvent;
}

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
          id: "slot-buses",
          streamId: "pool",
          sortOrder: 1,
          startTime: "14:30:00",
          endTime: null,
          kind: "Item",
          label: "Buses depart",
          adjustmentCategory: null,
          eventIds: [],
        },
      ],
    },
  ],
};

const events = [
  event({ id: "free-13" }),
  event({ id: "free-14", ageGroup: "14", slotOrder: 1 }),
  event({ id: "back-13", eventDefinitionId: "def-back", definitionName: "Backstroke 50m", scheduleSlotId: null, slotOrder: null }),
];

function renderBuilder(over: Partial<Parameters<typeof ScheduleBuilder>[0]> = {}) {
  // The screen's write cycle in miniature (ScheduleBuilderScreen.run): run the
  // action and report whether it LANDED, which is what every form here decides
  // to close or clear on.
  const onRun = vi.fn(async (action: () => Promise<unknown>) => {
    await action();
    return true;
  });
  render(
    <ScheduleBuilder
      carnivalId="c-1"
      schedule={schedule}
      events={events}
      adjustmentCategories={["Novelty"]}
      canManage
      busy={false}
      onRun={onRun}
      {...over}
    />,
  );
  return onRun;
}

beforeEach(() => {
  cleanup();
  vi.mocked(updateScheduleSlot).mockClear();
  vi.mocked(placeScheduleEvent).mockClear();
});

describe("ScheduleBuilder", () => {
  it("lists the program as streams of slots, with what each slot holds", () => {
    renderBuilder();
    const pool = within(screen.getByRole("region", { name: "Stream Pool" }));
    expect(pool.getByText("FREESTYLE EVENTS")).toBeTruthy();
    expect(pool.getByText("2 events")).toBeTruthy();
    expect(pool.getByText("2 slots · 2 events placed")).toBeTruthy();
    // A non-competition item is named as one, so nobody looks for its results.
    expect(pool.getByText("Non-competition item")).toBeTruthy();
  });

  it("disables the up control on the first slot and down on the last", () => {
    // Re-ordering is up/down, never drag-and-drop (an ADR-0014 non-goal).
    renderBuilder();
    expect(screen.getByLabelText("Move FREESTYLE EVENTS earlier").hasAttribute("disabled")).toBe(true);
    expect(screen.getByLabelText("Move FREESTYLE EVENTS later").hasAttribute("disabled")).toBe(false);
    expect(screen.getByLabelText("Move Buses depart earlier").hasAttribute("disabled")).toBe(false);
    expect(screen.getByLabelText("Move Buses depart later").hasAttribute("disabled")).toBe(true);
  });

  it("disables both event controls on a block holding one event, and pins the ends of a longer one", () => {
    renderBuilder();
    const up = screen.getByLabelText("Move Freestyle 50m 13 · Girls earlier in this block");
    const down = screen.getByLabelText("Move Freestyle 50m 14 · Girls later in this block");
    expect(up.hasAttribute("disabled")).toBe(true);
    expect(down.hasAttribute("disabled")).toBe(true);
    expect(
      screen
        .getByLabelText("Move Freestyle 50m 13 · Girls later in this block")
        .hasAttribute("disabled"),
    ).toBe(false);
  });

  it("says what removing a slot actually does before it does it", () => {
    // The honest framing (ADR-0014 §1): a line of the program goes, and the
    // events in it stay fully enterable — the schedule never gates anything.
    renderBuilder();
    const slot = screen.getByText("FREESTYLE EVENTS").closest("li")!;
    fireEvent.click(within(slot).getByRole("button", { name: "Remove" }));
    expect(within(slot).getByText(/become unscheduled/)).toBeTruthy();
    expect(within(slot).getByText(/keep every entry and result/)).toBeTruthy();
    expect(within(slot).getByRole("button", { name: "Confirm remove" })).toBeTruthy();
  });

  it("shows what is still unscheduled, and offers exactly those in the picker", () => {
    renderBuilder();
    const worklist = within(screen.getByRole("region", { name: /Not on the program/ }));
    expect(worklist.getByText("Backstroke 50m")).toBeTruthy();
    expect(worklist.queryByText("Freestyle 50m")).toBeNull();

    const picker = screen.getByLabelText("Add an event to this block") as HTMLSelectElement;
    expect([...picker.options].map((o) => o.text)).toEqual([
      "— 1 unscheduled —",
      "Backstroke 50m — 13 · Girls",
    ]);
  });

  it("reports a fully placed program instead of an empty list", () => {
    renderBuilder({ events: events.slice(0, 2) });
    expect(screen.getByText("All 2 events are placed in a slot.")).toBeTruthy();
  });

  it("hides every organiser control from someone who cannot use it, and nothing else", () => {
    // UI hiding is NOT access control (CLAUDE.md) — the server refuses the write.
    // This only avoids offering a button that would come back 403; the program
    // itself stays fully readable.
    renderBuilder({ canManage: false });
    expect(screen.getByText("FREESTYLE EVENTS")).toBeTruthy();
    expect(screen.getByText("Backstroke 50m")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Remove" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Add stream" })).toBeNull();
    expect(screen.queryByLabelText("Add an event to this block")).toBeNull();
  });

  it("invites a first stream rather than showing an empty program as a fault", () => {
    renderBuilder({ schedule: { carnivalId: "c-1", streams: [] } });
    expect(screen.getByText(/No program yet/)).toBeTruthy();
    expect(screen.getByText(/every event stays fully enterable/)).toBeTruthy();
    // Nothing to re-apply over, so the template caveat would be noise here.
    expect(screen.queryByText(/re-applying that template/)).toBeNull();
  });

  it("says that re-applying a template puts back a slot renamed or removed here", () => {
    // `apply-template` matches slots by LABEL and has no record of what it
    // created, so a deleted "PHOTOS" line returns on the next apply. Until slot
    // provenance lands (BACKLOG reconcile-template) the note is the whole fix —
    // and it is a note, not a guard: renaming and removing slots is what this
    // screen is for, so nothing here is blocked or confirmed twice.
    renderBuilder();
    expect(screen.getByText(/re-applying that template puts back any slot/)).toBeTruthy();
    expect(screen.getByText(/matches slots by their printed label/)).toBeTruthy();
    expect(screen.queryByRole("alertdialog")).toBeNull();
  });

  it("marks a slot that starts before the one above it, without acting on it", () => {
    // 09:45 typed as 19:45 is invisible until the program is printed. Running
    // out of order is legitimate though (ADR-0014 §1), so the mark is all that
    // happens: no control is disabled, no time is rewritten, nothing re-sorts.
    const pool = schedule.streams[0]!;
    const backwards: Schedule = {
      carnivalId: "c-1",
      streams: [
        {
          ...pool,
          slots: [pool.slots[1]!, { ...pool.slots[0]!, sortOrder: 1 }],
        },
      ],
    };
    renderBuilder({ schedule: backwards });

    const slot = screen.getByText("FREESTYLE EVENTS").closest("li")!;
    expect(within(slot).getByText("starts before the slot above")).toBeTruthy();
    expect(within(slot).getByRole("button", { name: "Edit" }).hasAttribute("disabled")).toBe(false);
    expect(screen.getByLabelText("Move FREESTYLE EVENTS earlier").hasAttribute("disabled")).toBe(
      false,
    );
    // The 14:30 slot now leads the stream, which is exactly what was asked for.
    const buses = screen.getByText("Buses depart").closest("li")!;
    expect(within(buses).queryByText("starts before the slot above")).toBeNull();
  });

  it("marks nothing in a stream that runs forwards", () => {
    renderBuilder();
    expect(screen.queryByText("starts before the slot above")).toBeNull();
  });

  it("re-orders a slot by restating both slots in full, never by patching the order", async () => {
    // The failure this pins: sending {sortOrder} alone would clear each slot's
    // label — the line the program prints — because the server's PUT replaces.
    const onRun = renderBuilder();
    fireEvent.click(screen.getByLabelText("Move Buses depart earlier"));
    await vi.waitFor(() => expect(onRun).toHaveBeenCalledTimes(1));

    const writes = vi.mocked(updateScheduleSlot).mock.calls;
    expect(writes.map(([, slotId, body]) => [slotId, body.sortOrder, body.label])).toEqual([
      ["slot-buses", 0, "Buses depart"],
      ["slot-free", 1, "FREESTYLE EVENTS"],
    ]);
    expect(writes[0]![2]).toMatchObject({ kind: "Item", startTime: "14:30:00" });
  });

  it("re-orders a block on the events' STORED order, never their position on screen", async () => {
    // Reachable through the builder's own buttons: unschedule the first two
    // events of a block and place two more, and the server APPENDS them at
    // max+1 — the block displays a tidy 13, 14, 15 while holding 2, 3, 4.
    // Ordering off the screen would leave the first event untouched at 2 and
    // write the third to 1, tying two events and throwing the moved one to the
    // top of the block.
    const pool = schedule.streams[0]!;
    const block = { ...pool.slots[0]!, eventIds: ["free-13", "free-14", "free-15"] };
    const drifted: Schedule = {
      carnivalId: "c-1",
      streams: [{ ...pool, slots: [block] }],
    };
    renderBuilder({
      schedule: drifted,
      events: [
        event({ id: "free-13", slotOrder: 2 }),
        event({ id: "free-14", ageGroup: "14", slotOrder: 3 }),
        event({ id: "free-15", ageGroup: "15", slotOrder: 4 }),
      ],
    });

    fireEvent.click(
      screen.getByLabelText("Move Freestyle 50m 15 · Girls earlier in this block"),
    );
    await vi.waitFor(() => expect(vi.mocked(placeScheduleEvent)).toHaveBeenCalledTimes(3));
    expect(
      vi.mocked(placeScheduleEvent).mock.calls.map(([, , body]) => [body.eventId, body.slotOrder]),
    ).toEqual([
      ["free-13", 0],
      ["free-15", 1],
      ["free-14", 2],
    ]);
  });

  it("keeps a refused edit open, with what the organiser typed still in it", async () => {
    // A write that failed must not look like one that worked: closing the form
    // would take the retyped label and both times with it and leave only a
    // banner, which is indistinguishable from a save on a screen with no outbox.
    const onRun = vi.fn(async () => false);
    renderBuilder({ onRun });
    const slot = screen.getByText("FREESTYLE EVENTS").closest("li")!;
    fireEvent.click(within(slot).getByRole("button", { name: "Edit" }));
    fireEvent.change(within(slot).getByLabelText("Printed label"), {
      target: { value: "SHOT PUT" },
    });
    fireEvent.change(within(slot).getByLabelText("Start"), { target: { value: "10:00" } });
    fireEvent.click(within(slot).getByRole("button", { name: "Save slot" }));

    await vi.waitFor(() => expect(onRun).toHaveBeenCalledTimes(1));
    expect((within(slot).getByLabelText("Printed label") as HTMLInputElement).value).toBe(
      "SHOT PUT",
    );
    expect((within(slot).getByLabelText("Start") as HTMLInputElement).value).toBe("10:00");
  });
});
