// @vitest-environment jsdom
import { cleanup, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { keys } from "../api/queryKeys";
import type { Carnival, CarnivalEvent, Schedule } from "../api/types";
import { renderWithQuery } from "../test/queryWrapper";

/**
 * The print view (ADR-0014 §2). It reads three resources and prints ONE
 * artefact, so the only behaviour worth pinning here is that it never renders a
 * partial one: a program handed to staff is wrong or it is nothing, and the
 * Print button is one tap away the whole time it is on screen.
 */

vi.mock("../api/resources", () => ({
  getCarnival: vi.fn(),
  getCarnivalSchedule: vi.fn(),
  listCarnivalEvents: vi.fn(),
}));

import { getCarnival, getCarnivalSchedule, listCarnivalEvents } from "../api/resources";
import ProgramScreen from "./ProgramScreen";

const carnival = {
  id: "c-1",
  name: "Swimming Carnival 2027",
  date: "2027-02-19",
  venue: "Aquatic Centre",
  status: "Open",
  type: "Swimming",
  ruleVersion: "v1",
  ownerSubject: "admin-1",
  version: 1,
} as Carnival;

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
          eventIds: ["free-13"],
        },
      ],
    },
  ],
};

const events = [
  {
    id: "free-13",
    carnivalId: "c-1",
    eventDefinitionId: "def-free",
    definitionCode: "SW-50-FREE",
    definitionName: "Freestyle 50m",
    ageGroup: "13",
    category: "F",
    measureType: "Time",
    attemptCount: null,
    ladder: null,
    heights: null,
    attemptsPerHeight: null,
    tier: "Competitive",
    scoringMode: null,
    effectiveScoringMode: "Places",
    scoresPoints: true,
    scheduleSlotId: "slot-free",
    slotOrder: 0,
    status: "Open",
    version: 1,
    complete: false,
    officials: [],
  } as CarnivalEvent,
];

/** The same program spread over `count` streams — athletics is six of them. */
function streams(count: number): Schedule {
  return {
    carnivalId: "c-1",
    streams: Array.from({ length: count }, (_, i) => ({
      ...schedule.streams[0]!,
      id: `stream-${i}`,
      name: `Stream ${i}`,
      sortOrder: i,
      slots: i === 0 ? schedule.streams[0]!.slots : [],
    })),
  };
}

/** The emitted `@page` rule, which is how the browser is told the orientation. */
function pageRule(container: HTMLElement): string {
  return [...container.querySelectorAll("style")].map((s) => s.textContent).join("");
}

