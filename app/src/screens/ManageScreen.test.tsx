// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { CARNIVAL_NOT_READY_TEXT } from "../api/errorText";
import { _resetReachabilityForTests } from "../api/reachability";
import type { Carnival, CarnivalSettings, CarnivalStatus } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { _resetSelectedCarnivalForTests } from "../carnivals/selectedCarnival";
import { _resetToastsForTests, getToasts } from "../shell/toast";
import { renderWithQuery } from "../test/queryWrapper";

/**
 * The organiser surface (ADR-0024 §7). What is pinned here is the separation
 * the old button row did not have: the role gate is on the SCREEN, and the one
 * irreversible transition is fenced away from the reversible ones.
 */

vi.mock("../api/resources", () => ({
  changeCarnivalStatus: vi.fn(),
  getCarnival: vi.fn(),
  getCarnivalSchedule: vi.fn(),
  getCarnivalSettings: vi.fn(),
  getProgress: vi.fn(),
  listAdjustments: vi.fn(),
  listCarnivalDevices: vi.fn(),
  listCarnivalEvents: vi.fn(),
  listEventDefinitions: vi.fn(),
  openScheduledEvents: vi.fn(),
}));

import {
  changeCarnivalStatus,
  getCarnival,
  getCarnivalSchedule,
  getCarnivalSettings,
  getProgress,
  listAdjustments,
  listCarnivalDevices,
  listCarnivalEvents,
  listEventDefinitions,
  openScheduledEvents,
} from "../api/resources";
import type {
  CarnivalEvent,
  CarnivalEventStatus,
  CarnivalProgress,
  DeviceFleet,
  DeviceStatus,
  EventDefinition,
} from "../api/types";
import ManageScreen from "./ManageScreen";

function device(over: Partial<DeviceStatus> = {}): DeviceStatus {
  return {
    deviceId: "device-1",
    lastSeenUtc: new Date().toISOString(),
    lastDrainUtc: new Date().toISOString(),
    pendingCount: 0,
    parkedCount: 0,
    oldestPendingCapturedAtUtc: null,
    lastSubject: "marshal-1",
    lastDisplayName: "Mia Marshal",
    appVersion: null,
    ...over,
  };
}

/** The list plus the clock it was read against (ADR-0034 §4). */
function fleet(devices: DeviceStatus[] = []): DeviceFleet {
  return { serverNowUtc: new Date().toISOString(), devices };
}

function carnival(status: CarnivalStatus = "Open"): Carnival {
  return {
    id: "c-1",
    name: "Preston Swimming",
    date: "2027-03-06",
    status,
    type: "Swimming",
    venue: "Preston Pool",
    ownerSubject: "s1",
    ruleVersion: "2027.1",
    version: 1,
  } as Carnival;
}

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

/** A division of the carnival, in whatever state the checklist is being read in. */
function division(id: string, status: CarnivalEventStatus = "Scheduled"): CarnivalEvent {
  return {
    id,
    carnivalId: "c-1",
    eventDefinitionId: "def-1",
    ageGroup: "12",
    scheduleSlotId: null,
    status,
  } as CarnivalEvent;
}

/** The catalogue behind the checklist's Events row (ADR-0059 §3). */
function definition(id: string, name: string): EventDefinition {
  return {
    id,
    code: name.toUpperCase(),
    name,
    sport: "Swimming",
    isActive: true,
    usage: { carnivals: 0, divisions: 0, schoolRecords: 0, results: 0 },
  } as EventDefinition;
}

/** ADR-0043 §2: `competitors` is the promoted roster, per house. */
function progress(competitors: number): CarnivalProgress {
  return {
    carnivalId: "c-1",
    participation: [{ houseCode: "BANKSIA", competitors, entered: 0, recorded: 0 }],
  } as CarnivalProgress;
}

