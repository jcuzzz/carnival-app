// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { QueryWrapper, renderWithQuery } from "../test/queryWrapper";
import { db, type OutboxItem, type OutboxStatus } from "../db/db";
import { saveCachedRoster } from "../db/roster";
import { discard } from "../sync/outbox";
import { _resetUuidv7StateForTests } from "../sync/uuidv7";
import { ARMING_LOCKOUT_MS } from "../features/ui/timing";
import { signIn, signOut } from "../auth/session";
import type {
  CarnivalEvent,
  EligibleStudent,
  Entry,
  Race,
  ResultDto,
  Schedule,
} from "../api/types";
import type {
  EntryCreatePayload,
  ResultCorrectPayload,
  ResultSubmitPayload,
} from "../api/outboxTransport";

/**
 * The event-day core: submitting a result must go through the OUTBOX (client
 * UUIDv7, optimistic display, live per-entry sync badge) — never a direct
 * fetch. The API layer is mocked; the outbox is the real Dexie one.
 */

const event: CarnivalEvent = {
  id: "ev-1",
  carnivalId: "c-1",
  eventDefinitionId: "def-1",
  definitionCode: "SWIM-50-FS",
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
  scheduleSlotId: null,
  slotOrder: null,
  status: "Open",
  version: 1,
  entryCount: 1,
  complete: false,
  officials: [],
};

const entry: Entry = {
  id: "en-1",
  carnivalEventId: "ev-1",
  studentId: "st-1",
  raceId: null,
  heat: 1,
  lane: 3,
  displayName: "Sam Swimmer",
  yearLevel: 7,
  houseCode: "BANK",
  ageGroup: "U13",
  overrideApplied: false,
  isTeam: false,
  teamHouseCode: null,
  legs: [],
};

const syncNow = vi.fn().mockResolvedValue({
  stopReason: "offline",
  accepted: 0,
  rejected: 0,
  conflicts: 0,
});

/** A recorded, still-provisional result for the one entry (correction base). */
const recorded: ResultDto = {
  id: "res-1",
  entryId: "en-1",
  status: "Ok",
  measuredValue: 83.45,
  jumps: null,
  lifecycleState: "Verified",
  currentRevision: 2,
  recordedAtUtc: "2026-07-31T01:00:00Z",
  recordedBySubject: "official-1",
  place: null,
};

/** A sibling division of the same definition — the selector's switch target. */
const sibling: CarnivalEvent = {
  ...event,
  id: "ev-2",
  ageGroup: "14",
  entryCount: 4,
  resultCount: 1,
};

/**
 * The program (ADR-0014 §1): both divisions listed in one block, so "Next
 * event →" has a stream to follow. Advisory — nothing on this screen may gate
 * on it.
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
          id: "slot-1",
          streamId: "pool",
          sortOrder: 0,
          startTime: "09:45:00",
          endTime: null,
          kind: "Events",
          label: "FREESTYLE EVENTS",
          adjustmentCategory: null,
          eventIds: ["ev-1", "ev-2"],
        },
      ],
    },
  ],
};

/**
 * The add-entry panel reads the PAGE (students + truncated + limit, K11); every
 * test that only cares about who is eligible keeps setting `searchEligibleStudents`,
 * and the page delegates to it. A test about truncation overrides the page directly.
 */
const eligible = vi.hoisted(() => {
  type Search = (
    carnivalId: string,
    eventId: string,
    search: string,
    house?: string,
  ) => Promise<EligibleStudent[]>;
  const students = vi.fn<Search>(() => Promise.resolve([]));
  const page = async (carnivalId: string, eventId: string, search: string, house?: string) => ({
    students: await students(carnivalId, eventId, search, house),
    truncated: false,
    limit: 50,
  });
  return { students, page };
});

vi.mock("../api/resources", () => ({
  // The screen fetches the single event, never the whole list; the list and the
  // schedule are read separately, under the keys the carnival landing already
  // filled, for the division selector and "Next event →".
  getCarnivalEvent: vi.fn(() => Promise.resolve(event)),
  listCarnivalEvents: vi.fn(() => Promise.resolve([event, sibling])),
  getCarnivalSchedule: vi.fn(() => Promise.resolve(schedule)),
  listEntries: vi.fn(() => Promise.resolve([entry])),
  listResults: vi.fn(() => Promise.resolve([])),
  // ADR-0018 §4: which race an entry sits in is knowable only here.
  listRaces: vi.fn(() => Promise.resolve([])),
  // The venue lane count (ADR-0015 §6), read by lane mode.
  getCarnivalSettings: vi.fn(() => Promise.resolve({ laneCount: 6, scoring: null, adjustmentCategories: [] })),
  // The standing record book (ADR-0028 §5) — empty here, so nothing badges.
  getRecords: vi.fn(() => Promise.resolve([])),
  searchEligibleStudents: eligible.students,
  searchEligibleStudentsPage: vi.fn(eligible.page),
  createEntry: vi.fn(),
  correctResult: vi.fn(),
  // ADR-0056 §2: raising the bar is a direct call, not an outbox kind.
  raiseBar: vi.fn(),
  // ADR-0054 §4: the notice that refuses results carries the open control.
  changeEventStatus: vi.fn(),
}));

/**
 * The outbox live query is a round trip behind a tap. A test that needs THAT
 * window — where this device's refs are the only record of a mint — freezes the
 * hook at a snapshot; every other test reads the real Dexie query.
 */
const liveOutbox = vi.hoisted(() => ({ frozen: null as unknown[] | null }));
vi.mock("../sync/useOutboxStatus", async (importOriginal) => {
  const actual = await importOriginal<typeof import("../sync/useOutboxStatus")>();
  return {
    ...actual,
    useOutboxItems: (statuses?: readonly OutboxStatus[]) => {
      const live = actual.useOutboxItems(statuses);
      return (liveOutbox.frozen as OutboxItem[] | null) ?? live;
    },
  };
});

vi.mock("../api/syncSetup", () => ({
  syncWorker: {
    start: vi.fn(),
    stop: vi.fn(),
    syncNow: (...args: unknown[]) => syncNow(...args),
  },
  resumeAfterSignIn: vi.fn(),
}));

import { submitAssignmentViaOutbox, submitSwapViaOutbox } from "../races/assignEntry";
import { submitMaterialiseViaOutbox } from "../races/submitRace";
import { divisionChipLabel } from "../features/events/divisions";
import EventResultsScreen, { deckPollMs } from "./EventResultsScreen";

/** The display names the races read carries for the fixtures above. */
const NAMES: Record<string, string> = { "en-1": "Sam Swimmer", "en-2": "Tessa Fourteen" };

/** A race holding the named [entryId, eventId] pairs (the races read shape). */
function race(
  id: string,
  members: [string, string][],
  lanes: (number | null)[] = [],
  eventId: string | null = null,
): Race {
  return {
    id,
    eventId,
    scheduleSlotId: null,
    sortOrder: 0,
    label: "Race 1",
    entries: members.map(([entryId, carnivalEventId], i) => ({
      entryId,
      carnivalEventId,
      displayName: NAMES[entryId] ?? entryId,
      houseCode: "BANK",
      isTeam: false,
      // Materialising assigns NO heat: entries join the race with their heats
      // untouched, so nothing about it is a scoring act.
      heat: null,
      lane: lanes[i] ?? null,
    })),
  };
}

/** navigator.onLine drives the correction path (direct call vs outbox). */
function setOnline(online: boolean) {
  Object.defineProperty(navigator, "onLine", { value: online, configurable: true });
}

beforeEach(async () => {
  cleanup();
  _resetUuidv7StateForTests();
  await db.outbox.clear();
  await db.syncMeta.clear();
  await db.eventRosters.clear();
  const {
    listResults,
    searchEligibleStudents,
    searchEligibleStudentsPage,
    correctResult,
    getCarnivalEvent,
    listEntries,
    listCarnivalEvents,
    getCarnivalSchedule,
    listRaces,
    getCarnivalSettings,
  } = await import("../api/resources");
  vi.mocked(getCarnivalSettings).mockResolvedValue({
    laneCount: 6,
    scoring: null,
    adjustmentCategories: [],
  } as never);
  vi.mocked(listRaces).mockReset();
  vi.mocked(listRaces).mockResolvedValue([]);
  vi.mocked(searchEligibleStudentsPage).mockImplementation(eligible.page);
  vi.mocked(getCarnivalEvent).mockResolvedValue(event);
  vi.mocked(listEntries).mockResolvedValue([entry]);
  vi.mocked(listCarnivalEvents).mockClear();
  vi.mocked(listCarnivalEvents).mockResolvedValue([event, sibling]);
  vi.mocked(getCarnivalSchedule).mockResolvedValue(schedule);
  vi.mocked(listResults).mockResolvedValue([]);
  vi.mocked(searchEligibleStudents).mockResolvedValue([]);
  vi.mocked(correctResult).mockReset();
  setOnline(true);
  // The selected heat is URL state (ADR-0039 §1), so it has to be reset like
  // any other state or one test's `?heat=` decides the next one's deck.
  location.hash = "";
  syncNow.mockClear();
  liveOutbox.frozen = null;
  signOut();
  signIn({ subject: "official-1", displayName: "Olivia Official", roles: ["Recorder"] });
});

/**
 * ADR-0041 §2: DNS/DNF/DQ, the lane picker and the removal all live behind one
 * per-row actions menu. This is a thumb opening it and choosing.
 */
function rowAction(name: string, item: string): void {
  fireEvent.click(screen.getByRole("button", { name: `Actions for ${name}` }));
  fireEvent.click(screen.getByRole("menuitem", { name: item }));
}

/**
 * ADR-0042 §2: an axis is a closed button reading its current value, over a
 * PORTALLED menu — so items are queried off `screen`, never within the group.
 */
function openPicker(name: string | RegExp): void {
  fireEvent.click(screen.getByRole("button", { name }));
}
const pickerItem = (name: string | RegExp) => screen.getByRole("menuitem", { name });
/** The heat trigger reads "Heat {label} {count}" — the axis, then the value. */
const openHeats = () => openPicker(/^Heat /);
const heatItems = () => screen.getAllByRole("menuitem", { name: /^Heat \d/ });
const closeMenu = () => fireEvent.keyDown(screen.getByRole("menu"), { key: "Escape" });

/**
 * The numeral block that leads a row (ADR-0041 §3) — an em dash for no lane.
 * What the deck BELIEVES about a placement is read here rather than off the
 * picker: a picker read inside `waitFor` would have to reveal itself on every
 * poll, and a `waitFor` whose body mutates the DOM never reaches its timeout.
 */
// The lockout reads Date.now, so a real wait with a margin is the honest way past it.
const pastArmingLockout = () => new Promise((r) => setTimeout(r, ARMING_LOCKOUT_MS + 20));

function laneNumber(name: string): string {
  return screen.getByText(name).closest("li")!.querySelector(".lane-number")!.textContent ?? "";
}

/**
 * That row's revealed lane picker. Reveals it only when it is not already up:
 * a helper that mutated the DOM every call would starve `waitFor`, whose
 * MutationObserver would re-run it forever inside one task.
 */
function lanePicker(name: string): HTMLSelectElement {
  const showing = screen.queryByLabelText(`Lane for ${name}`);
  if (showing) return showing as HTMLSelectElement;
  rowAction(name, "Change lane");
  return screen.getByLabelText(`Lane for ${name}`) as HTMLSelectElement;
}