/** A read the test releases by hand, to hold one response back. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

beforeEach(() => {
  cleanup();
  vi.mocked(getCarnival).mockReset();
  vi.mocked(getCarnivalSchedule).mockReset();
  vi.mocked(listCarnivalEvents).mockReset();
});

describe("ProgramScreen", () => {
  it("prints nothing until the event list is in, not a program of empty blocks", async () => {
    // The race on a first visit: the smallest response wins. With the carnival
    // and the schedule in but the events still in flight, rendering would put
    // every block's time and label on the page with ZERO event lines under
    // them — and someone tapping Print in that window gets a useless program.
    const pending = deferred<CarnivalEvent[]>();
    vi.mocked(getCarnival).mockResolvedValue(carnival);
    vi.mocked(getCarnivalSchedule).mockResolvedValue(schedule);
    vi.mocked(listCarnivalEvents).mockReturnValue(pending.promise);

    const { client } = renderWithQuery(<ProgramScreen carnivalId="c-1" />);
    await waitFor(() => expect(client.getQueryData(keys.schedule("c-1"))).toBeDefined());

    expect(screen.getByText("Loading…")).toBeTruthy();
    expect(screen.queryByText("FREESTYLE EVENTS")).toBeNull();
    expect(screen.queryByRole("heading", { name: carnival.name })).toBeNull();

    pending.resolve(events);
    await screen.findByText("FREESTYLE EVENTS");
    expect(screen.getByText("Freestyle 50m")).toBeTruthy();
    expect(screen.queryByText("Loading…")).toBeNull();
  });

  it("does not claim a carnival has no program while the schedule is still loading", async () => {
    // The other half of the same race: the carnival alone is enough to render
    // "No program has been built for this carnival yet" over a carnival that
    // has one.
    const pending = deferred<Schedule>();
    vi.mocked(getCarnival).mockResolvedValue(carnival);
    vi.mocked(getCarnivalSchedule).mockReturnValue(pending.promise);
    vi.mocked(listCarnivalEvents).mockResolvedValue(events);

    const { client } = renderWithQuery(<ProgramScreen carnivalId="c-1" />);
    await waitFor(() => expect(client.getQueryData(keys.carnival("c-1"))).toBeDefined());

    expect(screen.queryByText(/No program has been built/)).toBeNull();

    pending.resolve(schedule);
    await screen.findByText("FREESTYLE EVENTS");
  });

  it("leaves a one-stream program portrait, hint and all", async () => {
    // Swimming: one column of timed blocks. Forcing landscape would waste half
    // the page, and there is nothing to advise staff about either.
    vi.mocked(getCarnival).mockResolvedValue(carnival);
    vi.mocked(getCarnivalSchedule).mockResolvedValue(schedule);
    vi.mocked(listCarnivalEvents).mockResolvedValue(events);

    const { container } = renderWithQuery(<ProgramScreen carnivalId="c-1" />);
    await screen.findByText("FREESTYLE EVENTS");

    expect(pageRule(container)).not.toContain("landscape");
    expect(screen.queryByText(/print in landscape/)).toBeNull();
  });

  it("advises landscape at four streams and sets it beyond what portrait holds", async () => {
    // A portrait A4 holds four 42mm columns, so four is advice — and the fifth
    // is where portrait starts wrapping columns down the page. Staff print this
    // once, early, under time pressure, so that one is not left to a hint.
    vi.mocked(getCarnival).mockResolvedValue(carnival);
    vi.mocked(listCarnivalEvents).mockResolvedValue(events);

    vi.mocked(getCarnivalSchedule).mockResolvedValue(streams(4));
    const four = renderWithQuery(<ProgramScreen carnivalId="c-1" />);
    await screen.findByText(/4 streams/);
    expect(pageRule(four.container)).not.toContain("landscape");

    // The boundary itself: five is the first count portrait cannot hold
    // (5 x 42mm = 210mm against 186mm usable). Asserting only 4 and 6 would let
    // the threshold drift by one without a test noticing.
    cleanup();
    vi.mocked(getCarnivalSchedule).mockResolvedValue(streams(5));
    const five = renderWithQuery(<ProgramScreen carnivalId="c-1" />);
    await screen.findByText(/5 streams/);
    expect(pageRule(five.container)).toContain("@page { size: landscape; }");

    cleanup();
    vi.mocked(getCarnivalSchedule).mockResolvedValue(streams(6));
    const six = renderWithQuery(<ProgramScreen carnivalId="c-1" />);
    await screen.findByText(/6 streams/);
    expect(pageRule(six.container)).toContain("@page { size: landscape; }");
    // The hint stays: Safari ignores `@page { size }`, and staff can override
    // the orientation in the dialog whatever the stylesheet asks for.
    expect(screen.getByText(/print in landscape/)).toBeTruthy();

    // It promises no column per stream: landscape holds six 42mm columns, and
    // athletics runs seven, so the seventh wraps onto a second row.
    cleanup();
    vi.mocked(getCarnivalSchedule).mockResolvedValue(streams(7));
    renderWithQuery(<ProgramScreen carnivalId="c-1" />);
    const hint = await screen.findByText(/7 streams/);
    expect(hint.textContent).toContain("streams wrap onto extra rows when they do not fit");
    expect(hint.textContent).not.toContain("one column per stream");
  });

  it("marks a draft carnival's program as a draft, on the page that gets printed", async () => {
    // Anyone who may view the carnival may read this program while it is still
    // being built — fine, it holds no student data — but a half-built program
    // can be printed and handed out, and paper carries no status. So the mark
    // sits INSIDE `.program`, which is the only thing the print stylesheet keeps.
    vi.mocked(getCarnival).mockResolvedValue({ ...carnival, status: "Draft" } as Carnival);
    vi.mocked(getCarnivalSchedule).mockResolvedValue(schedule);
    vi.mocked(listCarnivalEvents).mockResolvedValue(events);

    const { container } = renderWithQuery(<ProgramScreen carnivalId="c-1" />);
    const mark = await screen.findByText(/DRAFT/);
    expect(mark.textContent).toMatch(/may still change/);
    expect(container.querySelector(".program")!.contains(mark)).toBe(true);
    expect(mark.closest(".no-print")).toBeNull();
  });

  it("prints an open carnival's program clean", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival);
    vi.mocked(getCarnivalSchedule).mockResolvedValue(schedule);
    vi.mocked(listCarnivalEvents).mockResolvedValue(events);

    renderWithQuery(<ProgramScreen carnivalId="c-1" />);
    await screen.findByText("FREESTYLE EVENTS");
    expect(screen.queryByText(/DRAFT/)).toBeNull();
  });
});