beforeEach(() => {
  cleanup();
  _resetReachabilityForTests();
  _resetSelectedCarnivalForTests();
  _resetToastsForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  vi.mocked(getCarnival).mockReset().mockResolvedValue(carnival());
  vi.mocked(getCarnivalSettings).mockReset().mockResolvedValue(settings);
  vi.mocked(listAdjustments).mockReset().mockResolvedValue([]);
  vi.mocked(changeCarnivalStatus).mockReset().mockResolvedValue(carnival("Closed"));
  vi.mocked(listCarnivalDevices).mockReset().mockResolvedValue(fleet());
  vi.mocked(listCarnivalEvents).mockReset().mockResolvedValue([]);
  vi.mocked(getCarnivalSchedule).mockReset().mockResolvedValue({
    carnivalId: "c-1",
    streams: [],
  });
  vi.mocked(getProgress).mockReset().mockResolvedValue(progress(0));
  vi.mocked(listEventDefinitions)
    .mockReset()
    .mockResolvedValue([
      definition("def-1", "Freestyle 50m"),
      definition("def-2", "Butterfly 50m"),
    ]);
  vi.mocked(openScheduledEvents).mockReset().mockResolvedValue({ opened: 0, eventIds: [] });
  signIn({ subject: "s1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
});

afterEach(() => {
  signOut();
});

describe("ManageScreen", () => {
  it("gathers the setup surfaces the deck no longer carries", async () => {
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect((await screen.findByRole("link", { name: /Events/ })).getAttribute("href")).toBe(
      "#/carnivals/c-1/setup/events",
    );
    expect(screen.getByRole("link", { name: /Program/ }).getAttribute("href")).toBe(
      "#/carnivals/c-1/schedule",
    );
    // ADR-0067 §1: the ages are no step of their own.
    expect(screen.queryByText(/Divisions/)).toBeNull();
    expect(document.querySelector('a[href$="/setup/divisions"]')).toBeNull();
  });

  // ADR-0066 §4: the standard events moved to Event setup.
  it("carries no template section", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByRole("button", { name: "Open carnival" });
    expect(screen.queryByRole("heading", { name: "Start from the standard events" })).toBeNull();
    expect(screen.queryByLabelText("Carnival template")).toBeNull();
    expect(screen.queryByRole("button", { name: /standard events/ })).toBeNull();
  });

  // ---- The setup checklist (ADR-0054 §1) ----

  it("orders the setup steps and points at the first one still to do", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const students = await screen.findByText(/^1\. Students/);
    const row = students.closest("li")!;
    expect(row.textContent).toContain("Done");
    expect(row.textContent).toContain("412 students");
    expect(row.className).not.toContain("awaiting");

    // Nothing is configured past the students, so Events is what to do next —
    // and it is the ONLY step marked so.
    const events = screen.getByText(/^2\. Events/).closest("li")!;
    expect(events.textContent).toContain("Do this next");
    expect(events.className).toContain("awaiting");
    expect(screen.getAllByText("Do this next")).toHaveLength(1);
  });

  it("derives every done state from the carnival's own reads", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...settings,
      divisions: { ageGroups: ["12"], categories: ["M"], tiers: ["Competitive"] },
    });
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1"), division("e-2", "Open")]);
    vi.mocked(getCarnivalSchedule).mockResolvedValue({
      carnivalId: "c-1",
      streams: [{ id: "s-1", name: "Pool", sortOrder: 0, slots: [{ id: "sl-1" }] }],
    } as never);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByText(/^1\. Students/);
    expect(screen.getByText(/^2\. Events/).closest("li")!.textContent).toContain(
      "1 in this carnival · 1 available to add",
    );
    expect(screen.getByText(/^3\. Program/).closest("li")!.textContent).toContain(
      "1 slot across 1 stream",
    );
    // Open: the carnival is Open, and one division is still Scheduled.
    expect(screen.getByText(/^4\. Open/).closest("li")!.textContent).toContain(
      "1 division still Scheduled",
    );
    expect(screen.queryByText("Do this next")).toBeNull();
  });

  it("marks the program optional rather than undone", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...settings,
      divisions: { ageGroups: ["12"], categories: ["M"], tiers: ["Competitive"] },
    });
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1")]);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const program = (await screen.findByText(/^3\. Program/)).closest("li")!;
    expect(program.textContent).toContain("Optional");
    expect(program.textContent).not.toContain("Do this next");
  });

  it("warns on the Students step when the roster's year is not the carnival's", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(getCarnivalSettings).mockResolvedValue({ ...settings, rosterYear: 2026 });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const row = (await screen.findByText(/^1\. Students/)).closest("li")!;
    const warning = await waitFor(() => {
      const found = row.querySelector(".alert.warn");
      expect(found?.textContent).toMatch(/ages are for 2026, but this carnival is in 2027/);
      return found!;
    });
    expect(warning.textContent).toContain("A system administrator can import them again for 2027");
    expect(row.contains(warning)).toBe(true);
    expect(row.textContent).toContain("Done");
  });

  it("raises no year warning when the roster is the carnival's year, or there is none", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({ ...settings, rosterYear: 2027 });
    // The lanes editor renders off the settings read, so the year has landed by then.
    const { unmount } = renderWithQuery(<ManageScreen carnivalId="c-1" />);
    await screen.findByRole("heading", { name: "Lanes" });
    expect(screen.queryByText(/ages are for/)).toBeNull();
    unmount();

    vi.mocked(getCarnivalSettings).mockResolvedValue({ ...settings, rosterYear: null });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);
    await screen.findByRole("heading", { name: "Lanes" });
    expect(screen.queryByText(/ages are for/)).toBeNull();
  });

  it("says so rather than guessing when the roster count can't be read", async () => {
    vi.mocked(getProgress).mockRejectedValue(new TypeError("Failed to fetch"));
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const students = (await screen.findByText(/^1\. Students/)).closest("li")!;
    expect(students.textContent).toContain("The student count couldn’t be read here");
    expect(students.textContent).not.toContain("Done");
    // Unknown is not undone: the pointer moves on to the step that IS.
    expect(screen.getByText(/^2\. Events/).closest("li")!.textContent).toContain(
      "Do this next",
    );
  });

  it("links the reports screen, which is where an export is taken from (ADR-0004)", async () => {
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect(
      (await screen.findByRole("link", { name: /Standings and results book/ })).getAttribute("href"),
    ).toBe("#/carnivals/c-1/reports");
  });

  it("keeps the record book on the Reports card, at its School address (ADR-0055 §3)", async () => {
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect(
      (await screen.findByRole("link", { name: /School records/ })).getAttribute("href"),
    ).toBe("#/school/records");
  });

  it("carries the two editors that moved OFF the dashboard (ADR-0024 §6/D3)", async () => {
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect(await screen.findByRole("heading", { name: /Novelty & bonus points/ })).toBeTruthy();
    expect(screen.getByRole("button", { name: /Scoring rules/ })).toBeTruthy();
  });

  it("hides the import link from a carnival administrator — imports are SysAdmin-only", async () => {
    // Server-side both ManageImportsRoles and ViewImportsRoles are
    // [SystemAdministrator], so offering the link here only ever refuses them.
    // The STEP still shows, off the progress aggregate every viewer may read.
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByRole("link", { name: /Events/ });
    expect(screen.getByText(/^1\. Students/).closest("li")!.textContent).toContain(
      "a system administrator imports the student list",
    );
    expect(screen.queryByRole("link", { name: /Students/ })).toBeNull();
  });

  it("links the import, launched from this carnival, for a system administrator", async () => {
    signIn({ subject: "s0", displayName: "Sam Sys", roles: ["SystemAdministrator"] });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect((await screen.findByRole("link", { name: /Students/ })).getAttribute("href")).toBe(
      "#/import?carnival=c-1",
    );
  });

  it("refuses an official who cannot manage, and sends them back to the deck", async () => {
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect(await screen.findByRole("alert")).toBeTruthy();
    expect(screen.getByRole("link", { name: /Back to the deck/ }).getAttribute("href")).toBe(
      "#/carnivals/c-1",
    );
    expect(screen.queryByRole("button", { name: /Close carnival/ })).toBeNull();
  });

  it("offers a sign-in to a signed-out visitor rather than naming a role", async () => {
    signOut();
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect(await screen.findByRole("link", { name: "Sign in" })).toBeTruthy();
    expect(screen.queryByText(/needs the carnival administrator role/)).toBeNull();
    expect(getCarnival).not.toHaveBeenCalled();
  });

  it("closes without ceremony — closing is recoverable (ADR-0017 §4)", async () => {
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Close carnival" }));
    await waitFor(() =>
      expect(vi.mocked(changeCarnivalStatus)).toHaveBeenCalledWith("c-1", "Closed"),
    );
  });

  it("offers the reopen escape hatch on a closed carnival", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Closed"));
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect(await screen.findByRole("button", { name: "Reopen carnival" })).toBeTruthy();
  });

  it("fences finalise away from the reversible controls, and takes two taps", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Closed"));
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const finalise = await screen.findByRole("button", { name: "Finalise carnival" });
    expect(finalise.closest(".danger-zone")).not.toBeNull();
    // Not in the same row as Reopen — that adjacency is the defect ADR-0024
    // set out to remove.
    expect(screen.getByRole("button", { name: "Reopen carnival" }).closest(".danger-zone")).toBeNull();

    fireEvent.click(finalise);
    expect(vi.mocked(changeCarnivalStatus)).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Confirm finalise?" }));
    await waitFor(() =>
      expect(vi.mocked(changeCarnivalStatus)).toHaveBeenCalledWith("c-1", "Finalised"),
    );
  });

  it("shows no finalise at all while the carnival is still open", async () => {
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByRole("button", { name: "Close carnival" });
    expect(screen.queryByRole("button", { name: /Finalise/ })).toBeNull();
  });

  // ---- Open all divisions (ADR-0054 §4) ----

  it("opens every scheduled division in one action, counting them on the button", async () => {
    vi.mocked(listCarnivalEvents).mockResolvedValue([
      division("e-1"),
      division("e-2"),
      division("e-3", "Open"),
    ]);
    vi.mocked(openScheduledEvents).mockResolvedValue({ opened: 2, eventIds: ["e-1", "e-2"] });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Open all divisions (2)" }));
    await waitFor(() => expect(openScheduledEvents).toHaveBeenCalledWith("c-1"));
    await waitFor(() => expect(getToasts().map((t) => t.message)).toContain("2 divisions opened."));
  });

  // ---- Opening, and Ready to open (ADR-0066 §5–6) ----

  it("opens the carnival and every scheduled division with the primary", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    vi.mocked(changeCarnivalStatus).mockResolvedValue(carnival("Open"));
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1"), division("e-2")]);
    vi.mocked(openScheduledEvents).mockResolvedValue({ opened: 2, eventIds: ["e-1", "e-2"] });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByRole("button", { name: "Open the carnival only" });
    const primary = screen.getByRole("button", { name: "Open carnival" });
    expect(primary.className).toBe("primary");
    fireEvent.click(primary);

    await waitFor(() => expect(openScheduledEvents).toHaveBeenCalledWith("c-1"));
    expect(changeCarnivalStatus).toHaveBeenCalledWith("c-1", "Open");
    expect(vi.mocked(changeCarnivalStatus).mock.invocationCallOrder[0]).toBeLessThan(
      vi.mocked(openScheduledEvents).mock.invocationCallOrder[0]!,
    );
  });

  it("opens only the carnival with the secondary", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    vi.mocked(changeCarnivalStatus).mockResolvedValue(carnival("Open"));
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1"), division("e-2")]);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const secondary = await screen.findByRole("button", { name: "Open the carnival only" });
    expect(secondary.className).not.toContain("primary");
    fireEvent.click(secondary);

    await waitFor(() => expect(changeCarnivalStatus).toHaveBeenCalledWith("c-1", "Open"));
    expect(openScheduledEvents).not.toHaveBeenCalled();
  });

  it("opens the divisions too while the event list is loading, or after it failed", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    vi.mocked(changeCarnivalStatus).mockResolvedValue(carnival("Open"));
    const loading = () => new Promise<never>(() => {});
    const failed = () => Promise.reject(new Error("nope"));
    for (const events of [loading, failed]) {
      cleanup();
      vi.mocked(changeCarnivalStatus).mockClear();
      vi.mocked(openScheduledEvents).mockClear();
      vi.mocked(listCarnivalEvents).mockImplementation(events);
      renderWithQuery(<ManageScreen carnivalId="c-1" />);

      const primary = await screen.findByRole("button", { name: "Open carnival" });
      expect(primary.className).toBe("primary");
      // An unread list claims nothing: the server is the check.
      await waitFor(() => expect(getProgress).toHaveBeenCalled());
      expect((primary as HTMLButtonElement).disabled).toBe(false);
      fireEvent.click(primary);

      await waitFor(() => expect(openScheduledEvents).toHaveBeenCalledWith("c-1"));
      expect(changeCarnivalStatus).toHaveBeenCalledWith("c-1", "Open");
    }
  });

  it("opens no divisions when opening the carnival was refused", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1")]);
    vi.mocked(changeCarnivalStatus).mockRejectedValue(new TypeError("Failed to fetch"));
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByRole("button", { name: "Open the carnival only" });
    fireEvent.click(screen.getByRole("button", { name: "Open carnival" }));

    await waitFor(() => expect(changeCarnivalStatus).toHaveBeenCalledWith("c-1", "Open"));
    const alert = await screen.findByRole("alert");
    // Beside the buttons in step 4, not at the top of Manage.
    expect(screen.getByText(/^4\. Open/).closest("li")!.contains(alert)).toBe(true);
    expect(openScheduledEvents).not.toHaveBeenCalled();
  });

  it("says the carnival opened when only its divisions failed, and offers the retry", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1"), division("e-2")]);
    vi.mocked(changeCarnivalStatus).mockImplementation(async () => {
      vi.mocked(getCarnival).mockResolvedValue(carnival("Open"));
      return carnival("Open");
    });
    vi.mocked(openScheduledEvents).mockRejectedValue(
      new ApiError(409, { error: "CONFLICT", detail: "Try again." }, "/x"),
    );
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByRole("button", { name: "Open the carnival only" });
    fireEvent.click(screen.getByRole("button", { name: "Open carnival" }));

    expect(
      await screen.findByText(/^The carnival is open, but its divisions weren’t opened: /),
    ).toBeTruthy();
    expect(await screen.findByRole("button", { name: "Open all divisions (2)" })).toBeTruthy();
  });

  it("lists what to check before opening, without disabling Open", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...settings,
      rosterAgeGroups: ["12", "13"],
      rosterYear: 2026,
    });
    vi.mocked(listCarnivalEvents).mockResolvedValue([
      { ...division("e-1"), ageGroup: "12", scheduleSlotId: "sl-1" },
      { ...division("e-2"), ageGroup: "19", scheduleSlotId: null },
    ]);
    vi.mocked(getCarnivalSchedule).mockResolvedValue({
      carnivalId: "c-1",
      streams: [{ id: "s-1", name: "Pool", sortOrder: 0, slots: [{ id: "sl-1" }] }],
    } as never);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const step = (await screen.findByText(/^4\. Open/)).closest("li")!;
    await waitFor(() =>
      expect([...step.querySelectorAll(".alert.warn li")].map((li) => li.textContent)).toEqual([
        "The student list’s ages are for 2026, but this carnival is in 2027.",
        "1 division has an age no current student has (age 19).",
        "1 division isn’t in the program, so it won’t appear on the printed running order. " +
          "Leave it if it runs separately. Add it to the program",
      ]),
    );
    expect(
      (screen.getByRole("link", { name: "Add it to the program" }) as HTMLAnchorElement).getAttribute(
        "href",
      ),
    ).toBe("#/carnivals/c-1/schedule");
    expect(step.textContent).toContain("1 event · 2 divisions");
    expect(
      (screen.getByRole("button", { name: "Open carnival" }) as HTMLButtonElement).disabled,
    ).toBe(false);
  });

  // ---- Not ready to open (ADR-0066 §5): shown greyed, never hidden ----

  it.each([
    ["no students and no events", 0, [], "Import the students and add events first."],
    ["no events", 412, [], "Add events first."],
    ["no students", 0, [division("e-1")], "Import the students first."],
  ] as const)(
    "greys both opens out with %s, saying what is missing",
    async (_case, students, divisions, reason) => {
      vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
      vi.mocked(getProgress).mockResolvedValue(progress(students));
      vi.mocked(listCarnivalEvents).mockResolvedValue([...divisions]);
      renderWithQuery(<ManageScreen carnivalId="c-1" />);

      const line = await screen.findByText(reason);
      const step = screen.getByText(/^4\. Open/).closest("li")!;
      expect(step.contains(line)).toBe(true);
      for (const name of ["Open carnival", "Open the carnival only"]) {
        const button = screen.getByRole("button", { name }) as HTMLButtonElement;
        expect(button.disabled).toBe(true);
        expect(button.getAttribute("aria-describedby")).toBe(line.id);
      }
      expect(screen.getByRole("button", { name: "Open carnival" }).className).toBe("primary");
      expect(step.textContent).not.toContain("With “Open the carnival only”");
      fireEvent.click(screen.getByRole("button", { name: "Open carnival" }));
      expect(changeCarnivalStatus).not.toHaveBeenCalled();
    },
  );

  it("enables both opens once there are students and events, with no missing line", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1")]);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByText("1 event · 1 division");
    await waitFor(() =>
      expect(screen.getByText(/^1\. Students/).closest("li")!.textContent).toContain("Done"),
    );
    for (const name of ["Open carnival", "Open the carnival only"]) {
      const button = screen.getByRole("button", { name }) as HTMLButtonElement;
      expect(button.disabled).toBe(false);
      expect(button.hasAttribute("aria-describedby")).toBe(false);
    }
    expect(screen.queryByText(/first\.$/)).toBeNull();
  });

  it("words the server's not-ready refusal in step 4, should the screen be behind", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1")]);
    vi.mocked(changeCarnivalStatus).mockRejectedValue(
      new ApiError(
        409,
        {
          error: "CARNIVAL_NOT_READY",
          detail: "Import the student list before opening.",
          missing: ["studentList"],
        },
        "/x",
      ),
    );
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByText("1 event · 1 division");
    fireEvent.click(screen.getByRole("button", { name: "Open carnival" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toBe(CARNIVAL_NOT_READY_TEXT);
    expect(screen.getByText(/^4\. Open/).closest("li")!.contains(alert)).toBe(true);
    expect(openScheduledEvents).not.toHaveBeenCalled();
  });

  it("re-reads the students and events on a not-ready refusal, so the opens grey out", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1")]);
    vi.mocked(changeCarnivalStatus).mockImplementation(async () => {
      // Another administrator removed the student list since this screen read it.
      vi.mocked(getProgress).mockResolvedValue(progress(0));
      throw new ApiError(
        409,
        { error: "CARNIVAL_NOT_READY", detail: "x", missing: ["studentList"] },
        "/x",
      );
    });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByText("1 event · 1 division");
    await waitFor(() =>
      expect(screen.getByText(/^1\. Students/).closest("li")!.textContent).toContain("Done"),
    );
    fireEvent.click(screen.getByRole("button", { name: "Open carnival" }));

    expect(await screen.findByText("Import the students first.")).toBeTruthy();
    for (const name of ["Open carnival", "Open the carnival only"])
      expect((screen.getByRole("button", { name }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("links divisions outside the program to the program builder", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    vi.mocked(listCarnivalEvents).mockResolvedValue([
      { ...division("e-1"), scheduleSlotId: "sl-1" },
      { ...division("e-2"), scheduleSlotId: null },
      { ...division("e-3"), scheduleSlotId: null },
    ]);
    vi.mocked(getCarnivalSchedule).mockResolvedValue({
      carnivalId: "c-1",
      streams: [{ id: "s-1", name: "Pool", sortOrder: 0, slots: [{ id: "sl-1" }] }],
    } as never);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const link = await screen.findByRole("link", { name: "Add them to the program" });
    expect(link.getAttribute("href")).toBe("#/carnivals/c-1/schedule");
  });

  it("shows no readiness check once the carnival is open", async () => {
    vi.mocked(listCarnivalEvents).mockResolvedValue([]);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const status = (await screen.findByRole("heading", { name: "Carnival status" })).closest(
      "section",
    )!;
    await screen.findByRole("button", { name: "Close carnival" });
    expect(status.textContent).not.toContain("No events yet");
    expect(status.textContent).not.toContain("Before you open");
  });

  it("shows the server's refusal beside the button it belongs to", async () => {
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1")]);
    vi.mocked(openScheduledEvents).mockRejectedValue(
      new ApiError(
        409,
        {
          error: "CARNIVAL_NOT_EDITABLE",
          detail: "Divisions cannot be opened on a Finalised carnival.",
        },
        "/x",
      ),
    );
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const button = await screen.findByRole("button", { name: "Open all divisions (1)" });
    fireEvent.click(button);

    const refusal = await screen.findByText(/Divisions cannot be opened on a Finalised carnival/);
    expect(refusal.closest("section")).toBe(button.closest("section"));
  });

  // The server gate is the single-event one (ADR-0054 §4, amended), so the app's
  // is too: Closed still opens divisions, and only Finalised has no control.
  it("bulk-opens on a closed carnival, saying results still wait on a reopen", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Closed"));
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1")]);
    vi.mocked(openScheduledEvents).mockResolvedValue({ opened: 1, eventIds: ["e-1"] });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const button = (await screen.findByRole("button", {
      name: "Open all divisions (1)",
    })) as HTMLButtonElement;
    expect(button.disabled).toBe(false);
    expect(screen.getByText(/only accepted once the carnival is reopened/)).toBeTruthy();

    fireEvent.click(button);
    await waitFor(() => expect(openScheduledEvents).toHaveBeenCalledWith("c-1"));
  });

  it("offers no bulk open on a finalised carnival", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Finalised"));
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1")]);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByText(/Finalised carnivals cannot be reopened/);
    expect(screen.queryByRole("button", { name: /Open all divisions/ })).toBeNull();
  });

  it("opens from the checklist's last step while Draft, with no status section", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1")]);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const secondary = await screen.findByRole("button", { name: "Open the carnival only" });
    const step = screen.getByText(/^4\. Open/).closest("li")!;
    expect(step.contains(screen.getByRole("button", { name: "Open carnival" }))).toBe(true);
    expect(step.contains(secondary)).toBe(true);
    expect(step.textContent).toContain(
      "With “Open the carnival only”, every division stays unopened, so you can open them one " +
        "at a time on the day.",
    );
    expect(screen.queryByRole("heading", { name: "Carnival status" })).toBeNull();
  });

  it("keeps the Carnival status section, not the checklist, for an open carnival", async () => {
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1")]);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const close = await screen.findByRole("button", { name: "Close carnival" });
    const status = screen.getByRole("heading", { name: "Carnival status" }).closest("section")!;
    expect(status.contains(close)).toBe(true);
    expect(status.contains(screen.getByRole("button", { name: "Open all divisions (1)" }))).toBe(
      true,
    );
    const step = screen.getByText(/^4\. Open/).closest("li")!;
    expect(step.querySelector("button")).toBeNull();
  });

  // Design system: one button.primary per screen.
  it.each([
    ["Draft", ["Open carnival"]],
    ["Open", ["Close carnival"]],
    ["Closed", []],
    ["Finalised", []],
  ] as const)("keeps to at most one primary while %s", async (status, primaries) => {
    vi.mocked(getCarnival).mockResolvedValue(carnival(status));
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1")]);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByText(/^4\. Open/);
    if (status !== "Finalised") {
      await screen.findByRole("heading", { name: "Lanes" });
      fireEvent.click(screen.getByRole("button", { name: "Scoring rules…" }));
      expect(screen.getByRole("button", { name: "Save scoring rules" }).className).toBe("");
      expect(screen.getByRole("button", { name: "Save lane settings" }).className).toBe("");
      expect(screen.getByRole("button", { name: "Award points" }).className).toBe("");
    } else {
      await screen.findByText(/Finalised carnivals cannot be reopened/);
    }
    const found = [...document.querySelectorAll("button.primary")].map((b) => b.textContent);
    expect(found).toEqual(primaries);
  });

  // ---- The device fleet (ADR-0034) ----

  it("says plainly that nothing reporting is not the same as nothing outstanding", async () => {
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect(await screen.findByRole("heading", { name: "Devices" })).toBeTruthy();
    expect(screen.getByText(/No device has reported a queue/)).toBeTruthy();
  });

  it("names the official holding each device and what it was carrying", async () => {
    vi.mocked(listCarnivalDevices).mockResolvedValue(
      fleet([device({ pendingCount: 4, oldestPendingCapturedAtUtc: new Date().toISOString() })]),
    );
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect(await screen.findByText("Mia Marshal")).toBeTruthy();
    expect(screen.getByText(/4 waiting to sync/)).toBeTruthy();
  });

  it("warns beside Finalise when a device went quiet still holding work", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Closed"));
    vi.mocked(listCarnivalDevices).mockResolvedValue(
      fleet([
        device({
          lastSeenUtc: new Date(Date.now() - 40 * 60_000).toISOString(),
          pendingCount: 5,
        }),
      ]),
    );
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const finalise = await screen.findByRole("button", { name: "Finalise carnival" });
    const zone = finalise.closest(".danger-zone")!;
    expect(zone.textContent).toContain("5 captures");
    expect(zone.textContent).toContain("Finalising now publishes without them");
  });

  it("still lets the organiser finalise over the warning — it is a warning, not a block", async () => {
    // ADR-0034 §4: an offline device and a lost one are indistinguishable from
    // the server, and ADR-0006's audited override has to keep working.
    vi.mocked(getCarnival).mockResolvedValue(carnival("Closed"));
    vi.mocked(listCarnivalDevices).mockResolvedValue(
      fleet([
        device({ lastSeenUtc: new Date(Date.now() - 40 * 60_000).toISOString(), pendingCount: 5 }),
      ]),
    );
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Finalise carnival" }));
    await screen.findByText(/Finalising now publishes without them/);
    fireEvent.click(screen.getByRole("button", { name: "Confirm finalise?" }));
    await waitFor(() =>
      expect(vi.mocked(changeCarnivalStatus)).toHaveBeenCalledWith(
        "c-1",
        "Finalised",
        "Finalised with 1 device not drained (5 captures not yet on the server).",
      ),
    );
  });

  it("calls out the device that is still reporting while nothing leaves it", async () => {
    // The state that looked GREEN before: fresh last-heard, a queue that has
    // never moved. A 401-paused device reports for as long as anyone holds it.
    vi.mocked(getCarnival).mockResolvedValue(carnival("Closed"));
    vi.mocked(listCarnivalDevices).mockResolvedValue(
      fleet([device({ lastDrainUtc: null, pendingCount: 6 })]),
    );
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect(await screen.findByText("not draining")).toBeTruthy();
    expect(screen.getByText(/reporting but not draining/)).toBeTruthy();
    const zone = screen.getByRole("button", { name: "Finalise carnival" }).closest(".danger-zone")!;
    expect(zone.textContent).toContain("6 captures");
    expect(zone.textContent).toContain("signed-out");
  });

  it("does not call a failed device read an empty fleet", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Closed"));
    vi.mocked(listCarnivalDevices).mockRejectedValue(new Error("nope"));
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const finalise = await screen.findByRole("button", { name: "Finalise carnival" });
    await waitFor(() =>
      expect(screen.queryByText(/This list could not be read/)).toBeTruthy(),
    );
    expect(screen.queryByText(/No device has reported a queue/)).toBeNull();
    expect(finalise.closest(".danger-zone")!.textContent).toContain(
      "verify devices by hand before finalising",
    );
  });
});