describe("EventResultsScreen", () => {
  it("loads the single event via GET /carnivals/{id}/events/{eventId}", async () => {
    const { getCarnivalEvent } = await import("../api/resources");
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Freestyle 50m");
    expect(vi.mocked(getCarnivalEvent)).toHaveBeenCalledWith("c-1", "ev-1");
  });

  it("offers the sibling divisions in a selector that NAVIGATES, marking the current one", async () => {
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByRole("group", { name: "Divisions" });
    openPicker("Age group 13");

    // The current division is not a link — it is where you already are — and it
    // says so to assistive technology.
    expect(pickerItem(/^13 · Girls/).getAttribute("aria-current")).toBe("page");

    // ADR-0014 rejects in-page switching: the item is a real route change, so
    // deep links, refresh, browser-back, audit and the outbox all still key off
    // the instance id.
    const other = pickerItem(/^14 · Girls/);
    expect(other.getAttribute("href")).toBe("#/carnivals/c-1/events/ev-2");
    // Each item carries its own division's progress: results entered / entries.
    expect(other.textContent).toContain("1/4");
  });

  it("marks the sibling divisions this official is assigned to run (ADR-0009)", async () => {
    // ORDERING, NEVER ACCESS CONTROL: the badge says "this one is yours to run",
    // and every other division stays one tap away.
    const { listCarnivalEvents } = await import("../api/resources");
    vi.mocked(listCarnivalEvents).mockResolvedValue([
      event,
      { ...sibling, officials: [{ subjectId: "official-1", subjectDisplayName: "Olivia Official" }] },
    ]);

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByRole("group", { name: "Divisions" });
    openPicker("Age group 13");
    expect(pickerItem(/^14 · Girls/).textContent).toContain("yours");
  });

  it("drops the add-entry panel wholesale when the selector switches division (ADR-0014 §5)", async () => {
    // An item is a hash-route change: the SAME screen element re-renders at the
    // same position, so anything React can preserve, it does. The reads re-key
    // on eventId; the add panel must not be allowed to.
    const { getCarnivalEvent, listEntries, searchEligibleStudents } =
      await import("../api/resources");
    vi.mocked(getCarnivalEvent).mockImplementation((_c, id) =>
      Promise.resolve(id === "ev-2" ? sibling : event),
    );
    vi.mocked(listEntries).mockImplementation((_c, id) =>
      Promise.resolve(id === "ev-2" ? [] : [entry]),
    );
    // 13·F answers from the server; 14·F is the venue default — no uplink and
    // nothing cached for it, which is exactly when carried state does the damage.
    vi.mocked(searchEligibleStudents).mockImplementation((_c, id) =>
      id === "ev-2"
        ? Promise.reject(new TypeError("Failed to fetch"))
        : Promise.resolve([
            { studentId: "st-13", displayName: "Thirteen Swimmer", yearLevel: 7, houseCode: "WATTLE", ageGroup: "U13" },
          ]),
    );

    const { client, rerender } = renderWithQuery(
      <EventResultsScreen carnivalId="c-1" eventId="ev-1" />,
    );
    await screen.findByText("Sam Swimmer");
    fireEvent.change(screen.getByLabelText("Search eligible students"), {
      target: { value: "swimmer" },
    });
    expect(await screen.findByText("Thirteen Swimmer")).toBeTruthy();

    rerender(
      <QueryWrapper client={client}>
        <EventResultsScreen carnivalId="c-1" eventId="ev-2" />
      </QueryWrapper>,
    );
    await screen.findByText("No entries yet — add students below.");

    // Neither the term nor the roster it was searching crosses the boundary…
    await waitFor(() => {
      expect((screen.getByLabelText("Search eligible students") as HTMLInputElement).value).toBe("");
    });
    expect(screen.queryByText("Thirteen Swimmer")).toBeNull();
    // …and searching again on the new division finds nothing rather than
    // offering 13-year-olds under a 14·F header.
    fireEvent.change(screen.getByLabelText("Search eligible students"), {
      target: { value: "swimmer" },
    });
    expect(await screen.findByText(/No eligible students match/)).toBeTruthy();
    expect(screen.queryByText("Thirteen Swimmer")).toBeNull();
  });

  it("re-offers a student on the sibling division after adding them here (ADR-0014 §9)", async () => {
    // The escape path from a Social twin: enter the swimmer in the Competitive
    // event as well, so the time can count. A carried "already added" set makes
    // that look like an eligibility failure — the student simply isn't there.
    const { getCarnivalEvent, listEntries, searchEligibleStudents } =
      await import("../api/resources");
    vi.mocked(getCarnivalEvent).mockImplementation((_c, id) =>
      Promise.resolve(id === "ev-2" ? sibling : event),
    );
    vi.mocked(listEntries).mockImplementation((_c, id) =>
      Promise.resolve(id === "ev-2" ? [] : [entry]),
    );
    vi.mocked(searchEligibleStudents).mockResolvedValue([
      { studentId: "st-late", displayName: "Late Swimmer", yearLevel: 7, houseCode: "WATTLE", ageGroup: "U13" },
    ]);

    const { client, rerender } = renderWithQuery(
      <EventResultsScreen carnivalId="c-1" eventId="ev-1" />,
    );
    await screen.findByText("Sam Swimmer");
    fireEvent.change(screen.getByLabelText("Search eligible students"), {
      target: { value: "late" },
    });
    fireEvent.click(await screen.findByRole("button", { name: "Add" }));
    await screen.findByText(/adding/);
    expect(screen.queryByRole("button", { name: "Add" })).toBeNull(); // removed here

    rerender(
      <QueryWrapper client={client}>
        <EventResultsScreen carnivalId="c-1" eventId="ev-2" />
      </QueryWrapper>,
    );
    await screen.findByText("No entries yet — add students below.");
    fireEvent.change(screen.getByLabelText("Search eligible students"), {
      target: { value: "late" },
    });
    expect(await screen.findByRole("button", { name: "Add" })).toBeTruthy();
  });

  it("keeps the progress off a deck with no rows — \"0 of 0 recorded\" is a claim", async () => {
    // Nothing has been read yet, or the division is genuinely empty: either way
    // a bar at 0% asserts a completeness this deck cannot see.
    const { listEntries } = await import("../api/resources");
    vi.mocked(listEntries).mockResolvedValue([]);
    const { container } = renderWithQuery(
      <EventResultsScreen carnivalId="c-1" eventId="ev-1" />,
    );
    await screen.findByText("No entries yet — add students below.");
    expect(container.querySelector(".deck-progress")).toBeNull();
  });

  it("counts a queued capture on the current division, so progress is honest offline", async () => {
    // ADR-0014 §1 makes this indicator the source of truth for "what's left".
    // `resultCount` is a server aggregate, so with the uplink down it would read
    // 0/1 with the saved row sitting directly below it.
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByRole("group", { name: "Divisions" });
    openPicker("Age group 13");
    expect(pickerItem(/^13 · Girls/).textContent).toContain("0/1");
    closeMenu();

    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), { target: { value: "34.56" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await screen.findByText("pending");

    openPicker("Age group 13");
    await waitFor(() => {
      expect(pickerItem(/^13 · Girls/).textContent).toContain("1/1");
    });
  });

  it("marks the event list stale on acceptance, so tile and selector progress heal", async () => {
    // The list key is a PREFIX of the results key, never the reverse, so
    // invalidating results alone leaves every tile's count at its last value.
    const { listCarnivalEvents } = await import("../api/resources");
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), { target: { value: "34.56" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await screen.findByText("pending");
    const before = vi.mocked(listCarnivalEvents).mock.calls.length;

    const item = (await db.outbox.toArray())[0]!;
    await db.outbox.put({ ...item, status: "accepted", serverResponse: { id: item.id } });

    await waitFor(() => {
      expect(vi.mocked(listCarnivalEvents).mock.calls.length).toBeGreaterThan(before);
    });
  });

  it("suggests the next event on the program, and hides it when there is none", async () => {
    const { getCarnivalSchedule } = await import("../api/resources");

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    const next = await screen.findByRole("link", { name: /Next event/ });
    expect(next.getAttribute("href")).toBe("#/carnivals/c-1/events/ev-2");

    // Unscheduled (or a program that could not be reached offline): no
    // suggestion at all, and certainly no warning — running out of order is
    // normal and correct.
    cleanup();
    vi.mocked(getCarnivalSchedule).mockResolvedValue({ carnivalId: "c-1", streams: [] });
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(screen.queryByRole("link", { name: /Next event/ })).toBeNull();
  });

  it("keeps result entry working when the navigation reads fail", async () => {
    // The selector and the suggestion are additive. A failed event-list or
    // schedule read must not raise an alert over the deck rows, and must not
    // stop a time being captured.
    const { listCarnivalEvents, getCarnivalSchedule } = await import("../api/resources");
    vi.mocked(listCarnivalEvents).mockRejectedValue(new Error("Network error"));
    vi.mocked(getCarnivalSchedule).mockRejectedValue(new Error("Network error"));

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    // No division to switch to without the list — but the axes still NAME the
    // division (ADR-0042 §1 deleted the meta line that used to), and the heat
    // axis (ADR-0039) is drawn from the races read, which did not fail.
    expect(screen.queryByRole("button", { name: /^Age group/ })).toBeNull();
    expect(screen.getByRole("group", { name: "Divisions" })).toBeTruthy();
    expect(screen.getByRole("group", { name: "Heats" })).toBeTruthy();
    expect(screen.queryByRole("link", { name: /Next event/ })).toBeNull();
    expect(screen.queryByRole("alert")).toBeNull();

    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), { target: { value: "34.56" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await screen.findByText("pending");
    expect(await db.outbox.count()).toBe(1);
  });

  it("submits a swim time through the outbox with optimistic display and pending badge", async () => {
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), {
      target: { value: "1:23.45" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    // Optimistic row: value + pending badge, no server round trip needed.
    await screen.findByText("pending");
    expect(screen.getByLabelText("Result for Sam Swimmer").textContent).toBe("1:23.45");

    const items = await db.outbox.toArray();
    expect(items).toHaveLength(1);
    const item = items[0]!;
    expect(item.kind).toBe("result.submit");
    expect(item.id).toMatch(/^[0-9a-f-]{36}$/); // capture-time UUIDv7
    expect(item.actorSubject).toBe("official-1"); // actor captured per item
    expect(item.status).toBe("queued");
    const payload = item.payload as ResultSubmitPayload;
    expect(payload).toMatchObject({
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      value: "1:23.45",
      status: null,
    });
    expect(syncNow).toHaveBeenCalled(); // immediate drain attempt, fire-and-forget
  });

  it("blocks an out-of-range time inline, disables Save, and enqueues nothing", async () => {
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    // 73 in the seconds field is unreachable/invalid — the masked field shows it
    // literally (never silently carried into minutes) and Save stays disabled.
    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), {
      target: { value: "1:73.45" },
    });

    await screen.findByRole("alert");
    expect(screen.getByRole("alert").textContent).toContain("under 60");
    expect((screen.getByLabelText(/Time for Sam Swimmer/) as HTMLInputElement).value).toBe("1:73.45");
    expect((screen.getByRole("button", { name: "Save" }) as HTMLButtonElement).disabled).toBe(true);
    expect(await db.outbox.count()).toBe(0);
  });

  it("submits DNS as a non-result status through the outbox", async () => {
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    rowAction("Sam Swimmer", "DNS");
    await screen.findByText("pending");

    const items = await db.outbox.toArray();
    expect(items).toHaveLength(1);
    // One canonical client casing (= the casing reads come back in); the
    // transport re-spells it for the wire.
    expect((items[0]!.payload as ResultSubmitPayload).status).toBe("Dns");
    expect((items[0]!.payload as ResultSubmitPayload).value).toBeNull();
  });

  it("adds a student offline through the outbox as an optimistic 'adding' row (ADR-0010)", async () => {
    const { searchEligibleStudents } = await import("../api/resources");
    vi.mocked(searchEligibleStudents).mockResolvedValue([
      { studentId: "st-late", displayName: "Late Swimmer", yearLevel: 7, houseCode: "WATTLE", ageGroup: "U13" },
    ]);

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    // As-you-type search (debounced) surfaces the student; adding enqueues an
    // entry.create — no network needed — and the row appears optimistically.
    fireEvent.change(screen.getByLabelText("Search eligible students"), {
      target: { value: "late" },
    });
    fireEvent.click(await screen.findByRole("button", { name: "Add" }));

    await screen.findByText(/adding/);
    expect(screen.getByText("Late Swimmer")).toBeTruthy();

    const items = await db.outbox.toArray();
    const createItem = items.find((i) => i.kind === "entry.create");
    expect(createItem).toBeTruthy();
    expect(createItem!.id).toMatch(/^[0-9a-f-]{36}$/); // client entry id = idempotency key
    expect((createItem!.payload as EntryCreatePayload).studentId).toBe("st-late");
    expect(createItem!.actorSubject).toBe("official-1");
    expect(syncNow).toHaveBeenCalled();
  });

  /**
   * The browsable eligible list (owner UX request 2026-08-13). Populating a race
   * is adding a dozen swimmers in a row, so the list is ON SCREEN with the panel
   * — typing narrows it, it never conjures it.
   */
  describe("browsable eligible list", () => {
    const roster: EligibleStudent[] = [
      { studentId: "st-a", displayName: "Ana Aardvark", yearLevel: 7, houseCode: "WATTLE", ageGroup: "U13" },
      { studentId: "st-b", displayName: "Bea Bandicoot", yearLevel: 7, houseCode: "BANK", ageGroup: "U13" },
      { studentId: "st-c", displayName: "Cass Cockatoo", yearLevel: 8, houseCode: "MTN", ageGroup: "U13" },
    ];

    /** The Add buttons of the eligible list, in list order. */
    function addButtons() {
      return screen.queryAllByRole("button", { name: "Add" });
    }

    it("shows the eligible roster with nothing typed, one Add per row", async () => {
      const { searchEligibleStudents } = await import("../api/resources");
      vi.mocked(searchEligibleStudents).mockResolvedValue(roster);

      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      await screen.findByText("Ana Aardvark");

      // No keystroke was sent: the panel asked for the whole eligible set.
      expect((screen.getByLabelText("Search eligible students") as HTMLInputElement).value).toBe("");
      expect(vi.mocked(searchEligibleStudents)).toHaveBeenCalledWith("c-1", "ev-1", "", undefined);
      expect(screen.getByText("Bea Bandicoot")).toBeTruthy();
      expect(screen.getByText("Cass Cockatoo")).toBeTruthy();
      expect(addButtons()).toHaveLength(3);
      // The context the row carries, so nobody adds a swimmer to the wrong race
      // off a bare name (ADR-0014 §5). House, year and age group only: the
      // category is the division picker's alone (ADR-0026 §2).
      expect(screen.getByText(/^BANK · Yr 7 · U13$/)).toBeTruthy();
      const list = within(document.querySelector(".eligible-list") as HTMLElement);
      for (const spelling of ["Girls", "U13 Girls", "F"]) {
        expect(list.queryByText(spelling)).toBeNull();
      }
    });

    it("adds straight from the list through the outbox and advances focus", async () => {
      const { searchEligibleStudents } = await import("../api/resources");
      vi.mocked(searchEligibleStudents).mockResolvedValue(roster);

      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      await screen.findByText("Ana Aardvark");

      fireEvent.click(addButtons()[0]!);
      await screen.findByText(/adding/);

      // The SAME submit path as the search flow: one entry.create, capture-time
      // actor, client id = idempotency key, drain attempted immediately.
      const createItem = (await db.outbox.toArray()).find((i) => i.kind === "entry.create");
      expect(createItem).toBeTruthy();
      expect((createItem!.payload as EntryCreatePayload).studentId).toBe("st-a");
      expect(createItem!.actorSubject).toBe("official-1");
      expect(createItem!.status).toBe("queued");
      expect(syncNow).toHaveBeenCalled();

      // She leaves the list at once and stays gone while the create is queued…
      await waitFor(() => expect(addButtons()).toHaveLength(2));
      // …and the finger/cursor lands on the next row rather than on nothing.
      await waitFor(() => expect(document.activeElement).toBe(addButtons()[0]));
    });

    it("narrows on typing and comes back in full when the box is cleared", async () => {
      const { searchEligibleStudents } = await import("../api/resources");
      vi.mocked(searchEligibleStudents).mockImplementation((_c, _e, search) =>
        Promise.resolve(
          search === ""
            ? roster
            : roster.filter((s) => s.displayName.toLowerCase().includes(search.toLowerCase())),
        ),
      );

      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      await screen.findByText("Cass Cockatoo");

      fireEvent.change(screen.getByLabelText("Search eligible students"), {
        target: { value: "bandi" },
      });
      await waitFor(() => expect(screen.queryByText("Cass Cockatoo")).toBeNull());
      expect(screen.getByText("Bea Bandicoot")).toBeTruthy();

      fireEvent.change(screen.getByLabelText("Search eligible students"), { target: { value: "" } });
      expect(await screen.findByText("Cass Cockatoo")).toBeTruthy();
      expect(addButtons()).toHaveLength(3);
    });

    it("says so when the page is capped, rather than looking complete (K11)", async () => {
      const { searchEligibleStudentsPage } = await import("../api/resources");
      vi.mocked(searchEligibleStudentsPage).mockResolvedValue({
        students: roster,
        truncated: true,
        limit: 50,
      });

      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      await screen.findByText("Ana Aardvark");
      expect(screen.getByText(/showing the first 50 — type to narrow/i)).toBeTruthy();
    });

    it("tells 'everyone is already entered' apart from 'nothing matched'", async () => {
      // Both are an empty list and they mean opposite things; staff see both on
      // the day, and one of them is not a problem at all.
      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      expect(await screen.findByText(/Everyone eligible is already entered/)).toBeTruthy();

      fireEvent.change(screen.getByLabelText("Search eligible students"), {
        target: { value: "nobody" },
      });
      expect(await screen.findByText(/No eligible students match/)).toBeTruthy();
      expect(screen.queryByText(/Everyone eligible is already entered/)).toBeNull();
    });

    it("browses the saved roster offline, and hands a student back when the add is refused", async () => {
      const { searchEligibleStudents } = await import("../api/resources");
      await saveCachedRoster("ev-1", roster, { truncated: false, limit: 50 });
      vi.mocked(searchEligibleStudents).mockRejectedValue(new TypeError("Failed to fetch"));

      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      // No uplink, nothing typed: the saved roster is browsable exactly as the
      // server list would have been, and says which one it is.
      await screen.findByText("Ana Aardvark");
      expect(screen.getByText("saved roster")).toBeTruthy();
      expect(addButtons()).toHaveLength(3);

      fireEvent.click(addButtons()[0]!);
      await screen.findByText(/adding/);
      await waitFor(() => expect(addButtons()).toHaveLength(2));

      // The server refuses it when the queue finally drains (ineligible, say).
      // The swimmer was never entered, so she must be offerable again — a local
      // "already added" set would have hidden her for the rest of the carnival.
      const createItem = (await db.outbox.toArray()).find((i) => i.kind === "entry.create")!;
      await db.outbox.put({
        ...createItem,
        status: "rejected",
        serverResponse: { error: "STUDENT_NOT_ELIGIBLE", detail: "Not in this division." },
      });

      await waitFor(() => expect(addButtons()).toHaveLength(3));
      expect(screen.getByText(/not added/)).toBeTruthy();
    });

    it("lets a refused add go as a DISCARD, never a delete", async () => {
      // ADR-0025 §2: the outbox is append-only, and a discarded capture never
      // reaches the server — so this row is the only evidence it existed.
      const { searchEligibleStudents } = await import("../api/resources");
      vi.mocked(searchEligibleStudents).mockResolvedValue(roster);

      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      await screen.findByText("Ana Aardvark");
      fireEvent.click(addButtons()[0]!);
      await screen.findByText(/adding/);
      const created = (await db.outbox.toArray()).find((i) => i.kind === "entry.create")!;
      await db.outbox.put({
        ...created,
        status: "rejected",
        serverResponse: { error: "STUDENT_NOT_ELIGIBLE", detail: "Not in this division." },
      });
      await screen.findByText(/not added/);
      // Two of her: the refused deck row, and her place back on the list.
      await waitFor(() => expect(screen.getAllByText("Ana Aardvark")).toHaveLength(2));

      fireEvent.click(screen.getByRole("button", { name: "Dismiss" }));

      await waitFor(async () =>
        expect((await db.outbox.get(created.id))?.status).toBe("discarded"),
      );
      const stored = await db.outbox.get(created.id);
      expect(stored?.resolvedBy).toBe("official-1");
      expect(stored?.actorSubject).toBe("official-1");
      expect(stored?.resolvedAt).toMatch(/^\d{4}-\d{2}-\d{2}T/);
      // The phantom row goes with it — a discarded add never became an entry,
      // so it must not leave a capture form for a swimmer who is not entered.
      await waitFor(() => expect(screen.getAllByText("Ana Aardvark")).toHaveLength(1));
      expect(screen.queryByText(/not added/)).toBeNull();
      expect(addButtons()).toHaveLength(3);
    });

    it("keeps ONE row per student when a refused add is made again", async () => {
      // The day-of sequence: the add comes back refused, the official fixes
      // whatever the server objected to and adds the swimmer again. Two queued
      // creates for the same student must not become two rows — the stale
      // refusal beside its own replacement is a phantom entry on the deck.
      const { searchEligibleStudents } = await import("../api/resources");
      vi.mocked(searchEligibleStudents).mockResolvedValue(roster);

      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      await screen.findByText("Ana Aardvark");

      fireEvent.click(addButtons()[0]!);
      await screen.findByText(/adding/);
      const refused = (await db.outbox.toArray()).find((i) => i.kind === "entry.create")!;
      await db.outbox.put({
        ...refused,
        status: "rejected",
        serverResponse: { error: "STUDENT_NOT_ELIGIBLE", detail: "Not in this division." },
      });
      await screen.findByText(/not added/);
      await waitFor(() => expect(addButtons()).toHaveLength(3));

      // Added again: a second entry.create for st-a, with a newer UUIDv7.
      fireEvent.click(addButtons()[0]!);

      // One row, and it is the fresh capture — "adding…", not "not added". The
      // superseded refusal is still in the outbox, which is the audit surface.
      await screen.findByText(/adding/);
      expect(screen.getAllByText("Ana Aardvark")).toHaveLength(1);
      expect(screen.queryByText(/not added/)).toBeNull();
      const creates = (await db.outbox.toArray()).filter((i) => i.kind === "entry.create");
      expect(creates).toHaveLength(2);
    });

    it("warns that a pre-metadata saved snapshot may be incomplete, quoting no page size", async () => {
      // A roster row written before truncation was recorded: it cannot vouch for
      // its own completeness, so it is treated as capped — and the line must not
      // print a blank where the page size would go.
      const { searchEligibleStudents } = await import("../api/resources");
      await db.eventRosters.put({
        carnivalEventId: "ev-1",
        students: roster,
        fetchedAt: "2026-08-01T00:00:00.000Z",
      });
      vi.mocked(searchEligibleStudents).mockRejectedValue(new TypeError("Failed to fetch"));

      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      await screen.findByText("Ana Aardvark");
      expect(screen.getByText(/saved roster may be incomplete — type to narrow/i)).toBeTruthy();
      expect(screen.queryByText(/showing the first/i)).toBeNull();
    });

    it("says the SAME of a capped snapshot that does know its page size", async () => {
      // Offline the filter runs over the snapshot, so quoting the server's page
      // size and inviting more typing promises swimmers this device cannot reach.
      const { searchEligibleStudents } = await import("../api/resources");
      await saveCachedRoster("ev-1", roster, { truncated: true, limit: 50 });
      vi.mocked(searchEligibleStudents).mockRejectedValue(new TypeError("Failed to fetch"));

      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      await screen.findByText("Ana Aardvark");
      expect(screen.getByText(/saved roster may be incomplete — type to narrow/i)).toBeTruthy();
      expect(screen.queryByText(/showing the first/i)).toBeNull();
      expect(screen.queryByText(/Keep typing to narrow/i)).toBeNull();
    });
  });

  it("shows a conflict prominently when the server refused the queued submission", async () => {
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), {
      target: { value: "45.10" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await screen.findByText("pending");

    // Simulate the worker mapping a 409 to conflict (ADR-0002).
    const item = (await db.outbox.toArray())[0]!;
    await db.outbox.put({
      ...item,
      status: "conflict",
      serverResponse: { error: "This entry already has a result." },
    });

    await waitFor(() => {
      expect(screen.getByText(/This entry already has a result/)).toBeTruthy();
    });
    expect(screen.getByText("conflict")).toBeTruthy();
  });

  it("hands the row back when a parked capture is let go from the outbox", async () => {
    // The discard happens on the outbox screen, but the DECK is where the
    // swimmer's real time gets typed. A row still owned by an abandoned capture
    // is a swimmer nobody can record until retention prunes it a week later.
    const { container } = renderWithQuery(
      <EventResultsScreen carnivalId="c-1" eventId="ev-1" />,
    );
    await screen.findByText("Sam Swimmer");
    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), {
      target: { value: "45.10" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await screen.findByText("pending");

    const item = (await db.outbox.toArray())[0]!;
    await db.outbox.put({
      ...item,
      status: "conflict",
      serverResponse: { error: "RESULT_EXISTS", detail: "Already recorded." },
    });
    await screen.findByText("conflict");
    await discard(item.id, "official-1");

    // An empty capture form, and nothing left claiming the slot.
    await waitFor(() => expect(screen.getByLabelText(/Time for Sam Swimmer/)).toBeTruthy());
    expect(screen.queryByText("45.10")).toBeNull();
    expect(container.querySelector(".deck-progress")?.textContent).toContain("0 of 1 recorded");
  });

  it("shows an unreadable-body refusal in the client's own words, not the server's", async () => {
    // ADR-0011: REQUEST_INVALID's `detail` is written for whoever reads a log.
    // The person reading this row is standing at the pool.
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), {
      target: { value: "45.10" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await screen.findByText("pending");

    const item = (await db.outbox.toArray())[0]!;
    await db.outbox.put({
      ...item,
      status: "conflict",
      serverResponse: {
        error: "REQUEST_INVALID",
        detail: "Check that the body is valid JSON and matches the documented shape.",
      },
    });

    await waitFor(() => {
      expect(screen.getByText(/still held on this device/)).toBeTruthy();
    });
    expect(screen.queryByText(/valid JSON/)).toBeNull();
    expect(screen.queryByText(/REQUEST_INVALID/)).toBeNull();
    // The captured time keeps its row rather than vanishing with the refusal.
    expect(screen.getByText("45.10")).toBeTruthy();
  });

  it("falls back to the saved roster when the search fails while navigator says online", async () => {
    // The pool case: the AP is up (navigator.onLine === true) but the uplink is
    // dead, so the search rejects. Cached roster must still be searchable.
    const { searchEligibleStudents } = await import("../api/resources");
    await saveCachedRoster("ev-1", [
      { studentId: "st-late", displayName: "Late Swimmer", yearLevel: 7, houseCode: "WATTLE", ageGroup: "U13" },
    ]);
    vi.mocked(searchEligibleStudents).mockRejectedValue(new TypeError("Failed to fetch"));

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(navigator.onLine).toBe(true);

    fireEvent.change(screen.getByLabelText("Search eligible students"), {
      target: { value: "late" },
    });

    expect(await screen.findByText("Late Swimmer")).toBeTruthy();
    expect(screen.getByText("saved roster")).toBeTruthy(); // honest badge, not an empty list
    expect(screen.getByRole("button", { name: "Add" })).toBeTruthy();
  });

  it("writes the same status from the menu as the keyboard path types (ADR-0041 §2)", async () => {
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    // The menu item fires the capture form's own submit, so it lands in the
    // same canonical status as typing "DNF" into the time field does.
    rowAction("Sam Swimmer", "DNF");
    await screen.findByText("pending");

    const items = await db.outbox.toArray();
    expect(items).toHaveLength(1);
    expect((items[0]!.payload as ResultSubmitPayload).status).toBe("Dnf");
    expect((items[0]!.payload as ResultSubmitPayload).value).toBeNull();
  });

  it("undo cancels the queued capture before the worker picks it up (U3)", async () => {
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    rowAction("Sam Swimmer", "DNS");
    await screen.findByText("pending");

    fireEvent.click(await screen.findByRole("button", { name: /^Undo/ }));

    // Kept as evidence, but withdrawn — and the row is ready for entry again.
    await waitFor(async () => {
      expect((await db.outbox.toArray())[0]!.status).toBe("cancelled");
    });
    await waitFor(() => {
      expect(screen.queryByLabelText(/Time for Sam Swimmer/)).not.toBeNull();
    });
    expect(screen.queryByText("pending")).toBeNull();
  });

  it("undo after the item synced opens the correction path instead of cancelling", async () => {
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), { target: { value: "45.10" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await screen.findByText("pending");

    // The worker drained it while the undo window was still open: the server
    // has the result (its id IS the client UUIDv7), so undo can only correct.
    const item = (await db.outbox.toArray())[0]!;
    await db.outbox.put({
      ...item,
      status: "accepted",
      serverResponse: { id: item.id, currentRevision: 1 },
    });
    // Applied while the window is still open: the state slot says so in a word
    // ("Saved"), and Undo is still the action beside it.
    await screen.findByText("Saved");

    fireEvent.click(screen.getByRole("button", { name: /^Undo/ }));

    // Correction form, prefilled with what was captured; nothing was withdrawn.
    await screen.findByRole("button", { name: "Save correction" });
    expect((screen.getByLabelText(/Time for Sam Swimmer/) as HTMLInputElement).value).toBe("45.10");
    expect((await db.outbox.get(item.id))?.status).toBe("accepted");
  });

  it("carries the save state in the row's status cluster, not in a line below it (U3 v2)", async () => {
    // The owner's complaint: a "Saved — Undo" line appeared under the row for
    // five seconds and then vanished, shunting every row below it down and back
    // up mid-capture. The state now lives in the row's own right-hand cluster.
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    const row = (await screen.findByText("Sam Swimmer")).closest("li")!;

    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), { target: { value: "34.56" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    // The optimistic row and the undo window are two separate arrivals — the
    // outbox's live read, then the form's own callback — so the badge can be on
    // screen a tick before Undo is. Wait for the later one; it cannot render at
    // all until the queued item has landed, so it implies the badge too.
    const undo = await screen.findByRole("button", { name: /^Undo/ });

    // Time, state and action in one cluster: the pending badge (the sync badge
    // idiom, unchanged) and Undo where Edit will be.
    const cluster = row.querySelector<HTMLElement>(".row-status")!;
    expect(within(cluster).getByLabelText("Result for Sam Swimmer").textContent).toBe("34.56");
    expect(within(cluster).getByText("pending")).toBeTruthy();
    expect(cluster.contains(undo)).toBe(true);
    expect(within(cluster).queryByRole("button", { name: "Edit" })).toBeNull();

    // Nothing was added below the row…
    expect(row.querySelector(".undo-window")).toBeNull();
    // …and the confirmation that line used to double as is still announced.
    expect(row.querySelector('[role="status"]')!.textContent).toMatch(
      /^Saved Sam Swimmer's result/,
    );
  });

  it("settles the cluster from Undo to Edit when the window closes (U3 v2)", async () => {
    // Fake timers so the 5s window is a jump, not a wait; `shouldAdvanceTime`
    // keeps the outbox's own async work moving underneath it.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      const row = (await screen.findByText("Sam Swimmer")).closest("li")!;
      rowAction("Sam Swimmer", "DNS");
      const undo = await screen.findByRole("button", { name: /^Undo/ });

      const cluster = row.querySelector<HTMLElement>(".row-status")!;
      expect(cluster.contains(undo)).toBe(true);

      await act(async () => {
        await vi.advanceTimersByTimeAsync(5_000);
      });

      // Same cluster element, different contents: the swap happens inside the
      // reserved slots, so nothing above or below the row is disturbed.
      expect(row.querySelector(".row-status")).toBe(cluster);
      expect(within(cluster).queryByRole("button", { name: /^Undo/ })).toBeNull();
      expect(within(cluster).getByRole("button", { name: "Edit" })).toBeTruthy();
      expect(within(cluster).getByText("pending")).toBeTruthy(); // the capture itself is untouched
      expect(row.querySelector('[role="status"]')!.textContent).toBe("");
    } finally {
      vi.useRealTimers();
    }
  });

  it("separates a swimmer's name from their division line (the deck row read 'Sam SwimmerBUNJ')", async () => {
    // Placed in a race, which is what a lane means — so the sub-line carries
    // nothing but the identity fields this test is about.
    const { listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockResolvedValue([race("r-1", [["en-1", "ev-1"]], [3])]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    const identity = (await screen.findByText("Sam Swimmer")).closest(".row-main")!;

    // Two elements, and neither may swallow the other's text: they are adjacent
    // inline spans with no separator in the markup (JSX drops the newline), so
    // the identity block stacks them — see styles.test.ts for that rule.
    expect(identity.querySelector(".row-title")!.textContent).toBe("Sam Swimmer");
    // The lane leads the row as its own block (ADR-0041 §3); what stays on this
    // line is the identity, and it still must not run into the name above.
    expect(identity.querySelector(".row-sub")!.firstElementChild!.textContent).toBe(
      "BANK · Yr 7 · heat 1",
    );
    expect(identity.closest(".entry-row")!.querySelector(".lane-number")!.textContent).toBe("3");
  });

  it("queues a correction offline as result.correct carrying the base revision", async () => {
    const { listResults } = await import("../api/resources");
    vi.mocked(listResults).mockResolvedValue([recorded]);
    setOnline(false);

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(screen.getByLabelText("Result for Sam Swimmer").textContent).toBe("1:23.45");

    // Edit-in-place: the same capture form, prefilled with the recorded time.
    fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
    expect((screen.getByLabelText(/Time for Sam Swimmer/) as HTMLInputElement).value).toBe("1:23.45");
    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), { target: { value: "1:20.00" } });
    fireEvent.click(screen.getByRole("button", { name: "Save correction" }));

    await waitFor(async () => {
      expect(await db.outbox.count()).toBe(1);
    });
    const item = (await db.outbox.toArray())[0]!;
    expect(item.kind).toBe("result.correct");
    // The concurrency token the official was looking at, not a drain-time re-read.
    expect(item.baseVersion).toBe(2);
    const payload = item.payload as ResultCorrectPayload;
    expect(payload).toMatchObject({ resultId: "res-1", entryId: "en-1", value: "1:20.00", status: null });
    expect(payload.reason).toBeTruthy(); // §9.3: a correction is never reasonless
    // Optimistic display: the corrected value with its own sync badge.
    expect(screen.getByLabelText("Result for Sam Swimmer").textContent).toBe("1:20.00");
    expect(screen.getByText("pending")).toBeTruthy();
  });

  it("sends an online correction straight to the API and reports a lost concurrency race", async () => {
    const { correctResult, listResults } = await import("../api/resources");
    vi.mocked(listResults).mockResolvedValue([recorded]);
    const { ApiError } = await import("../api/client");
    vi.mocked(correctResult).mockRejectedValue(
      new ApiError(
        409,
        { error: "CONCURRENCY_CONFLICT", detail: "changed since read", currentRevision: 3 },
        "/api/v1/results/res-1/correct",
      ),
    );

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), { target: { value: "1:20.00" } });
    fireEvent.click(screen.getByRole("button", { name: "Save correction" }));

    expect(await screen.findByText(/changed elsewhere/)).toBeTruthy();
    expect(vi.mocked(correctResult)).toHaveBeenCalledWith("res-1", expect.objectContaining({
      expectedRevision: 2,
      value: "1:20.00",
      status: null,
    }));
    // A refused correction is the server's answer — it is never also queued.
    expect(await db.outbox.count()).toBe(0);
  });

  /**
   * ADR-0056 §4: a Height division is the card grid, and everything the deck's
   * header owns — title, pickers, progress, Add entry — is untouched by it.
   */
  it("captures a high jump as the card grid, not as entry rows", async () => {
    const { getCarnivalEvent, listCarnivalEvents } = await import("../api/resources");
    const laddered: CarnivalEvent = {
      ...event,
      measureType: "Height",
      ladder: { start: 1.2, step: 0.05 },
      heights: [1.2, 1.25],
    };
    vi.mocked(getCarnivalEvent).mockResolvedValue(laddered);
    vi.mocked(listCarnivalEvents).mockResolvedValue([laddered, sibling]);
    const { container } = renderWithQuery(
      <EventResultsScreen carnivalId="c-1" eventId="ev-1" />,
    );

    expect(
      await screen.findByRole("button", { name: "Mark Sam Swimmer at 1.25 m, attempt 1 of 3" }),
    ).toBeTruthy();
    expect(screen.getByRole("group", { name: "Divisions" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Raise bar to 1.30 m" })).toBeTruthy();
    // The entry-row deck is gone with it, and heats never surfaced (ADR-0053 §6).
    expect(container.querySelector(".entry-row")).toBeNull();
    expect(screen.queryByRole("group", { name: "Heats" })).toBeNull();
  });

  /** ADR-0056 amendment 2026-09-25 (c): a card whose last mark came off. */
  it("counts a cleared high-jump card as not recorded", async () => {
    const { getCarnivalEvent, listCarnivalEvents, listResults } = await import("../api/resources");
    const laddered: CarnivalEvent = {
      ...event,
      measureType: "Height",
      ladder: { start: 1.2, step: 0.05 },
      heights: [1.2, 1.25],
    };
    vi.mocked(getCarnivalEvent).mockResolvedValue(laddered);
    vi.mocked(listCarnivalEvents).mockResolvedValue([laddered, sibling]);
    vi.mocked(listResults).mockResolvedValue([
      { ...recorded, status: "Nm", measuredValue: null, jumps: null },
    ]);
    const { container } = renderWithQuery(
      <EventResultsScreen carnivalId="c-1" eventId="ev-1" />,
    );

    expect(
      await screen.findByRole("button", { name: "Mark Sam Swimmer at 1.20 m, attempt 1 of 3" }),
    ).toBeTruthy();
    expect(container.querySelector(".deck-progress")?.textContent).toContain("0 of 1 recorded");
  });

  /**
   * The 2026-09-17 amendment: the TYPE is the mode. A Height division with no
   * suggestions on its definition is still the card grid, and the raise control
   * simply has nothing to pre-fill.
   */
  it("is still the card grid on a Height division carrying no suggestions", async () => {
    const { getCarnivalEvent, listCarnivalEvents } = await import("../api/resources");
    const bare: CarnivalEvent = {
      ...event,
      measureType: "Height",
      ladder: null,
      heights: [1.2, 1.25],
    };
    vi.mocked(getCarnivalEvent).mockResolvedValue(bare);
    vi.mocked(listCarnivalEvents).mockResolvedValue([bare, sibling]);
    const { container } = renderWithQuery(
      <EventResultsScreen carnivalId="c-1" eventId="ev-1" />,
    );

    expect(
      await screen.findByRole("button", { name: "Mark Sam Swimmer at 1.25 m, attempt 1 of 3" }),
    ).toBeTruthy();
    expect(container.querySelector(".entry-row")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Raise the bar" }));
    const field = (await screen.findByLabelText("New height")) as HTMLInputElement;
    expect(field.value).toBe("");
    expect(field.placeholder).toBe("e.g. 1.20");
  });

  /** And a Distance division keeps the ADR-0053 attempts strip on entry rows. */
  it("keeps the attempt series on a Distance division", async () => {
    const { getCarnivalEvent, listCarnivalEvents } = await import("../api/resources");
    const field: CarnivalEvent = { ...event, measureType: "Distance", attemptCount: 3 };
    vi.mocked(getCarnivalEvent).mockResolvedValue(field);
    vi.mocked(listCarnivalEvents).mockResolvedValue([field, sibling]);
    const { container } = renderWithQuery(
      <EventResultsScreen carnivalId="c-1" eventId="ev-1" />,
    );

    expect(await screen.findByLabelText("Attempt 1 mark for Sam Swimmer")).toBeTruthy();
    expect(container.querySelector(".jump-grid")).toBeNull();
    expect(screen.queryByRole("button", { name: /the bar/ })).toBeNull();
  });

  it("marks rows unknown (never 'no result yet') when the result list failed to load", async () => {
    const { listResults } = await import("../api/resources");
    vi.mocked(listResults).mockRejectedValue(new Error("Network error"));

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    // The failure is surfaced…
    await waitFor(() => {
      expect(screen.getAllByRole("alert").some((el) => el.textContent?.includes("Network error"))).toBe(true);
    });
    // …and the row shows an unknown state instead of an entry form, so nobody
    // re-enters a time that already exists (→ 409 for an administrator to resolve).
    expect(screen.getByLabelText("Result for Sam Swimmer").textContent).toContain("unknown");
    expect(screen.queryByLabelText(/Time for Sam Swimmer/)).toBeNull();
    expect(screen.queryByRole("button", { name: "Save" })).toBeNull();
  });
});

/**
 * ADR-0039 §1–§2, ADR-0042 §2: the heat is the context. One picker is the
 * axis, the deck shows ONE heat, and an add names it — minting it first where
 * it does not exist yet. Nothing guesses a race, so no `entry.assign` is chained.
 */
describe("EventResultsScreen heat picker and adding in context", () => {
  const late: EligibleStudent = {
    studentId: "st-late",
    displayName: "Late Swimmer",
    yearLevel: 7,
    houseCode: "WATTLE",
    ageGroup: "U13",
  };

  async function offerLateSwimmer() {
    const { searchEligibleStudents } = await import("../api/resources");
    vi.mocked(searchEligibleStudents).mockResolvedValue([late]);
  }

  const assigns = async () => (await db.outbox.toArray()).filter((i) => i.kind === "entry.assign");
  const creates = async () => (await db.outbox.toArray()).filter((i) => i.kind === "entry.create");
  const mints = async () => (await db.outbox.toArray()).filter((i) => i.kind === "race.materialise");

  /** Two heats of this division, in the carnival's own order. */
  async function twoHeats() {
    const { listRaces } = await import("../api/resources");
    const first = { ...race("r-1", [["en-1", "ev-1"]], [3], "ev-1"), sortOrder: 0 };
    const second = { ...race("r-2", [], [], "ev-1"), sortOrder: 1 };
    vi.mocked(listRaces).mockResolvedValue([first, second]);
  }

  function heatAxis() {
    return within(screen.getByRole("group", { name: "Heats" }));
  }

  it("leaves the DIVISION as the only thing claiming to be the page", async () => {
    // Two axes, one page: the heat is a modifier on the division's route, so a
    // second aria-current="page" would put two answers on screen to "where am I".
    await twoHeats();
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByRole("button", { name: /^Heat 2/ });
    openHeats();
    expect(pickerItem(/^Heat 2/).getAttribute("aria-current")).toBe("true");
    expect(document.querySelectorAll("[aria-current='page']")).toHaveLength(0);

    closeMenu();
    openPicker("Age group 13");
    expect(document.querySelectorAll("[aria-current='page']")).toHaveLength(1);
  });

  it("offers one item per heat, numbered within the DIVISION, plus the mint", async () => {
    await twoHeats();
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByRole("button", { name: /^Heat 2/ });
    openHeats();
    await waitFor(() => expect(pickerItem(/^Heat 1/)).toBeTruthy());
    expect(pickerItem(/^Heat 2/)).toBeTruthy();
    expect(screen.queryByRole("menuitem", { name: /^Heat 3/ })).toBeNull();
    expect(pickerItem(/^New heat/)).toBeTruthy();
  });

  it("counts each heat's own recorded rows — on the closed button and the items", async () => {
    const { listResults } = await import("../api/resources");
    await twoHeats();
    vi.mocked(listResults).mockResolvedValue([recorded]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    // Sam is heat 1's only member and holds a result; heat 2 is empty and is
    // the one on screen. The arithmetic and wording are the selector's (B3),
    // and the CURRENT heat's count stays readable with the menu shut.
    const trigger = await screen.findByRole("button", { name: /^Heat 2/ });
    await waitFor(() => expect(within(trigger).getByText("0 entered")).toBeTruthy());
    openHeats();
    expect(within(pickerItem(/^Heat 1/)).getByText("1/1").className).toContain("mono");
  });

  it("counts a queued capture on its heat, so the axis is honest offline", async () => {
    // ADR-0014 §1's truth rule, which the division picker above already applies:
    // "8/8" on the division must never sit over "0/8" on its only heat.
    await twoHeats();
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), {
      target: { value: "1:23.45" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    await waitFor(() =>
      expect(screen.getByRole("button", { name: /^Heat 1 1\/1/ })).toBeTruthy(),
    );
  });

  it("counts a swimmer mid-move on ONE heat — the one they are going to", async () => {
    // The row SHOWS in both heats until the read lands (§2), so counting it the
    // same way would report one swimmer twice across the division.
    await twoHeats();
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    const picker = lanePicker("Sam Swimmer");
    const intoHeat2 = [
      ...[...picker.querySelectorAll("optgroup")]
        .find((g) => g.label === "Heat 2")!
        .querySelectorAll("option"),
    ].find((o) => o.textContent === "Lane 1")!.value;
    fireEvent.change(picker, { target: { value: intoHeat2 } });
    await pastArmingLockout();
    fireEvent.click(screen.getByRole("button", { name: /^Confirm moving/ }));
    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    await waitFor(() =>
      expect(screen.getByRole("button", { name: /^Heat 1 0 entered/ })).toBeTruthy(),
    );
    openHeats();
    expect(screen.getAllByRole("menuitem", { name: /0\/1$/ })).toHaveLength(1);
  });

  it("says the count is UNKNOWN on every heat when the results read failed (M13)", async () => {
    // "0/1" there is a claim this device cannot make, and a heat called
    // complete off its own queued captures is the same claim the other way.
    const { listResults } = await import("../api/resources");
    await twoHeats();
    vi.mocked(listResults).mockRejectedValue(new TypeError("Failed to fetch"));
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);

    const trigger = await screen.findByRole("button", { name: /^Heat 1/ });
    await waitFor(() => expect(within(trigger).getByText("?/1")).toBeTruthy());
    openHeats();
    expect(within(pickerItem(/^Heat 1/)).getByText("?/1")).toBeTruthy();
    expect(pickerItem(/^Heat 1/).querySelector(".chip-tick")).toBeNull();
  });

  it("reads a SOCIAL division's heat as 'n entered' — no time is owed there", async () => {
    const { getCarnivalEvent, listCarnivalEvents } = await import("../api/resources");
    const social = { ...event, effectiveScoringMode: "Social" as const };
    vi.mocked(getCarnivalEvent).mockResolvedValue(social);
    vi.mocked(listCarnivalEvents).mockResolvedValue([social, sibling]);
    await twoHeats();
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await waitFor(() => expect(heatAxis().getByText("1 entered")).toBeTruthy());
  });

  it("defaults to the LAST heat — the one being filled", async () => {
    await twoHeats();
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("No swimmers yet — add one below.");
    // Heat 2 is empty, so Sam (heat 1) is not on the deck at all.
    expect(screen.queryByText("Sam Swimmer")).toBeNull();
    openHeats();
    expect(pickerItem(/^Heat 1/).tagName).toBe("A");
  });

  it("shows the heat the URL names, and falls back to the last for one it does not", async () => {
    await twoHeats();
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    cleanup();
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-deleted";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("No swimmers yet — add one below.");
  });

  it("mints a heat on the New heat item and selects it", async () => {
    await twoHeats();
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    openHeats();
    fireEvent.click(pickerItem(/^New heat/));
    await waitFor(async () => expect(await mints()).toHaveLength(1));
    const minted = (await mints())[0]!;
    // The URL is the selection (§1), and the queued heat sorts last.
    await waitFor(() => expect(location.hash).toContain(`heat=${minted.id}`));
    await screen.findByText("No swimmers yet — add one below.");
    await waitFor(() =>
      expect(screen.getByRole("button", { name: /^Heat 3/ })).toBeTruthy(),
    );
  });

  it("holds the mint dead while one of this device's own is unresolved, and says why", async () => {
    await twoHeats();
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByRole("button", { name: /^Heat 2/ });
    openHeats();
    fireEvent.click(pickerItem(/^New heat/));
    await waitFor(async () => expect(await mints()).toHaveLength(1));

    // The reason is visible text INSIDE the dead item (review B5, ADR-0042 §2),
    // and a second tap on it does nothing.
    openHeats();
    const add = await screen.findByRole("menuitem", { name: /already queued on this device/ });
    expect(add.getAttribute("aria-disabled")).toBe("true");
    fireEvent.click(add);
    expect(await mints()).toHaveLength(1);
  });

  it("holds the mint dead while the races read is unresolved (the standing mint gate)", async () => {
    const { listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockRejectedValue(new Error("Network error"));
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    // Nothing resolved to stand at, so the closed button says exactly that
    // rather than reading as a bare axis, and offers the mint alone.
    fireEvent.click(await screen.findByRole("button", { name: "No heat" }));
    const add = await screen.findByRole("menuitem", { name: /^New heat/ });
    await waitFor(() => expect(add.getAttribute("aria-disabled")).toBe("true"));
    expect(add.textContent).toContain("Waiting for this division's heats");
    // The visible name carries the word in its accessible name (WCAG 2.5.3).
    expect(add.textContent).toContain("New heat");
  });

  it("adds into the SELECTED heat, naming it on the create", async () => {
    await offerLateSwimmer();
    await twoHeats();
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Late Swimmer");
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    await screen.findByText(/adding/);

    await waitFor(async () => expect(await creates()).toHaveLength(1));
    expect((await creates())[0]!.payload).toMatchObject({ raceId: "r-1" });
    // ADR-0018 §7 is retired: the create names its heat, nothing chains.
    expect(await assigns()).toHaveLength(0);
  });

  it("shows the new row in the selected heat at once, and nowhere else", async () => {
    await offerLateSwimmer();
    await twoHeats();
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Late Swimmer");
    // Heat 2 is selected by default and is empty.
    await screen.findByText("No swimmers yet — add one below.");
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    // The optimistic row belongs to the heat its create named — at once, before
    // the create has been anywhere near the API.
    await screen.findByText(/adding/);
    expect(screen.getByText("Late Swimmer")).toBeTruthy();
    expect(screen.queryByText("Sam Swimmer")).toBeNull();
  });

  it("mints the heat FIRST and creates into it, in that order (the prospective heat)", async () => {
    const { listRaces } = await import("../api/resources");
    await offerLateSwimmer();
    vi.mocked(listRaces).mockResolvedValue([]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Late Swimmer");
    // A division with no race at all still has a heat to stand at.
    expect(heatAxis().getByText("Heat 1")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    await waitFor(async () => expect(await creates()).toHaveLength(1));
    const mint = (await mints())[0]!;
    const create = (await creates())[0]!;
    // FIFO by capture-time UUIDv7: the mint drains first, so the create names a
    // race that exists by the time it lands.
    expect(mint.id < create.id).toBe(true);
    expect(create.payload).toMatchObject({ raceId: mint.id });
    expect(await assigns()).toHaveLength(0);
  });

  it("mints ONE heat for two adds on a prospective heat", async () => {
    const { listRaces, searchEligibleStudents } = await import("../api/resources");
    vi.mocked(listRaces).mockResolvedValue([]);
    vi.mocked(searchEligibleStudents).mockResolvedValue([
      late,
      { ...late, studentId: "st-late-2", displayName: "Later Swimmer" },
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Late Swimmer");
    // Both taps land BEFORE the first mint resolves — the window the outbox
    // live query cannot have closed yet, which is the one this must survive.
    fireEvent.click(screen.getAllByRole("button", { name: "Add" })[0]!);
    fireEvent.click(screen.getAllByRole("button", { name: "Add" })[0]!);
    await waitFor(async () => expect(await creates()).toHaveLength(2));

    expect(await mints()).toHaveLength(1);
    const mint = (await mints())[0]!;
    for (const create of await creates())
      expect(create.payload).toMatchObject({ raceId: mint.id });
  });

  it("names no heat, and mints nothing, while the races read is unresolved", async () => {
    // A mint off an unknown read could duplicate a race that exists (the
    // standing mint gate); the server's own placement is the honest fallback.
    const { listRaces } = await import("../api/resources");
    await offerLateSwimmer();
    vi.mocked(listRaces).mockRejectedValue(new Error("Network error"));

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    expect(await screen.findByText(/Marshalling is paused until the race list loads/)).toBeTruthy();
    fireEvent.click(await screen.findByRole("button", { name: "Add" }));
    await waitFor(async () => expect(await creates()).toHaveLength(1));
    expect((await creates())[0]!.payload).not.toMatchObject({ raceId: expect.anything() });
    expect(await mints()).toHaveLength(0);
    expect(await assigns()).toHaveLength(0);
  });

  /** A materialisation of this division's, parked as the server refused it. */
  async function refusedMint() {
    const mint = await submitMaterialiseViaOutbox({
      carnivalId: "c-1",
      eventId: "ev-1",
      title: "13 · F",
      actorSubject: "official-1",
    });
    await db.outbox.put({
      ...(await db.outbox.get(mint.id))!,
      status: "conflict",
      serverResponse: { error: "CARNIVAL_NOT_OPEN", detail: "not open" },
    });
    return mint;
  }

  it("refuses to add into a heat the server turned down, and says so (review B1)", async () => {
    // Every add on a refused chip would queue behind a race that will never
    // exist — RACE_NOT_IN_EVENT, parked, with Requeue re-sending the dead id.
    const { listRaces } = await import("../api/resources");
    await offerLateSwimmer();
    vi.mocked(listRaces).mockResolvedValue([]);
    await refusedMint();

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Late Swimmer");
    const add = screen.getByRole("button", { name: "Add" }) as HTMLButtonElement;
    expect(add.disabled).toBe(true);
    // Visible, not a title: the reason has to be readable on a tablet.
    expect(
      screen.getAllByText(/The server refused this heat — clear it from the Outbox first/).length,
    ).toBeGreaterThan(0);
    // …and "add one below" is never printed under a refusal.
    expect(screen.queryByText("No swimmers yet — add one below.")).toBeNull();

    fireEvent.click(add);
    await new Promise((r) => setTimeout(r, 20));
    expect(await creates()).toHaveLength(0);
  });

  it("leaves an add queued behind a refused mint unplaced, with no lane to pick (M9)", async () => {
    const { listRaces } = await import("../api/resources");
    await offerLateSwimmer();
    vi.mocked(listRaces).mockResolvedValue([]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Late Swimmer");
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    await waitFor(async () => expect(await creates()).toHaveLength(1));
    const mint = (await mints())[0]!;
    await db.outbox.put({
      ...(await db.outbox.get(mint.id))!,
      status: "conflict",
      serverResponse: { error: "CARNIVAL_NOT_OPEN", detail: "not open" },
    });
    await waitFor(() =>
      expect(screen.getAllByText(/The server refused this heat/).length).toBeGreaterThan(0),
    );

    const picker = lanePicker("Late Swimmer");
    const lanes = [...picker.querySelectorAll("option")].map((o) => o.textContent ?? "");
    expect(lanes.filter((t) => t.startsWith("Lane"))).toEqual([]);
    fireEvent.change(picker, { target: { value: "1" } });
    await new Promise((r) => setTimeout(r, 20));
    expect(await assigns()).toHaveLength(0);
  });

  it("stops naming a discarded mint on every later add (review A3)", async () => {
    const { listRaces, searchEligibleStudents } = await import("../api/resources");
    vi.mocked(listRaces).mockResolvedValue([]);
    vi.mocked(searchEligibleStudents).mockResolvedValue([
      late,
      { ...late, studentId: "st-late-2", displayName: "Later Swimmer" },
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Late Swimmer");
    fireEvent.click(screen.getAllByRole("button", { name: "Add" })[0]!);
    await waitFor(async () => expect(await mints()).toHaveLength(1));
    const mint = (await mints())[0]!;

    // The mint is refused and then let go from the outbox: the id it held names
    // a race the server will never have, and the ref must not keep serving it.
    await db.outbox.put({ ...(await db.outbox.get(mint.id))!, status: "conflict" });
    await discard(mint.id, "official-1");
    await waitFor(async () => expect((await db.outbox.get(mint.id))!.status).toBe("discarded"));

    fireEvent.click(screen.getAllByRole("button", { name: "Add" })[0]!);
    await waitFor(async () => expect(await mints()).toHaveLength(2));
    const second = (await mints()).find((i) => i.id !== mint.id)!;
    const created = (await creates()).find(
      (i) => (i.payload as EntryCreatePayload).studentId === "st-late-2",
    )!;
    expect((created.payload as EntryCreatePayload).raceId).toBe(second.id);
  });

  it("never reorders the heats — a legacy mismatch relabels one in place", async () => {
    // ADR-0039 amendment: heat numbers follow the race ordinal, so the items are
    // stable. Data from before it can still disagree (here r-1 holds heat 2 and
    // r-2 holds heat 1) — the item then says which pool it really is, and STAYS
    // in the division's running order rather than swapping under the marshal.
    const { listEntries, listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockResolvedValue([
      { ...race("r-1", [["en-1", "ev-1"]], [1], "ev-1"), sortOrder: 0 },
      { ...race("r-2", [["en-2", "ev-1"]], [1], "ev-1"), sortOrder: 1 },
    ]);
    vi.mocked(listEntries).mockResolvedValue([
      { ...entry, id: "en-1", heat: 2, raceId: "r-1" },
      { ...entry, id: "en-2", studentId: "st-2", heat: 1, raceId: "r-2", displayName: "Tessa Fourteen" },
    ]);
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-2";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Tessa Fourteen");
    // The selected heat is r-2, which holds heat 1 and says so.
    await waitFor(() => expect(screen.getByRole("button", { name: /^Heat 1/ })).toBeTruthy());
    openHeats();
    expect(pickerItem(/^Heat 1/).getAttribute("aria-current")).toBe("true");
    const labels = heatItems().map((i) => i.textContent);
    expect(labels[0]).toContain("Heat 2"); // r-1, first by sortOrder
    expect(labels[1]).toContain("Heat 1"); // r-2, second — the label moved, not the item
  });

  it("keeps the heat order when a swimmer is added to a heat added a moment ago", async () => {
    // The owner's report: adding into a new heat shuffled the row. Nothing the
    // deck learns about a heat's members may move an item.
    await twoHeats();
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    const order = () => heatItems().map((i) => i.textContent!.split(" ")[1]).slice(0, 3);

    openHeats();
    fireEvent.click(pickerItem(/^New heat/));
    await waitFor(async () => expect(await mints()).toHaveLength(1));
    openHeats();
    await waitFor(() => expect(order()).toEqual(["1", "2", "3"]));
    closeMenu();

    // The server places the add and mints the heat: under the old fill-order
    // rule it came back as heat 1 and this chip jumped to the front.
    const minted = (await mints())[0]!;
    const { listEntries, listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockResolvedValue([
      { ...race("r-1", [["en-1", "ev-1"]], [3], "ev-1"), sortOrder: 0 },
      { ...race("r-2", [], [], "ev-1"), sortOrder: 1 },
      { ...race(minted.id, [["en-late", "ev-1"]], [1], "ev-1"), sortOrder: 2 },
    ]);
    vi.mocked(listEntries).mockResolvedValue([
      { ...entry, heat: 1, lane: 3, raceId: "r-1" },
      {
        ...entry,
        id: "en-late",
        studentId: "st-late",
        displayName: "Late Comer",
        heat: 1,
        lane: 1,
        raceId: minted.id,
      },
    ]);
    await db.outbox.put({ ...minted, status: "accepted" });
    await screen.findByText("Late Comer");
    openHeats();
    expect(order()).toEqual(["1", "2", "1"]); // the label is honest…
    const hrefs = heatItems().map((i) => i.getAttribute("href"));
    expect(hrefs[0]).toContain("heat=r-1"); // …and the item has not moved
    expect(hrefs[1]).toContain("heat=r-2");
  });

  it("renders and counts rows the SERVER already placed, with nothing in the outbox", async () => {
    // The ordinary steady state since ADR-0039 §1: no create is raceless, so
    // every row arrives stamped with its race and its heat, in a race the races
    // read lists. What shows and what is counted must be the same set.
    const { listEntries, listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockResolvedValue([
      { ...race("r-1", [["en-1", "ev-1"], ["en-2", "ev-1"]], [1, 2], "ev-1"), sortOrder: 0 },
    ]);
    vi.mocked(listEntries).mockResolvedValue([
      { ...entry, id: "en-1", heat: 1, lane: 1, raceId: "r-1" },
      {
        ...entry,
        id: "en-2",
        studentId: "st-2",
        heat: 1,
        lane: 2,
        raceId: "r-1",
        displayName: "Tessa Fourteen",
      },
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(screen.getByText("Tessa Fourteen")).toBeTruthy();
    expect(screen.queryByText("No swimmers yet — add one below.")).toBeNull();
    // One heat, holding both, counted on the closed button the deck is showing.
    expect(heatAxis().getAllByText(/^Heat /)).toHaveLength(1);
    expect(heatAxis().getByText("0/2")).toBeTruthy();
    expect(await db.outbox.toArray()).toHaveLength(0);
  });

  it("offers an item per heat off the ENTRIES read when the races read is down (review B2)", async () => {
    const { listEntries, listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockRejectedValue(new Error("Network error"));
    vi.mocked(listEntries).mockResolvedValue([
      { ...entry, id: "en-1", heat: 1, raceId: "r-1" },
      { ...entry, id: "en-2", studentId: "st-2", heat: 2, raceId: "r-2", displayName: "Tessa Fourteen" },
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    // Two heats, not one prospective "Heat 1" claiming the whole division.
    await screen.findByRole("button", { name: /^Heat 2/ });
    openHeats();
    expect(pickerItem(/^Heat 1/)).toBeTruthy();
    expect(pickerItem(/^Heat 2/)).toBeTruthy();
    closeMenu();
    // The last heat is the default, and it holds its own member only.
    expect(screen.getByText("Tessa Fourteen")).toBeTruthy();
    expect(screen.queryByText("Sam Swimmer")).toBeNull();
  });

  it("shows a row whose heat exists nowhere under the FIRST heat (review A2c)", async () => {
    // A stamp naming a race in neither read nor the outbox — a discarded mint,
    // a deleted race. The row belongs somewhere; nowhere is not an option.
    const { listEntries } = await import("../api/resources");
    await twoHeats();
    vi.mocked(listEntries).mockResolvedValue([
      { ...entry, id: "en-1", heat: null, raceId: "r-gone", displayName: "Lost Swimmer" },
    ]);
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    expect(await screen.findByText("Lost Swimmer")).toBeTruthy();
  });

  it("writes the default heat into the address, so Back off a mint goes somewhere (B6)", async () => {
    await twoHeats();
    location.hash = "/carnivals/c-1/events/ev-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByRole("group", { name: "Heats" });
    await waitFor(() => expect(location.hash).toBe("#/carnivals/c-1/events/ev-1?heat=r-2"));
  });

  it("renders LEGACY raceless rows under the FIRST heat, with no place badge", async () => {
    // Pre-ADR data and API callers only (§3): the row is in no heat, so it is
    // not in the field being ranked.
    const { listEntries, listResults } = await import("../api/resources");
    await twoHeats();
    vi.mocked(listEntries).mockResolvedValue([
      { ...entry, id: "en-loose", heat: null, lane: null, raceId: null, displayName: "Loose End" },
      { ...entry, raceId: "r-1" },
    ]);
    vi.mocked(listResults).mockResolvedValue([{ ...recorded, entryId: "en-loose", place: 1 }]);
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Loose End");
    expect(screen.queryByText("1st")).toBeNull();

    // …and not under the second heat.
    cleanup();
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-2";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("No swimmers yet — add one below.");
    expect(screen.queryByText("Loose End")).toBeNull();
  });

  it("mints NOTHING off a races read that failed — the read is unknown, not empty", async () => {
    // A failed read looks exactly like a division with no race, and the server
    // has no duplicate-race guard: minting off it splits a division across two
    // races, which is two heats where there should be one.
    const { listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockRejectedValue(new Error("Network error"));

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    const control = lanePicker("Sam Swimmer");
    expect(control.disabled).toBe(true);
    // Belt and braces: even driven directly, nothing is queued.
    fireEvent.change(control, { target: { value: "2" } });
    await new Promise((r) => setTimeout(r, 20));
    expect(await db.outbox.toArray()).toHaveLength(0);
  });

  it("offers the mint the moment the read RESOLVES to no races", async () => {
    const { listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockResolvedValue([]);

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(lanePicker("Sam Swimmer").disabled).toBe(false);
  });
});


/**
 * The lane, edited where it is displayed (ADR-0020 §2). The assign-lanes mode
 * is gone: a row's static "lane 3" is now the control that changes it, and the
 * time · state · action cluster beside it is untouched.
 */
describe("EventResultsScreen inline lane control", () => {
  async function withRaces(rows: ReturnType<typeof race>[]) {
    const { listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockResolvedValue(rows);
  }

  const laneSelect = lanePicker;

  const assigns = async () => (await db.outbox.toArray()).filter((i) => i.kind === "entry.assign");

  it("sits on the metadata line: vacant lanes, the swimmer's own, and the taken one as a swap", async () => {
    const { listEntries } = await import("../api/resources");
    vi.mocked(listEntries).mockResolvedValue([
      entry,
      { ...entry, id: "en-2", studentId: "st-2", displayName: "Tessa Fourteen", heat: 1, lane: 4 },
    ]);
    await withRaces([race("r-1", [["en-1", "ev-1"], ["en-2", "ev-1"]], [3, 4])]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    // laneCount 6: lane 3 is Sam's own and stays selectable; lane 4 is Ria's and
    // is offered only as a named swap, never as a bare lane (ADR-0030 §1).
    expect(
      [...laneSelect("Sam Swimmer").querySelectorAll("option")].map((o) => o.textContent),
    ).toEqual([
      "No lane",
      "Lane 1",
      "Lane 2",
      "Lane 3",
      "Lane 5",
      "Lane 6",
      // Named from the RACES read, the one source that holds an occupant this
      // division's entries list may not (a legacy multi-event race).
      "Lane 4 — swap with Tessa Fourteen",
      "Remove from race…",
    ]);
    // The capture cluster is where it was: the picker never joined it.
    const row = screen.getByText("Sam Swimmer").closest("li")!;
    expect(row.querySelector(".row-main .lane-select")).toBeTruthy();
    expect(row.querySelector(".row-status .lane-select, .entry-form .lane-select")).toBeNull();
  });

  it("moves a swimmer as one entry.assign, with the heat left to the server", async () => {
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "5" } });
    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    expect((await assigns())[0]!.payload).toMatchObject({
      carnivalEventId: "ev-1",
      entryId: "en-1",
      raceId: "r-1",
      lane: 5,
      // Never typed here: the server derives it (ADR-0015 §5).
      heat: null,
    });
  });

  it("materialises the division's race on the first pick, then places", async () => {
    // ADR-0018 §4: race rows are materialised lazily, and the marshal holds
    // that right — never through race.create, which is administrator-only.
    await withRaces([]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "2" } });
    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    const items = await db.outbox.toArray();
    const materialised = items.find((i) => i.kind === "race.materialise")!;
    const assign = items.find((i) => i.kind === "entry.assign")!;
    expect(items.some((i) => i.kind === "race.create")).toBe(false);
    // FIFO: the placement drains behind the materialisation and names the race
    // id its own item id will become.
    expect(assign.id > materialised.id).toBe(true);
    expect(assign.payload).toMatchObject({ raceId: materialised.id, lane: 2, heat: null });
  });

  it("mints ONE race however fast the taps come (R3)", async () => {
    await withRaces([]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    // Two picks of one fumbled gesture, in the same tick — the state flag they
    // would both have read is still false, and two races is a swimmer alone in
    // an orphan nobody but an administrator can delete.
    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "1" } });
    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "2" } });

    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    expect((await db.outbox.toArray()).filter((i) => i.kind === "race.materialise")).toHaveLength(1);
  });

  it("keeps marshalling into a race that exists only in this device's queue", async () => {
    await withRaces([]);
    const queued = await submitMaterialiseViaOutbox({
      carnivalId: "c-1",
      eventId: "ev-1",
      title: "13 · F",
      actorSubject: "official-1",
    });
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    // The queued materialisation IS the race, so nothing is minted twice.
    expect(screen.queryByText(/Assigning the first lane creates/)).toBeNull();

    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "1" } });
    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    expect((await db.outbox.toArray()).filter((i) => i.kind === "race.materialise")).toHaveLength(1);
    expect((await assigns())[0]!.payload).toMatchObject({ raceId: queued.id, lane: 1 });
  });

  it("takes two taps to remove, and clears race, lane and heat together", async () => {
    // Removal is scoring-relevant, so it never fires from the dropdown itself.
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "__remove" } });
    expect(await assigns()).toHaveLength(0);

    const armed = await screen.findByRole("button", {
      name: "Confirm removing Sam Swimmer from this race",
    });
    // Armed reads differently, and IGNORES the second half of a double-tap: on a
    // wet deck that gesture is one tap, and a guard it satisfies guards nothing.
    expect(armed.className).toContain("armed");
    fireEvent.click(armed);
    expect(await assigns()).toHaveLength(0);

    await pastArmingLockout();
    fireEvent.click(armed);
    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    expect((await assigns())[0]!.payload).toMatchObject({ raceId: null, lane: null, heat: null });
  });

  it("lets the armed strip be cancelled without removing anyone", async () => {
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "__remove" } });
    fireEvent.click(await screen.findByRole("button", { name: "Cancel" }));
    await waitFor(() =>
      expect(screen.queryByRole("button", { name: /^Confirm removing/ })).toBeNull(),
    );
    expect(await assigns()).toHaveLength(0);
    // …and the picker snapped back to the lane the swimmer is still in.
    expect(laneSelect("Sam Swimmer").value).toBe("3");
  });

  it("says so, and stays put, when the device queue refuses the placement", async () => {
    // Storage evicted, quota, private mode: nothing was captured anywhere, and
    // silence there is a swimmer in a race no device can see.
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);
    location.hash = "#/carnivals/c-1/events/ev-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    const failing = vi.spyOn(db.outbox, "add").mockRejectedValue(new Error("QuotaExceededError"));
    try {
      fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "2" } });
      const alert = await screen.findByRole("alert");
      expect(alert.textContent).toContain("Couldn't queue lane 2 on this device");
      expect(await db.outbox.toArray()).toHaveLength(0);
      // The default heat is written into the address (review B6), and a refused
      // placement moves nobody off it.
      expect(location.hash).toBe("#/carnivals/c-1/events/ev-1?heat=r-1");
    } finally {
      failing.mockRestore();
    }
  });

  it("keeps an accepted placement on the row until the read catches up (R4)", async () => {
    // The races read still shows the swimmer unlaned, and their lane must not
    // blink away and back.
    await withRaces([race("r-1", [["en-1", "ev-1"]])]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    const placed = await submitAssignmentViaOutbox({
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      raceId: "r-1",
      lane: 4,
      entryDisplayName: "Sam Swimmer",
      actorSubject: "official-1",
    });
    // Stamped NOW, which is after the read the render above already waited for.
    await db.outbox.put({
      ...(await db.outbox.get(placed.id))!,
      status: "accepted",
      syncedAt: new Date().toISOString(),
    });

    await waitFor(() => expect(laneNumber("Sam Swimmer")).toBe("4"));
  });

  it("re-orders the deck on the lane just picked, not the one the read still has", async () => {
    // Offline the entries read never refreshes, so the deck's own "heat → lane"
    // promise held only until someone marshalled from it: a race stayed in
    // entry order for the whole morning, and save-and-advance walked it so.
    const { listEntries } = await import("../api/resources");
    vi.mocked(listEntries).mockResolvedValue([
      { ...entry, heat: null, lane: null },
      {
        ...entry,
        id: "en-2",
        studentId: "st-2",
        displayName: "Ada Ames",
        heat: null,
        lane: null,
      },
    ]);
    await withRaces([race("r-1", [["en-1", "ev-1"], ["en-2", "ev-1"]])]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Ada Ames");
    const order = () => [...document.querySelectorAll(".deck .row-title")].map((n) => n.textContent);
    // Unlaned rows sit in CAPTURE order (ADR-0039 §2) — outbox/entry ids are
    // UUIDv7, so en-1 precedes en-2 whatever the names say.
    expect(order()).toEqual(["Sam Swimmer", "Ada Ames"]);

    fireEvent.change(laneSelect("Ada Ames"), { target: { value: "1" } });
    await waitFor(() => expect(order()).toEqual(["Ada Ames", "Sam Swimmer"]));
  });

  it("offers BOTH heats of the division, grouped, to a row in neither (ADR-0038 §3)", async () => {
    // The two-race dead end: the row used to lose its control entirely and be
    // sent to a disclosure. Every heat's free lanes are on the row now, and the
    // group says which HEAT a lane belongs to (ADR-0039 §2).
    await withRaces([
      race("r-1", [["en-other", "ev-1"]], [1]),
      { ...race("r-2", [["en-more", "ev-1"]], [1]), sortOrder: 1, label: "Race 2" },
    ]);
    // A raceless row renders under the first heat (§3), so that is where it is.
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    const control = laneSelect("Sam Swimmer");
    await waitFor(() =>
      expect([...control.querySelectorAll("optgroup")].map((g) => g.label)).toEqual([
        "Heat 1",
        "Heat 2",
      ]),
    );
    // A pick in the second heat names it: the placement must not go to the one
    // the deck happened to list first.
    const lane = [...control.querySelectorAll("optgroup")]
      .find((g) => g.label === "Heat 2")!
      .querySelector("option")!;
    fireEvent.change(control, { target: { value: lane.value } });
    // A pick in another race changes the placing pool, so it ARMS (ADR-0038
    // review): nothing is queued until the strip is confirmed.
    expect(await assigns()).toHaveLength(0);
    await pastArmingLockout();
    fireEvent.click(screen.getByRole("button", { name: /^Confirm moving/ }));
    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    expect((await assigns())[0]!.payload).toMatchObject({
      entryId: "en-1",
      raceId: "r-2",
      lane: 2,
      heat: null,
    });
  });

  it("offers a swimmer who has SWUM no race but their own (ADR-0037 review fix 1)", async () => {
    // A placing is scored against the pool of the race it was swum in, so a
    // one-tap move to another race silently re-ranks two heats. Leaving a race
    // they have swum in goes through the confirmed Remove and nothing else.
    const { listResults } = await import("../api/resources");
    vi.mocked(listResults).mockResolvedValue([recorded]);
    await withRaces([
      race("r-1", [["en-1", "ev-1"]], [3]),
      { ...race("r-2", [["en-2", "ev-1"]], [1]), sortOrder: 1, label: "Race 2" },
    ]);
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    const moves = () =>
      [...laneSelect("Sam Swimmer").querySelectorAll("option")].filter((o) =>
        o.value.startsWith("__move:"),
      );
    await waitFor(() => expect(screen.getByLabelText("Result for Sam Swimmer")).toBeTruthy());
    expect(moves()).toEqual([]);
    // Their own race is still fully editable — lane, swaps and Remove.
    expect(
      [...laneSelect("Sam Swimmer").querySelectorAll("option")].map((o) => o.textContent),
    ).toContain("Remove from race…");
  });

  it("still offers every heat to a RESULTED swimmer who is in NONE of them", async () => {
    // The fence is about moving between placing pools; a swimmer in no race is
    // not moving between any, and withholding it would be the two-race dead end
    // ADR-0038 §3 exists to kill.
    const { listResults } = await import("../api/resources");
    vi.mocked(listResults).mockResolvedValue([recorded]);
    await withRaces([
      race("r-1", [["en-2", "ev-1"]], [1]),
      { ...race("r-2", [["en-3", "ev-1"]], [1]), sortOrder: 1, label: "Race 2" },
    ]);
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    await waitFor(() =>
      expect(
        [...laneSelect("Sam Swimmer").querySelectorAll("optgroup")].map((g) => g.label),
      ).toEqual(["Heat 1", "Heat 2"]),
    );
  });

  it("offers no lane control at all when nobody is signed in", async () => {
    // ADR-0006: the actor is captured on every placement, at capture time.
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    act(() => signOut());
    // The row's menu stays — a status is still typable — but the lane is not
    // in it: placing is an actor-stamped act.
    fireEvent.click(screen.getByRole("button", { name: "Actions for Sam Swimmer" }));
    expect(screen.queryByRole("menuitem", { name: "Change lane" })).toBeNull();
    // …and the lane is still legible, because the row still leads with it.
    expect(screen.getByText("Sam Swimmer").closest("li")!.querySelector(".lane-number")!.textContent)
      .toBe("3");
  });
});

/**
 * What the selected heat still says for itself (ADR-0039 §2): a refused mint,
 * a race the program lost, an unreadable lane count — and the "New heat" item
 * that mints the next one, under ADR-0037's gates.
 */
describe("EventResultsScreen the selected heat's banners and the mint", () => {
  async function withRaces(rows: ReturnType<typeof race>[]) {
    const { listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockResolvedValue(rows);
  }

  const laneSelect = lanePicker;

  /** The value of a lane offered under a named heat group of a row's picker. */
  const laneIn = (name: string, group: string, lane: number) =>
    [
      ...[...laneSelect(name).querySelectorAll("optgroup")]
        .find((g) => g.label === group)!
        .querySelectorAll("option"),
    ].find((o) => o.textContent === `Lane ${lane}`)!.value;

  const kinds = async (kind: string) =>
    (await db.outbox.toArray()).filter((i) => i.kind === kind);

  it("offers the minted heat's lanes on a row of the heat it is leaving", async () => {
    // Minting selects the new heat, but the swimmers being SPLIT into it are on
    // the old one — so the move is made from their row, as ADR-0038 §3 has it,
    // with the group naming the heat.
    await withRaces([race("r-1", [["en-1", "ev-1"]], [1], "ev-1")]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    openHeats();
    fireEvent.click(pickerItem(/^New heat/));
    await waitFor(async () => expect(await kinds("race.materialise")).toHaveLength(1));
    // Back to the heat Sam is in — the new one is selected by the tap.
    cleanup();
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    await waitFor(() =>
      expect([...laneSelect("Sam Swimmer").querySelectorAll("optgroup")].map((g) => g.label)).toEqual(
        ["Heat 1", "Heat 2"],
      ),
    );

    fireEvent.change(laneSelect("Sam Swimmer"), {
      target: { value: laneIn("Sam Swimmer", "Heat 2", 1) },
    });
    // Another heat is another placing pool: the move is confirmed, never one tap.
    await pastArmingLockout();
    fireEvent.click(screen.getByRole("button", { name: /^Confirm moving/ }));
    await waitFor(async () => expect(await kinds("entry.assign")).toHaveLength(1));
    const minted = (await kinds("race.materialise"))[0]!;
    expect((await kinds("entry.assign"))[0]!.payload).toMatchObject({
      entryId: "en-1",
      raceId: minted.id,
      lane: 1,
      heat: null,
    });
  });

  it("keeps a row visible in the heat it is LEAVING until the read lands", async () => {
    // ADR-0039 §2: the row visibly leaves, it does not vanish under the finger.
    await withRaces([
      race("r-1", [["en-1", "ev-1"]], [1], "ev-1"),
      { ...race("r-2", [], [], "ev-1"), sortOrder: 1, label: "Race 2" },
    ]);
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    fireEvent.change(laneSelect("Sam Swimmer"), {
      target: { value: laneIn("Sam Swimmer", "Heat 2", 1) },
    });
    await pastArmingLockout();
    fireEvent.click(screen.getByRole("button", { name: /^Confirm moving/ }));
    await waitFor(async () => expect(await kinds("entry.assign")).toHaveLength(1));
    // Still here, with the placement's own badge on the row.
    expect(screen.getByText("Sam Swimmer")).toBeTruthy();
    await waitFor(() => expect(document.querySelector(".lane-control .sync-badge")).toBeTruthy());

    // …and gone once the races read agrees with the queue.
    cleanup();
    await db.outbox.clear();
    await withRaces([
      { ...race("r-1", [], [], "ev-1") },
      { ...race("r-2", [["en-1", "ev-1"]], [1], "ev-1"), sortOrder: 1, label: "Race 2" },
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("No swimmers yet — add one below.");
    expect(screen.queryByText("Sam Swimmer")).toBeNull();
  });

  it("holds the mint dead on a division with no entries (ADR-0038 review A6)", async () => {
    // A heat minted for nobody is litter no marshal can clear — the deck has no
    // delete, so an empty division must not be able to mint one.
    const { listEntries } = await import("../api/resources");
    vi.mocked(listEntries).mockResolvedValue([]);
    await withRaces([]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("No entries yet — add students below.");
    openHeats();
    const add = pickerItem(/^New heat/);
    expect(add.getAttribute("aria-disabled")).toBe("true");
    expect(add.textContent).toContain("Add a swimmer first");
  });

  it("holds the mint dead, and says so, while a heat of this device's stands REFUSED", async () => {
    await withRaces([race("r-1", [["en-1", "ev-1"]], [1], "ev-1")]);
    const refused = await submitMaterialiseViaOutbox({
      carnivalId: "c-1",
      eventId: "ev-1",
      title: "13 · F",
      actorSubject: "official-1",
    });
    await db.outbox.put({
      ...(await db.outbox.get(refused.id))!,
      status: "conflict",
      serverResponse: { error: "CARNIVAL_NOT_OPEN", detail: "not open" },
    });

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    // Said in the add panel AND inside the dead item: both controls are dead.
    await screen.findByText(/The server refused this heat — clear it from the Outbox first/);
    openHeats();
    const add = pickerItem(/^New heat/);
    expect(add.getAttribute("aria-disabled")).toBe("true");
    expect(
      screen.getAllByText(/The server refused this heat — clear it from the Outbox first/),
    ).toHaveLength(2);
  });

  it("keeps an EMPTY landed race on the heat axis by its STAMP", async () => {
    // The steady state after the drain: the mint is accepted and the races read
    // carries the race with nobody in it. The stamp is what finds it (ADR-0038)
    // — this device's own outbox item is no longer needed to attribute one.
    await withRaces([
      race("r-1", [["en-1", "ev-1"]], [1], "ev-1"),
      { ...race("r-2", [], [], "ev-1"), sortOrder: 1, label: "Race 2" },
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByRole("button", { name: /^Heat 2/ });
    // …and it is the one on screen, empty, which is the state it is filled from.
    expect(await screen.findByText("No swimmers yet — add one below.")).toBeTruthy();
  });

  it("falls back to a typical pool when the venue has no lane count", async () => {
    // A lane that is not offered is a lane a marshal cannot fill.
    const { getCarnivalSettings } = await import("../api/resources");
    vi.mocked(getCarnivalSettings).mockResolvedValue({ laneCount: null } as never);
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    await waitFor(() =>
      expect(
        [...laneSelect("Sam Swimmer").querySelectorAll("option")].map((o) => o.textContent),
      ).toEqual([
        "No lane",
        "Lane 1",
        "Lane 2",
        "Lane 3",
        "Lane 4",
        "Lane 5",
        "Lane 6",
        "Lane 7",
        "Lane 8",
        "Remove from race…",
      ]),
    );
  });

  it("keeps the heat standing while the ack outruns the races read (R4)", async () => {
    // The materialisation is accepted, the drain has invalidated, and the
    // refetch is still in flight. Dropping the heat here took the swimmer just
    // placed back to "this division has no race".
    const { listRaces } = await import("../api/resources");
    let reads = 0;
    // The REFETCH the acceptance triggers never lands: that in-flight window is
    // exactly the state under test.
    vi.mocked(listRaces).mockImplementation(() =>
      reads++ === 0 ? Promise.resolve([]) : new Promise(() => {}),
    );
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    const minted = await submitMaterialiseViaOutbox({
      carnivalId: "c-1",
      eventId: "ev-1",
      title: "13 · F",
      actorSubject: "official-1",
    });
    await db.outbox.put({
      ...(await db.outbox.get(minted.id))!,
      status: "accepted",
      syncedAt: new Date().toISOString(),
    });

    // The outbox live query has to reach the screen before the pick means
    // anything — there is no other observable for "the heat is standing".
    await act(async () => {
      await new Promise((r) => setTimeout(r, 100));
    });
    // The heat exists, so a lane pick PLACES rather than minting a second race.
    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "2" } });
    await waitFor(async () => expect(await kinds("entry.assign")).toHaveLength(1));
    expect(await kinds("race.materialise")).toHaveLength(1);
    expect((await kinds("entry.assign"))[0]!.payload).toMatchObject({ raceId: minted.id });
  });

  it("lets a stale acknowledgement go once the read has caught up (G2)", async () => {
    // A week-old accepted materialisation for a race an administrator has
    // deleted must not resurrect it for the whole retention window: the next
    // lane pick mints a heat of its own.
    await withRaces([]);
    const old = await submitMaterialiseViaOutbox({
      carnivalId: "c-1",
      eventId: "ev-1",
      title: "13 · F",
      actorSubject: "official-1",
    });
    await db.outbox.put({
      ...(await db.outbox.get(old.id))!,
      status: "accepted",
      syncedAt: new Date(Date.now() - 7 * 24 * 3600_000).toISOString(),
    });

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "2" } });
    await waitFor(async () => expect(await kinds("race.materialise")).toHaveLength(2));
  });

  it("says so on the heat when the server refused its race (R5)", async () => {
    await withRaces([]);
    const refused = await submitMaterialiseViaOutbox({
      carnivalId: "c-1",
      eventId: "ev-1",
      title: "13 · F",
      actorSubject: "official-1",
    });
    await db.outbox.put({
      ...(await db.outbox.get(refused.id))!,
      status: "conflict",
      serverResponse: { error: "CARNIVAL_NOT_OPEN", detail: "not open" },
    });

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(
      await screen.findByText(/The server refused this heat, so nothing here has been marshalled/),
    ).toBeTruthy();
  });

  it("banners a race that asked for a program block it never got", async () => {
    // The keyed degrade (ADR-0015 phase 2): the block was tidied away while the
    // race sat in the queue, so it landed unscheduled. The only record of what
    // was intended is the outbox item whose id IS the race id.
    await withRaces([race("r-mint", [["en-1", "ev-1"]], [1])]);
    await db.outbox.put({
      id: "r-mint",
      kind: "race.materialise",
      payload: {
        carnivalId: "c-1",
        eventId: "ev-1",
        scheduleSlotId: "slot-1",
        label: null,
        display: { title: "13 · F" },
      },
      actorSubject: "official-1",
      capturedAt: "2026-08-17T09:00:00.000Z",
      status: "accepted",
      attempts: 1,
    });

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(await screen.findByText(/asked for FREESTYLE EVENTS/)).toBeTruthy();
    expect(screen.getByText(/not on the program/)).toBeTruthy();
  });

  it("says nothing about the program for a race that asked for no block", async () => {
    await withRaces([race("r-1", [["en-1", "ev-1"]], [1])]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(screen.queryByText(/asked for/)).toBeNull();
  });

  it("draws NO heat divider for one heat, and one where a legacy race holds two", async () => {
    // One heat per view (ADR-0039 §2), so the divider is silent — except in a
    // legacy race carrying two heats of a division, which must still render.
    const { listEntries } = await import("../api/resources");
    vi.mocked(listEntries).mockResolvedValue([
      { ...entry, heat: 1, lane: 1 },
      { ...entry, id: "en-2", studentId: "st-2", displayName: "Una Waiting", heat: 1, lane: 2 },
    ]);
    await withRaces([race("r-1", [["en-1", "ev-1"], ["en-2", "ev-1"]], [1, 2], "ev-1")]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Una Waiting");
    expect(document.querySelectorAll(".heat-divider")).toHaveLength(0);

    cleanup();
    vi.mocked(listEntries).mockResolvedValue([
      { ...entry, heat: 1, lane: 1 },
      { ...entry, id: "en-2", studentId: "st-2", displayName: "Una Waiting", heat: 2, lane: 2 },
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Una Waiting");
    expect([...document.querySelectorAll(".heat-divider")].map((d) => d.textContent)).toEqual([
      "Heat 2",
    ]);
  });
});

/**
 * The consolidated review fixes on the flat deck (ADR-0020): what must not be
 * marshallable, what must be, and the states that used to have no way out.
 */
describe("EventResultsScreen marshalling guards", () => {
  async function withRaces(rows: Race[]) {
    const { listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockResolvedValue(rows);
  }

  async function withEntries(rows: Entry[]) {
    const { listEntries } = await import("../api/resources");
    vi.mocked(listEntries).mockResolvedValue(rows);
  }

  async function withLaneCount(laneCount: number | null) {
    const { getCarnivalSettings } = await import("../api/resources");
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      laneCount,
      scoring: null,
      adjustmentCategories: [],
    } as never);
  }

  const laneSelect = lanePicker;
  const assigns = async () => (await db.outbox.toArray()).filter((i) => i.kind === "entry.assign");
  const options = (name: string) =>
    [...laneSelect(name).querySelectorAll("option")].map((o) => o.textContent);
  /** The value of a lane offered under a named race group of a row's picker. */
  const laneIn = (name: string, group: string, lane: number) =>
    [
      ...[...laneSelect(name).querySelectorAll("optgroup")]
        .find((g) => g.label === group)!
        .querySelectorAll("option"),
    ].find((o) => o.textContent === `Lane ${lane}`)!.value;

  const swimmer = (n: number, displayName: string, over: Partial<Entry> = {}): Entry => ({
    ...entry,
    id: `en-${n}`,
    studentId: `st-${n}`,
    displayName,
    heat: null,
    lane: null,
    ...over,
  });

  it("marshals a TEAM division — a relay occupies a lane like anyone", async () => {
    // The row-only door plus a team guard left every relay division with no
    // marshalling surface at all; the server's placement has no team rule.
    const { getCarnivalEvent } = await import("../api/resources");
    const teamEvent = { ...event, format: "Team", teamSize: 4, legs: ["1", "2", "3", "4"] };
    vi.mocked(getCarnivalEvent).mockResolvedValue(teamEvent as never);
    await withEntries([
      {
        ...entry,
        id: "en-t1",
        studentId: null,
        displayName: "BANK Relay",
        heat: null,
        lane: null,
        isTeam: true,
        teamHouseCode: "BANK",
      } as Entry,
    ]);
    await withRaces([]);

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("BANK Relay");

    fireEvent.change(laneSelect("BANK Relay"), { target: { value: "2" } });
    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    const materialised = (await db.outbox.toArray()).find((i) => i.kind === "race.materialise")!;
    expect(materialised).toBeTruthy();
    expect((await assigns())[0]!.payload).toMatchObject({
      entryId: "en-t1",
      raceId: materialised.id,
      lane: 2,
    });
  });

  it("advances save-and-advance in the order the deck is RENDERED", async () => {
    // The flat sort puts every heat-less row at the end together; the page puts
    // the race's own unlaned rows inside the race and the unraced last. Saving
    // used to jump the focus across that gap, past the row on screen next.
    await withEntries([
      swimmer(1, "Sam Swimmer", { heat: 1, lane: 1 }),
      swimmer(2, "Zoe Zed"),
      swimmer(3, "Ada Ames"),
    ]);
    await withRaces([race("r-1", [["en-1", "ev-1"], ["en-2", "ev-1"]], [1, null])]);

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Ada Ames");
    // Rendered: the selected heat in LANE order, its unlaned rows last.
    expect([...document.querySelectorAll(".deck .row-title")].map((n) => n.textContent)).toEqual([
      "Sam Swimmer",
      "Zoe Zed",
      "Ada Ames",
    ]);

    fireEvent.change(screen.getByLabelText(/Time for Sam Swimmer/), { target: { value: "34.12" } });
    fireEvent.click(
      within(screen.getByText("Sam Swimmer").closest("li")!).getByRole("button", { name: "Save" }),
    );
    await waitFor(() =>
      expect(document.activeElement).toBe(screen.getByLabelText(/Time for Zoe Zed/)),
    );
  });

  it("keeps a row's lane on screen when the races read cannot speak for it", async () => {
    // Marshalling is paused, but the entries read carries heat and lane: a row
    // reading "Lane —" beside a swimmer standing in lane 4 is the deck lying.
    const { listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockRejectedValue(new TypeError("Failed to fetch"));
    await withEntries([swimmer(1, "Sam Swimmer", { heat: 1, lane: 4 })]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    await waitFor(() => expect(laneNumber("Sam Swimmer")).toBe("4"));
  });

  it("orders the heat by the entries read's lanes while the races read is down (M9)", async () => {
    const { listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockRejectedValue(new TypeError("Failed to fetch"));
    await withEntries([
      swimmer(1, "Sam Swimmer", { heat: 1, raceId: "r-1", lane: 5 }),
      swimmer(2, "Tessa Fourteen", { heat: 1, raceId: "r-1", lane: 2 }),
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    const order = () =>
      [...document.querySelectorAll(".deck .row-title")].map((n) => n.textContent);
    await waitFor(() => expect(order()).toEqual(["Tessa Fourteen", "Sam Swimmer"]));
  });

  it("keeps a heat unseen by the races read laned, offering no taken lane (M9)", async () => {
    await withRaces([]);
    await withEntries([
      swimmer(1, "Sam Swimmer", { heat: 1, raceId: "r-9", lane: 3 }),
      swimmer(2, "Tessa Fourteen", { heat: 1, raceId: "r-9", lane: 4 }),
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(laneNumber("Sam Swimmer")).toBe("3");
    expect(laneNumber("Tessa Fourteen")).toBe("4");
    await waitFor(() =>
      expect(laneSelect("Sam Swimmer").selectedOptions[0]?.textContent).toBe("Lane 3"),
    );
    expect(options("Sam Swimmer")).not.toContain("Lane 4");
    expect(options("Sam Swimmer")).toContain("Remove from race…");
  });

  it("offers no lane the entries read fills while the races read is behind it (M9)", async () => {
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3], "ev-1")]);
    await withEntries([
      swimmer(1, "Sam Swimmer", { heat: 1, raceId: "r-1", lane: 3 }),
      swimmer(2, "Tessa Fourteen", { heat: 1, raceId: "r-1", lane: 4 }),
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Tessa Fourteen");
    await waitFor(() => expect(options("Sam Swimmer")).toContain("Lane 5"));
    expect(options("Sam Swimmer")).not.toContain("Lane 4");
    expect(laneNumber("Tessa Fourteen")).toBe("4");
    expect(
      [...laneSelect("Tessa Fourteen").querySelectorAll("option")].map((o) => o.textContent),
    ).not.toContain("Lane 3");
  });

  it("sizes the pool past lanes only the entries read fills, with no count set (M9)", async () => {
    await withLaneCount(null);
    await withRaces([race("r-1", [["en-1", "ev-1"]], [null], "ev-1")]);
    await withEntries([
      swimmer(1, "Sam Swimmer", { heat: 1, raceId: "r-1" }),
      ...[1, 2, 3, 4, 5, 6, 7, 8].map((n) =>
        swimmer(n + 1, `Lane ${n} Swimmer`, { heat: 1, raceId: "r-1", lane: n }),
      ),
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    await waitFor(() => expect(options("Sam Swimmer")).toContain("Lane 9"));
    expect(options("Sam Swimmer").filter((t) => t?.startsWith("Lane "))).toEqual(["Lane 9"]);
  });

  it("mints ONE race for two rows' picks taken inside the live query's window (H5)", async () => {
    // The R3 fence catches two taps in one tick. This is the tap AFTER the
    // first has resolved, with the outbox query still a round trip behind: the
    // second pick used to mint a race of its own, splitting one division
    // between two pools that nothing in the product can merge.
    await withRaces([]);
    await withEntries([swimmer(1, "Sam Swimmer"), swimmer(2, "Una Waiting")]);
    liveOutbox.frozen = [];
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Una Waiting");

    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "1" } });
    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    fireEvent.change(laneSelect("Una Waiting"), { target: { value: "2" } });
    await waitFor(async () => expect(await assigns()).toHaveLength(2));

    const minted = (await db.outbox.toArray()).filter((i) => i.kind === "race.materialise");
    expect(minted).toHaveLength(1);
    expect((await assigns()).map((i) => (i.payload as { raceId: string }).raceId)).toEqual([
      minted[0]!.id,
      minted[0]!.id,
    ]);
  });

  it("gives a swimmer in NO heat a way in when the pool is full (R1 successor)", async () => {
    // The old door needed an unlaned row INSIDE the race; a swimmer who never
    // joined it satisfied nothing, and a full race left them with no way in.
    await withLaneCount(1);
    await withEntries([swimmer(1, "Sam Swimmer", { heat: 1, lane: 1 }), swimmer(2, "Una Waiting")]);
    await withRaces([race("r-1", [["en-1", "ev-1"]], [1], "ev-1")]);
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Una Waiting");

    // A full heat has no lane to offer, and the picker says exactly that
    // rather than being told it is full (ADR-0038 §3).
    expect(options("Una Waiting")).toEqual(["Unplaced"]);

    // The next heat, added on this or any device, is on her row at once.
    await withRaces([
      race("r-1", [["en-1", "ev-1"]], [1], "ev-1"),
      { ...race("r-2", [], [], "ev-1"), sortOrder: 1, label: "Race 2" },
    ]);
    cleanup();
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Una Waiting");
    await waitFor(() => expect(options("Una Waiting")).toContain("Lane 1"));

    fireEvent.change(laneSelect("Una Waiting"), {
      target: { value: laneIn("Una Waiting", "Heat 2", 1) },
    });
    // A pick in another heat changes the placing pool, so it ARMS (ADR-0038
    // review): nothing is queued until the strip is confirmed.
    expect(await assigns()).toHaveLength(0);
    await pastArmingLockout();
    fireEvent.click(screen.getByRole("button", { name: /^Confirm moving/ }));

    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    expect((await assigns())[0]!.payload).toMatchObject({
      entryId: "en-2",
      raceId: "r-2",
      lane: 1,
      // Never a heat number: the placing pool is the server's to derive, and
      // this first placement into the new race is what mints heat 2.
      heat: null,
    });
  });

  it("offers the added heat to a swimmer left UNLANED in the full one (ADR-0037)", async () => {
    // Materialisation swept the whole division into race 1, so every overflow
    // swimmer is already a member of it. Without the other heat's lanes on the
    // row the added heat has nobody to put in it and the division is stuck full.
    await withLaneCount(1);
    await withEntries([swimmer(1, "Sam Swimmer", { heat: 1, lane: 1 }), swimmer(2, "Una Waiting")]);
    await withRaces([
      race("r-1", [["en-1", "ev-1"], ["en-2", "ev-1"]], [1, null], "ev-1"),
      { ...race("r-2", [], [], "ev-1"), sortOrder: 1, label: "Race 2" },
    ]);
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Una Waiting");
    await waitFor(() => expect(options("Una Waiting")).toContain("Lane 1"));

    fireEvent.change(laneSelect("Una Waiting"), {
      target: { value: laneIn("Una Waiting", "Heat 2", 1) },
    });
    expect(await assigns()).toHaveLength(0);
    await pastArmingLockout();
    fireEvent.click(screen.getByRole("button", { name: /^Confirm moving/ }));

    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    expect((await assigns())[0]!.payload).toMatchObject({
      entryId: "en-2",
      raceId: "r-2",
      lane: 1,
      heat: null,
    });
  });

  it("holds the lane pickers while the venue's lane count is still loading", async () => {
    // Falling back to a typical pool silently offers lanes a six-lane venue
    // rejects, and a rejected placement parks for a human.
    const { getCarnivalSettings } = await import("../api/resources");
    vi.mocked(getCarnivalSettings).mockImplementation(() => new Promise(() => {}));
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(screen.getAllByText(/Loading the venue's lanes/).length).toBeGreaterThan(0);
    expect(laneSelect("Sam Swimmer").disabled).toBe(true);
  });

  it("states the typical-pool caveat on the block", async () => {
    // The rows are assigned from, and the lanes they offer are the same guess:
    // the caveat belongs where the marshal is already looking.
    const { getCarnivalSettings } = await import("../api/resources");
    vi.mocked(getCarnivalSettings).mockRejectedValue(new Error("Network error"));
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(
      await screen.findByText(
        /The venue's lane count couldn't be read, so this shows a typical pool/,
      ),
    ).toBeTruthy();
  });

  it("never OFFERS a lane above the venue's count, but keeps a held one selectable", async () => {
    // laneOptions widens for display so a placement above the count is still
    // shown; offering the rest of that widened range queues placements the
    // server refuses, which park until a human deals with them.
    await withEntries([
      swimmer(1, "Sam Swimmer", { heat: 1, lane: 3 }),
      swimmer(2, "Deep Ender", { heat: 1, lane: 9 }),
    ]);
    await withRaces([race("r-1", [["en-1", "ev-1"], ["en-2", "ev-1"]], [3, 9])]);

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Deep Ender");

    expect(options("Sam Swimmer")).toEqual([
      "No lane",
      "Lane 1",
      "Lane 2",
      "Lane 3",
      "Lane 4",
      "Lane 5",
      "Lane 6",
      "Remove from race…",
    ]);
    // Lane 9 is where this swimmer IS: withholding it would make the picker
    // unable to describe the row it sits on.
    expect(options("Deep Ender")).toContain("Lane 9");
    expect(options("Deep Ender")).not.toContain("Lane 7");
    // Nor as a SWAP, in either direction (ADR-0030 §1): a swap touching an
    // over-cap lane is two writes the server refuses on the lane bound alone.
    expect(options("Sam Swimmer").some((o) => o?.includes("swap"))).toBe(false);
    expect(options("Deep Ender").some((o) => o?.includes("swap"))).toBe(false);
  });

  it("names each heat on the rows' pickers when the division has two of them", async () => {
    // The deck itself carries no race headings any more (ADR-0039 §2 — one heat
    // per view, named on the heat axis); the picker is where a lane names its heat.
    await withEntries([
      swimmer(1, "Sam Swimmer", { heat: 1, lane: 1 }),
      swimmer(2, "Ria Reyes", { heat: 2, lane: 1 }),
    ]);
    await withRaces([
      race("r-1", [["en-1", "ev-1"]], [1], "ev-1"),
      { ...race("r-2", [["en-2", "ev-1"]], [1], "ev-1"), label: "Race 2", sortOrder: 1 },
    ]);
    location.hash = "/carnivals/c-1/events/ev-1?heat=r-1";

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    // Ria is in the other heat, so she is not on this deck at all.
    expect(screen.queryByText("Ria Reyes")).toBeNull();
    expect(
      [...laneSelect("Sam Swimmer").querySelectorAll("optgroup")].map((g) => g.label),
    ).toEqual(["Heat 1", "Heat 2"]);
  });

  it("carries the race's title on every queued placement, so a parked one is decidable", async () => {
    await withRaces([{ ...race("r-1", [["en-1", "ev-1"]], [3]), label: null }]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "5" } });
    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    // Positional, because the race carries no label of its own (raceGroups).
    expect((await assigns())[0]!.payload).toMatchObject({
      display: { entryDisplayName: "Sam Swimmer", raceTitle: "Race 1" },
    });
  });

  it("names the DIVISION on a placement into a race this tap is minting", async () => {
    await withRaces([]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "2" } });
    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    expect(
      ((await assigns())[0]!.payload as { display: { raceTitle?: string } }).display.raceTitle,
    ).toBe(divisionChipLabel(event));
  });

  it("says so when a second tap is refused the race it was going to mint", async () => {
    await withRaces([]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "1" } });
    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "2" } });

    expect(await screen.findByText(/Another lane tap is already creating this race/)).toBeTruthy();
    await waitFor(async () => expect(await assigns()).toHaveLength(1));
  });

  it("does not report a park without a lane as 'lane null'", async () => {
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    const failing = vi.spyOn(db.outbox, "add").mockRejectedValue(new Error("QuotaExceededError"));
    try {
      fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "" } });
      const alert = await screen.findByRole("alert");
      expect(alert.textContent).toContain("park without a lane");
      expect(alert.textContent).not.toContain("lane null");
    } finally {
      failing.mockRestore();
    }
  });

  it("drops a queue error when the division switches under it", async () => {
    // The screen re-renders in place, and an error naming the lane of another
    // division's swimmer reads as this deck's.
    const { getCarnivalEvent, listEntries } = await import("../api/resources");
    vi.mocked(getCarnivalEvent).mockImplementation((_c, id) =>
      Promise.resolve(id === "ev-2" ? sibling : event),
    );
    vi.mocked(listEntries).mockImplementation((_c, id) =>
      Promise.resolve(id === "ev-2" ? [] : [entry]),
    );
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);
    const { client, rerender } = renderWithQuery(
      <EventResultsScreen carnivalId="c-1" eventId="ev-1" />,
    );
    await screen.findByText("Sam Swimmer");

    const failing = vi.spyOn(db.outbox, "add").mockRejectedValue(new Error("QuotaExceededError"));
    try {
      fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "5" } });
      expect((await screen.findByRole("alert")).textContent).toContain("Couldn't queue lane 5");
    } finally {
      failing.mockRestore();
    }

    rerender(
      <QueryWrapper client={client}>
        <EventResultsScreen carnivalId="c-1" eventId="ev-2" />
      </QueryWrapper>,
    );
    await screen.findByText("No entries yet — add students below.");
    expect(screen.queryByText(/Couldn't queue lane 5/)).toBeNull();
  });

  it("parks a swimmer in the race with no lane, keeping the race and clearing the lane", async () => {
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "" } });
    await waitFor(async () => expect(await assigns()).toHaveLength(1));
    expect((await assigns())[0]!.payload).toMatchObject({
      raceId: "r-1",
      lane: null,
      heat: null,
    });
  });

  it("shows the venue's whole lane range for a race whose swimmers are ALL unlaned (R2)", async () => {
    // The shape of a freshly materialised race: everybody in, nobody placed.
    // Reading its lanes off the roster calls that a mass start and offers none.
    await withEntries([swimmer(1, "Sam Swimmer"), swimmer(2, "Una Waiting")]);
    await withRaces([race("r-1", [["en-1", "ev-1"], ["en-2", "ev-1"]])]);

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Una Waiting");
    await waitFor(() =>
      expect(options("Una Waiting")).toEqual([
        "No lane",
        "Lane 1",
        "Lane 2",
        "Lane 3",
        "Lane 4",
        "Lane 5",
        "Lane 6",
        "Remove from race…",
      ]),
    );
  });

  it("gives a row whose add the server refused no lane control at all", async () => {
    // Half an add: the entry does not exist, so a placement naming it can only
    // park behind it.
    const { searchEligibleStudents } = await import("../api/resources");
    vi.mocked(searchEligibleStudents).mockResolvedValue([
      {
        studentId: "st-late",
        displayName: "Late Swimmer",
        yearLevel: 7,
        houseCode: "WATTLE",
        ageGroup: "U13",
      },
    ]);
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Late Swimmer");
    fireEvent.click(screen.getByRole("button", { name: "Add" }));
    await screen.findByText(/adding/);

    const created = (await db.outbox.toArray()).find((i) => i.kind === "entry.create")!;
    await db.outbox.put({
      ...created,
      status: "rejected",
      serverResponse: { error: "STUDENT_NOT_ELIGIBLE", detail: "Not in this division." },
    });

    await screen.findByText(/not added/);
    expect(screen.queryByRole("button", { name: "Actions for Late Swimmer" })).toBeNull();
  });

  it("keeps the lane control reachable on a carnival with ONE division", async () => {
    // Nothing to switch to there — the axes only name the division — so the
    // deck row is the only door.
    const { listCarnivalEvents } = await import("../api/resources");
    vi.mocked(listCarnivalEvents).mockResolvedValue([event]);
    await withRaces([race("r-1", [["en-1", "ev-1"]], [3])]);

    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(screen.queryByRole("button", { name: /^Age group/ })).toBeNull();
    expect(document.querySelectorAll(".picker-trigger.static")).toHaveLength(2);
    expect(laneSelect("Sam Swimmer")).toBeTruthy();
  });

  /**
   * ADR-0030. Two swimmers in each other's lanes: the obvious pair of taps is
   * unsatisfiable in either order, so the swap is one confirmed gesture behind
   * three ordered placements.
   */
  describe("swapping two lanes", () => {
    const pair = async () => {
      await withEntries([
        swimmer(1, "Sam Swimmer", { heat: 1, lane: 3 }),
        swimmer(2, "Tessa Fourteen", { heat: 1, lane: 4 }),
      ]);
      await withRaces([race("r-1", [["en-1", "ev-1"], ["en-2", "ev-1"]], [3, 4])]);
    };

    const deckNames = () =>
      [...document.querySelectorAll(".deck .row-title")].map((n) => n.textContent);

    it("offers the occupied lane as a named swap, and takes two taps to fire it", async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true });
      try {
        await pair();
        renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
        await screen.findByText("Tessa Fourteen");
        expect(options("Sam Swimmer")).toContain("Lane 4 — swap with Tessa Fourteen");

        fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "__swap:en-2" } });
        expect(await assigns()).toHaveLength(0);

        await act(async () => {
          await vi.advanceTimersByTimeAsync(400);
        });
        fireEvent.click(
          screen.getByRole("button", { name: "Confirm swapping Sam Swimmer with Tessa Fourteen" }),
        );

        await waitFor(async () => expect(await assigns()).toHaveLength(3));
        // Vacate, cross, return — and the race is on every one of them, so
        // nobody's heat moves (the server derives it from the race they stay in).
        expect((await assigns()).map((i) => i.payload)).toEqual([
          expect.objectContaining({ entryId: "en-1", raceId: "r-1", lane: null, heat: null }),
          expect.objectContaining({ entryId: "en-2", raceId: "r-1", lane: 3, heat: null }),
          expect.objectContaining({ entryId: "en-1", raceId: "r-1", lane: 4, heat: null }),
        ]);
      } finally {
        vi.useRealTimers();
      }
    });

    it("shows BOTH swimmers moved before the queue drains", async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true });
      try {
        await pair();
        renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
        await screen.findByText("Tessa Fourteen");
        expect(deckNames()).toEqual(["Sam Swimmer", "Tessa Fourteen"]);

        fireEvent.change(laneSelect("Sam Swimmer"), { target: { value: "__swap:en-2" } });
        await act(async () => {
          await vi.advanceTimersByTimeAsync(400);
        });
        fireEvent.click(screen.getByRole("button", { name: /^Confirm swapping/ }));

        // The newest queued placement per entry speaks for the row, so the
        // three-write sequence renders as the finished swap, not as its middle.
        await waitFor(() => expect(laneNumber("Sam Swimmer")).toBe("4"));
        expect(laneNumber("Tessa Fourteen")).toBe("3");
        expect(deckNames()).toEqual(["Tessa Fourteen", "Sam Swimmer"]);
      } finally {
        vi.useRealTimers();
      }
    });

    it("tells the truth when the middle write parks: no lane, not a half-move", async () => {
      // ADR-0030 §5. A third device claimed the vacated lane, so writes 2 and 3
      // both park — a `conflict` does not stop the drain. The deck must show the
      // swimmer where they now are (nowhere), never in the lane they never got.
      const items = await submitSwapViaOutbox({
        carnivalId: "c-1",
        raceId: "r-1",
        moving: {
          entryId: "en-1",
          carnivalEventId: "ev-1",
          entryDisplayName: "Sam Swimmer",
          lane: 3,
        },
        occupant: {
          entryId: "en-2",
          carnivalEventId: "ev-1",
          entryDisplayName: "Tessa Fourteen",
          lane: 4,
        },
        actorSubject: "u-1",
      });
      const refused = {
        error: "LANE_TAKEN",
        detail: "Lane 3 is already taken in this race by another entry.",
      };
      await db.outbox.put({
        ...items[0]!,
        status: "accepted",
        syncedAt: new Date(0).toISOString(),
      });
      await db.outbox.put({ ...items[1]!, status: "conflict", serverResponse: refused });
      await db.outbox.put({ ...items[2]!, status: "conflict", serverResponse: refused });
      // The server as it is after write 1 alone: Sam in the race, no lane.
      await withEntries([
        swimmer(1, "Sam Swimmer", { heat: 1, lane: null }),
        swimmer(2, "Tessa Fourteen", { heat: 1, lane: 4 }),
      ]);
      await withRaces([race("r-1", [["en-1", "ev-1"], ["en-2", "ev-1"]], [null, 4])]);

      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      await screen.findByText("Tessa Fourteen");

      await waitFor(() => expect(laneNumber("Sam Swimmer")).toBe("—"));
      expect(laneNumber("Tessa Fourteen")).toBe("4");
      // Both rows carry the refusal where the tap happened (ADR-0025 §5).
      for (const name of ["Sam Swimmer", "Tessa Fourteen"])
        expect(
          screen.getByText(name).closest("li")!.querySelector(".lane-control .sync-badge.conflict"),
        ).toBeTruthy();
    });

    it("offers no swap to a swimmer with no lane — they have nothing to give", async () => {
      await withEntries([
        swimmer(1, "Sam Swimmer", { heat: 1, lane: null }),
        swimmer(2, "Tessa Fourteen", { heat: 1, lane: 4 }),
      ]);
      await withRaces([race("r-1", [["en-1", "ev-1"], ["en-2", "ev-1"]], [null, 4])]);

      renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
      await screen.findByText("Tessa Fourteen");
      expect(options("Sam Swimmer").some((o) => o?.includes("swap"))).toBe(false);
      // …and the swimmer who HAS one is offered nobody either: lane 4 is the
      // only occupied lane, and it is their own.
      expect(options("Tessa Fourteen").some((o) => o?.includes("swap"))).toBe(false);
    });
  });
});

