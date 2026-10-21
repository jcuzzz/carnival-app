// @vitest-environment jsdom
import { cleanup, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { _resetReachabilityForTests } from "../api/reachability";
import type {
  Carnival,
  CarnivalProgress,
  CarnivalSettings,
  DeviceFleet,
  Standings,
} from "../api/types";
import { signIn, signOut } from "../auth/session";
import { _resetHouseMarksForTests, applyHouseMarks } from "../branding/houseMarks";
import { _resetSelectedCarnivalForTests } from "../carnivals/selectedCarnival";
import { RESULT_SUBMIT_KIND } from "../api/outboxTransport";
import { db } from "../db/db";
import { renderWithQuery } from "../test/queryWrapper";

/**
 * The other half of ADR-0024 §6/D3: the dashboard is READ-MOSTLY. Without this,
 * a build that left both editors here and put neither on Manage would pass the
 * whole suite — the split is only pinned if both directions are asserted.
 *
 * Plus ADR-0043 layout A: progress, what is still to record, today's record
 * candidates, participation, and the organiser device row that must not appear
 * for anyone the server refuses.
 */

vi.mock("../api/resources", () => ({
  getCarnival: vi.fn(),
  getCarnivalSettings: vi.fn(),
  getProgress: vi.fn(),
  getStandings: vi.fn(),
  listCarnivalDevices: vi.fn(),
}));

import {
  getCarnival,
  getCarnivalSettings,
  getProgress,
  getStandings,
  listCarnivalDevices,
} from "../api/resources";
import DashboardScreen from "./DashboardScreen";

const carnival = {
  id: "c-1",
  name: "Preston Swimming",
  date: "2027-03-06",
  status: "Open",
  type: "Swimming",
  venue: "Preston Aquatic",
} as Carnival;

const settings: CarnivalSettings = {
  divisionsDefault: null,
  rosterAgeGroups: [],
  rosterYear: null,
  scoring: {
    placePoints: [8, 6, 4, 2],
    participationFloor: 1,
    dnfPoints: 0,
    dnsPoints: 0,
    dqPoints: 0,
    socialPerEntryPoints: 1,
    version: "2027.1",
  },
  adjustmentCategories: ["Novelty 1"],
  laneCount: null,
  autoFillLanes: false,
  divisions: { ageGroups: [], categories: [], tiers: [] },
};

const standings = {
  provisional: true,
  houses: [
    {
      houseCode: "WATTLE",
      rank: 1,
      total: 7,
      placementPoints: 7,
      participationPoints: 0,
      adjustmentPoints: 0,
    },
  ],
  weightedEvents: [],
  hasSocialEvents: false,
} as unknown as Standings;

function progress(overrides: Partial<CarnivalProgress> = {}): CarnivalProgress {
  return {
    carnivalId: "c-1",
    provisional: true,
    totals: {
      events: 3,
      eventsComplete: 1,
      eventsInProgress: 1,
      eventsNotStarted: 1,
      expected: 12,
      recorded: 6,
    },
    events: [
      {
        carnivalEventId: "e-done",
        definitionName: "Freestyle 50m",
        divisionLabel: "13 Boys",
        slotId: null,
        slotLabel: null,
        stream: null,
        startTime: null,
        sortOrder: 0,
        expected: 4,
        recorded: 4,
        status: "complete",
      },
      {
        carnivalEventId: "e-part",
        definitionName: "Backstroke 50m",
        divisionLabel: "13 Girls",
        slotId: "s-1",
        slotLabel: "Heat 1",
        stream: "Pool",
        startTime: null,
        sortOrder: 1,
        expected: 4,
        recorded: 2,
        status: "inProgress",
      },
      {
        carnivalEventId: "e-none",
        definitionName: "Butterfly 50m",
        divisionLabel: "14 Girls",
        slotId: null,
        slotLabel: null,
        stream: null,
        startTime: null,
        sortOrder: 2,
        expected: 4,
        recorded: 0,
        status: "notStarted",
      },
    ],
    participation: [{ houseCode: "WATTLE", competitors: 10, entered: 8, recorded: 6 }],
    recordsToday: [
      {
        carnivalEventId: "e-done",
        definitionName: "Freestyle 50m",
        divisionLabel: "13 Boys",
        entryId: "en-1",
        displayName: "Harper Andersson",
        houseCode: "WATTLE",
        value: "31.02",
        kind: "new",
        standing: null,
      },
    ],
    ...overrides,
  };
}

const fleet: DeviceFleet = {
  serverNowUtc: new Date().toISOString(),
  devices: [
    {
      deviceId: "dev-1",
      lastSubject: "s1",
      lastDisplayName: "Alex Admin",
      lastSeenUtc: new Date().toISOString(),
      lastDrainUtc: new Date().toISOString(),
      oldestPendingCapturedAtUtc: null,
      pendingCount: 3,
      parkedCount: 0,
      appVersion: "test",
    },
  ],
} as unknown as DeviceFleet;

beforeEach(async () => {
  cleanup();
  _resetReachabilityForTests();
  _resetSelectedCarnivalForTests();
  _resetHouseMarksForTests();
  await db.outbox.clear();
  vi.mocked(getCarnival).mockReset().mockResolvedValue(carnival);
  vi.mocked(getCarnivalSettings).mockReset().mockResolvedValue(settings);
  vi.mocked(getStandings).mockReset().mockResolvedValue(standings);
  vi.mocked(getProgress).mockReset().mockResolvedValue(progress());
  vi.mocked(listCarnivalDevices).mockReset().mockResolvedValue(fleet);
  // An administrator: if the editors were going to leak back onto this screen
  // for anyone, it would be for them.
  signIn({ subject: "s1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
});

afterEach(async () => {
  signOut();
  _resetHouseMarksForTests();
  await db.outbox.clear();
});

async function queueResult(carnivalEventId: string, entryId: string): Promise<void> {
  await db.outbox.add({
    id: `ob-${entryId}`,
    kind: RESULT_SUBMIT_KIND,
    status: "queued",
    capturedAtUtc: new Date().toISOString(),
    attempts: 0,
    actorSubject: "s1",
    payload: { carnivalEventId, entryId },
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
  } as any);
}

describe("DashboardScreen", () => {
  it("shows the standings and the printable-program link", async () => {
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    expect(await screen.findByRole("heading", { name: "Dashboard", level: 1 })).toBeTruthy();
    expect(screen.getByRole("heading", { name: /House standings/, level: 2 })).toBeTruthy();
    expect(screen.getByRole("link", { name: /Printable program/ }).getAttribute("href")).toBe(
      "#/carnivals/c-1/program",
    );
  });

  it("carries NEITHER editor — they belong to Manage now", async () => {
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    await screen.findByRole("heading", { name: "Dashboard", level: 1 });
    expect(screen.queryByRole("heading", { name: /Novelty & bonus points/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /Scoring rules/ })).toBeNull();
    expect(screen.queryByRole("button", { name: /Award points/ })).toBeNull();
  });

  it("counts the events complete and badges the aggregate provisional", async () => {
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    // The headline is the RESULTS ratio (ADR-0043 §1); the event spine follows.
    const card = (await screen.findByText(/results recorded/)).closest("section")!;
    expect(card.textContent).toContain("6 of 12 results recorded");
    expect(card.textContent).toContain("1 of 3 events complete");
    expect(within(card).getByText("provisional")).toBeTruthy();
    // Standings carries its own — one badge per claim, not one for the screen.
    const standingsCard = screen
      .getByRole("heading", { name: /House standings/, level: 2 })
      .closest("section")!;
    expect(within(standingsCard).getByText("provisional")).toBeTruthy();
  });

  it("lists what is still to record, each linking to its deck with its state", async () => {
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    const part = await screen.findByRole("link", { name: /Backstroke 50m/ });
    expect(part.getAttribute("href")).toBe("#/carnivals/c-1/events/e-part");
    expect(within(part).getByText("2 of 4")).toBeTruthy();

    const none = screen.getByRole("link", { name: /Butterfly 50m/ });
    expect(within(none).getByText("not started")).toBeTruthy();

    // The complete event is not outstanding work and does not appear.
    expect(screen.queryByRole("link", { name: /Freestyle 50m/ })).toBeNull();
  });

  it("counts this device's queued captures as progress (ADR-0014 §1)", async () => {
    await queueResult("e-part", "en-a");
    await queueResult("e-part", "en-b");
    await queueResult("e-none", "en-c");
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    // e-part reaches 4 of 4 and leaves the list; e-none becomes 1 of 4.
    await vi.waitFor(() => {
      expect(within(screen.getByRole("link", { name: /Butterfly 50m/ })).getByText("1 of 4"))
        .toBeTruthy();
      expect(screen.queryByRole("link", { name: /Backstroke 50m/ })).toBeNull();
    });
    const card = screen.getByRole("heading", { name: /Progress/, level: 2 }).closest("section")!;
    expect(card.textContent).toContain("2 of 3 events complete");
  });

  it("reads today's records as CANDIDATES, new and equalled apart", async () => {
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    expect(await screen.findByText("candidate — new")).toBeTruthy();
    expect(screen.getByText("31.02")).toBeTruthy();
    expect(screen.getByText(/Harper Andersson/)).toBeTruthy();
    // The card head reaches the deck the swim was recorded on.
    expect(screen.getByRole("link", { name: /31\.02/ }).getAttribute("href")).toBe(
      "#/carnivals/c-1/events/e-done",
    );
  });

  it("says so plainly when a record is only EQUALLED", async () => {
    const equalled = progress();
    equalled.recordsToday[0]!.kind = "equalled";
    vi.mocked(getProgress).mockResolvedValue(equalled);
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    expect(await screen.findByText("candidate — equalled")).toBeTruthy();
  });

  it("says nothing has been beaten rather than showing an empty strip", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress({ recordsToday: [] }));
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    expect(await screen.findByText("No record candidates yet")).toBeTruthy();
  });

  it("reads a finalised carnival's records as RECORDS, not candidates", async () => {
    const finalised = progress({ provisional: false });
    finalised.recordsToday.push({
      ...finalised.recordsToday[0]!,
      entryId: "en-2",
      kind: "equalled",
    });
    vi.mocked(getProgress).mockResolvedValue(finalised);
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    expect(await screen.findByText("new record")).toBeTruthy();
    expect(screen.getByText("record equalled")).toBeTruthy();
    expect(screen.queryByText(/candidate/)).toBeNull();
  });

  it("says no records were set once the carnival is finalised", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress({ provisional: false, recordsToday: [] }));
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    expect(await screen.findByText("No records set at this carnival")).toBeTruthy();
    expect(screen.queryByText(/candidate/)).toBeNull();
  });

  it("shows participation per house", async () => {
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    const card = (await screen.findByRole("heading", { name: "Participation", level: 2 }))
      .closest("section")!;
    expect(within(card).getByText("8 of 10 entered")).toBeTruthy();
  });

  it("draws the device row when the fleet read answers", async () => {
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    expect(await screen.findByText("3 queued")).toBeTruthy();
    expect(screen.getByRole("link", { name: "Device list" }).getAttribute("href")).toBe(
      "#/carnivals/c-1/manage",
    );
  });

  it("says the fleet could not be checked rather than leaving a silent gap", async () => {
    vi.mocked(listCarnivalDevices).mockRejectedValue(new Error("Forbidden"));
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    expect(await screen.findByText(/Couldn't check devices/)).toBeTruthy();
    expect(screen.queryByRole("link", { name: "Device list" })).toBeNull();
  });

  it("never reads the fleet for a caller who cannot manage the carnival", async () => {
    signOut();
    signIn({ subject: "s2", displayName: "Ollie Official", roles: ["Recorder"] });
    renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    await screen.findByText(/results recorded/);
    expect(vi.mocked(listCarnivalDevices)).not.toHaveBeenCalled();
    expect(screen.queryByText(/Couldn't check devices/)).toBeNull();
  });

  it("paints the standings house where the school configured a colour", async () => {
    applyHouseMarks([{ code: "WATTLE", name: "Wattle", colour: "#2e9e52" }]);
    const { container } = renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    await screen.findByRole("heading", { name: /House standings/, level: 2 });
    await vi.waitFor(() =>
      expect(container.querySelector(".standings-row .house-swatch")).not.toBeNull(),
    );
    expect(container.querySelector(".standings-bar")!.getAttribute("style")).toContain(
      "var(--house-wattle)",
    );
  });

  it("leaves the standings text-only when no house has a colour", async () => {
    applyHouseMarks([{ code: "WATTLE", name: "Wattle", colour: null }]);
    const { container } = renderWithQuery(<DashboardScreen carnivalId="c-1" />);

    await screen.findByRole("heading", { name: /House standings/, level: 2 });
    await vi.waitFor(() => expect(container.querySelector(".standings-row")).not.toBeNull());
    expect(container.querySelector(".house-swatch")).toBeNull();
    expect(screen.getAllByText("Wattle").length).toBeGreaterThan(0);
  });
});