/**
 * The placing badge on the deck (ADR-0036). The row's own suppression rules are
 * pinned in EntryRow.test.tsx; what is pinned here is what only the screen
 * knows — whether a heatless row is the field or a row outside it —
 * and the poll that lets a SECOND device's captures reach these badges at all.
 */
describe("EventResultsScreen place badges", () => {
  const heatless: Entry = { ...entry, heat: null, lane: null };
  const first: ResultDto = { ...recorded, place: 1 };

  async function deck(rows: { races: Race[] }) {
    const { listEntries, listRaces, listResults } = await import("../api/resources");
    vi.mocked(listEntries).mockResolvedValue([heatless]);
    vi.mocked(listRaces).mockResolvedValue(rows.races);
    vi.mocked(listResults).mockResolvedValue([first]);
  }

  it("badges nothing on a row in NO heat of a division that has them", async () => {
    // Legacy data only since ADR-0039 §3, and it renders under the first heat
    // rather than in the field — so a ribbon there argues with the row.
    await deck({ races: [race("r-1", [["en-2", "ev-1"]])] });
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    await waitFor(() =>
      expect(screen.getByLabelText("Result for Sam Swimmer").textContent).toBe("1:23.45"),
    );
    expect(screen.queryByText("1st")).toBeNull();
  });

  it("badges nothing on a row swept into a race but given no heat (review A5)", async () => {
    // The axis is the HEAT, not the race: the sweep's own product is in a race
    // and in no ranked field, so an ordinal on it ranks it against nobody.
    await deck({ races: [race("r-1", [["en-1", "ev-1"]], [1], "ev-1")] });
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    await waitFor(() =>
      expect(screen.getByLabelText("Result for Sam Swimmer").textContent).toBe("1:23.45"),
    );
    expect(screen.queryByText("1st")).toBeNull();
  });

  it("badges a heatless row where the division has NO races — that pool is the field", async () => {
    // Field events and never-raced divisions: the server ranks the heatless
    // field as its own, exactly as the results book prints it.
    await deck({ races: [] });
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    expect(await screen.findByText("1st")).toBeTruthy();
  });

  it("polls the deck's reads only while the server is reachable", () => {
    // Ungated, an offline poll would spend the service worker's 5 s network
    // timeout and then report the uplink alive off the cache hit behind it.
    expect(deckPollMs(false)).toBe(0);
    expect(deckPollMs(true)).toBeGreaterThan(0);
  });

  it("applies that interval to the ENTRIES and RACES reads too (review A7)", async () => {
    // A second tablet's adds and moves reach this one only by re-reading, and
    // the per-heat n/N is computed off both of those reads, not off results.
    const { keys } = await import("../api/queryKeys");
    await deck({ races: [] });
    const { client } = renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    const polled = client
      .getQueryCache()
      .getAll()
      .filter((q) => (q.options as { refetchInterval?: number }).refetchInterval === deckPollMs(true))
      .map((q) => JSON.stringify(q.queryKey));
    for (const key of [
      keys.entries("c-1", "ev-1"),
      keys.results("c-1", "ev-1"),
      keys.races("c-1"),
    ])
      expect(polled).toContain(JSON.stringify(key));
  });
});

/**
 * Auto-fill lanes on the deck (ADR-0038). Two halves: what a queued add says
 * while the SERVER still owes it a lane, and the block a race gets from its own
 * `eventId` rather than from anybody's membership.
 */
describe("EventResultsScreen auto-fill lanes", () => {
  async function withSettings(over: { autoFillLanes?: boolean; laneCount?: number | null }) {
    const { getCarnivalSettings } = await import("../api/resources");
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      laneCount: 6,
      scoring: null,
      adjustmentCategories: [],
      autoFillLanes: false,
      ...over,
    } as never);
  }

  async function queueAdd() {
    const { searchEligibleStudents } = await import("../api/resources");
    vi.mocked(searchEligibleStudents).mockResolvedValue([
      { studentId: "st-late", displayName: "Late Swimmer", yearLevel: 7, houseCode: "WATTLE", ageGroup: "U13" },
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    fireEvent.change(screen.getByLabelText("Search eligible students"), {
      target: { value: "late" },
    });
    fireEvent.click(await screen.findByRole("button", { name: "Add" }));
    await screen.findByText(/adding/);
  }

  it("says 'lane pending' on a queued add while auto-fill is on", async () => {
    // The lane is the SERVER's to pick (§2), so the row promises nothing but
    // that somebody is about to have one — and no longer reads as a swimmer
    // sitting outside the field with no explanation.
    await withSettings({ autoFillLanes: true });
    await queueAdd();
    await waitFor(() => expect(screen.getByText(/lane pending/)).toBeTruthy());
  });

  it("says nothing about a lane on a queued add when auto-fill is off", async () => {
    await withSettings({ autoFillLanes: false });
    await queueAdd();
    expect(screen.queryByText(/lane pending/)).toBeNull();
  });

  it("blocks an EMPTY race attributed to this division with nothing in the outbox", async () => {
    // ADR-0037's cross-device residual: a race another marshal added held none
    // of this division's entries, so this device could not see it at all. The
    // stamp is what puts it on the deck, and its lanes on every row's picker.
    const { listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockResolvedValue([race("r-1", [], [], "ev-1")]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    const control = lanePicker("Sam Swimmer");
    await waitFor(() => expect(control.querySelectorAll("option").length).toBeGreaterThan(1));
    fireEvent.change(control, { target: { value: "1" } });

    // A placement into the race that already exists — NOT a second one minted
    // beside it, which is what an unstamped empty race used to produce.
    await waitFor(async () =>
      expect((await db.outbox.toArray()).filter((i) => i.kind === "entry.assign")).toHaveLength(1),
    );
    const items = await db.outbox.toArray();
    expect(items.find((i) => i.kind === "entry.assign")!.payload).toMatchObject({
      raceId: "r-1",
      lane: 1,
    });
    expect(items.filter((i) => i.kind === "race.materialise")).toHaveLength(0);
  });

  const assigns = async () => (await db.outbox.toArray()).filter((i) => i.kind === "entry.assign");

  it("chains NO placement behind a late add, with auto-fill on or off (ADR-0039 §2)", async () => {
    // The ADR-0018 §7 chain is retired: the create names its heat, so nothing
    // guesses — and nothing drains behind the create to wipe the lane the
    // server just filled.
    const { listRaces } = await import("../api/resources");
    for (const autoFillLanes of [true, false]) {
      vi.mocked(listRaces).mockResolvedValue([race("r-1", [["en-1", "ev-1"]], [1], "ev-1")]);
      await withSettings({ autoFillLanes });
      await queueAdd();

      await new Promise((r) => setTimeout(r, 30));
      expect(await assigns()).toHaveLength(0);
      const create = (await db.outbox.toArray()).find((i) => i.kind === "entry.create")!;
      expect(create.payload).toMatchObject({ raceId: "r-1" });
      cleanup();
      await db.outbox.clear();
    }
  });

  it("marks the RACES read stale on an accepted add, whatever the flag says", async () => {
    // The create may have placed the swimmer and minted the race it placed them
    // in (§2). Gated on the flag this was lossy in exactly the window where the
    // settings read had not landed yet, and the races read is small.
    const { listRaces } = await import("../api/resources");
    await withSettings({ autoFillLanes: false });
    await queueAdd();
    const before = vi.mocked(listRaces).mock.calls.length;

    const create = (await db.outbox.toArray()).find((i) => i.kind === "entry.create")!;
    await db.outbox.put({ ...create, status: "accepted", serverResponse: { id: create.id } });
    await waitFor(() => expect(vi.mocked(listRaces).mock.calls.length).toBeGreaterThan(before));
  });

  it("leaves a race stamped for ANOTHER division off this deck", async () => {
    // The stamp is an attribution, not a licence to render every race: this
    // division still has none, so the first lane pick mints one.
    const { listRaces } = await import("../api/resources");
    vi.mocked(listRaces).mockResolvedValue([race("r-9", [], [], "ev-2")]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    const control = lanePicker("Sam Swimmer");
    await waitFor(() => expect(control.querySelectorAll("option").length).toBeGreaterThan(1));
    fireEvent.change(control, { target: { value: "1" } });

    await waitFor(async () =>
      expect((await db.outbox.toArray()).filter((i) => i.kind === "race.materialise")).toHaveLength(
        1,
      ),
    );
  });
});

/**
 * ADR-0053 §6: heats are a Time event's vocabulary. The data is untouched — the
 * server still places a field entry in a heat with no lane (ADR-0039) — so this
 * is deck copy, keyed on the event's measure and nothing else.
 */
describe("EventResultsScreen heats on a non-Time event", () => {
  async function fieldDeck(attemptCount: number | null = null) {
    const { getCarnivalEvent, listCarnivalEvents, listRaces } = await import("../api/resources");
    const field = { ...event, measureType: "Distance" as const, attemptCount };
    vi.mocked(getCarnivalEvent).mockResolvedValue(field);
    vi.mocked(listCarnivalEvents).mockResolvedValue([field, sibling]);
    vi.mocked(listRaces).mockResolvedValue([race("r-1", [["en-1", "ev-1"]], [null], "ev-1")]);
  }

  it("shows no heat axis, and writes no ?heat= into the address", async () => {
    await fieldDeck();
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    expect(screen.queryByRole("group", { name: "Heats" })).toBeNull();
    // Nor the "New heat" item the picker carries, nor its refusal text.
    expect(screen.queryByRole("menuitem", { name: "New heat" })).toBeNull();
    await waitFor(() => expect(location.hash).not.toContain("heat="));
  });

  it("says nothing about a heat on the row, and removes the ENTRY rather than the heat", async () => {
    await fieldDeck();
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    expect(screen.queryByText(/heat 1/)).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Actions for Sam Swimmer" }));
    expect(screen.getByRole("menuitem", { name: "Remove entry" })).toBeTruthy();
    expect(screen.queryByRole("menuitem", { name: "Remove from heat" })).toBeNull();
  });

  it("shows the whole division, not one flight, with no heat axis to switch with", async () => {
    await fieldDeck();
    const { listRaces, listEntries } = await import("../api/resources");
    // Two flights of the same division, exactly as the server may place them.
    vi.mocked(listRaces).mockResolvedValue([
      race("r-1", [["en-1", "ev-1"]], [null], "ev-1"),
      { ...race("r-2", [["en-2", "ev-1"]], [null], "ev-1"), sortOrder: 1 },
    ]);
    vi.mocked(listEntries).mockResolvedValue([
      { ...entry, heat: 1, raceId: "r-1" },
      { ...entry, id: "en-2", displayName: "Tessa Fourteen", heat: 2, raceId: "r-2" },
    ]);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);

    expect(await screen.findByText("Sam Swimmer")).toBeTruthy();
    expect(screen.getByText("Tessa Fourteen")).toBeTruthy();
  });

  it("still shows the heat axis on a Time event", async () => {
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");
    expect(screen.getByRole("group", { name: "Heats" })).toBeTruthy();
  });

  it("captures a field series through the strip, one write per cell", async () => {
    await fieldDeck(3);
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);
    await screen.findByText("Sam Swimmer");

    const cells = screen.getAllByLabelText(/^Attempt \d mark for Sam Swimmer$/);
    expect(cells).toHaveLength(3);
    fireEvent.change(cells[0]!, { target: { value: "395" } });
    fireEvent.click(cells[0]!.closest(".attempt-cell")!.querySelector("button.primary")!);

    await waitFor(async () =>
      expect((await db.outbox.toArray()).filter((i) => i.kind === "result.attempt")).toHaveLength(1),
    );
    const [queued] = (await db.outbox.toArray()).filter((i) => i.kind === "result.attempt");
    expect(queued!.payload).toMatchObject({ entryId: "en-1", ordinal: 1, value: "3.95", foul: false });
  });
});

/**
 * ADR-0054 §4: a deck that refuses results told the recorder to go and find the
 * event list. The control the list's row already had is now on the notice.
 */
describe("EventResultsScreen — the event is not Open", () => {
  it("offers the open control to someone who may, and opens this event", async () => {
    const { getCarnivalEvent, changeEventStatus } = await import("../api/resources");
    vi.mocked(getCarnivalEvent).mockResolvedValue({ ...event, status: "Scheduled" });
    vi.mocked(changeEventStatus).mockResolvedValue({ ...event, status: "Open" });
    signOut();
    signIn({ subject: "admin-1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);

    expect(await screen.findByText(/Results are only accepted while the event is Open/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Open this event" }));
    await waitFor(() =>
      expect(vi.mocked(changeEventStatus)).toHaveBeenCalledWith("c-1", "ev-1", "Open"),
    );
  });

  it("says why to a recorder without offering them a control the server refuses", async () => {
    const { getCarnivalEvent } = await import("../api/resources");
    vi.mocked(getCarnivalEvent).mockResolvedValue({ ...event, status: "Closed" });
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);

    expect(await screen.findByText(/Results are only accepted while the event is Open/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /this event/ })).toBeNull();
  });

  // Scheduled → Open and Closed → Open are the only transitions the server
  // admits (`EventRow`), so no other status is offered a control that 409s.
  it("offers no control on a status the server will not open from", async () => {
    const { getCarnivalEvent } = await import("../api/resources");
    vi.mocked(getCarnivalEvent).mockResolvedValue({ ...event, status: "Finalised" });
    signOut();
    signIn({ subject: "admin-1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
    renderWithQuery(<EventResultsScreen carnivalId="c-1" eventId="ev-1" />);

    expect(await screen.findByText(/Results are only accepted while the event is Open/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /this event/ })).toBeNull();
  });
});
