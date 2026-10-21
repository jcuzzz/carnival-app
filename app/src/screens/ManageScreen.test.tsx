// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
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
  applyCarnivalTemplate: vi.fn(),
  changeCarnivalStatus: vi.fn(),
  getCarnival: vi.fn(),
  getCarnivalSchedule: vi.fn(),
  getCarnivalSettings: vi.fn(),
  getProgress: vi.fn(),
  listAdjustments: vi.fn(),
  listCarnivalDevices: vi.fn(),
  listCarnivalEvents: vi.fn(),
  listEventDefinitions: vi.fn(),
  listEventTemplates: vi.fn(),
  openScheduledEvents: vi.fn(),
}));

import {
  applyCarnivalTemplate,
  changeCarnivalStatus,
  getCarnival,
  getCarnivalSchedule,
  getCarnivalSettings,
  getProgress,
  listAdjustments,
  listCarnivalDevices,
  listCarnivalEvents,
  listEventDefinitions,
  listEventTemplates,
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
  return { id, carnivalId: "c-1", eventDefinitionId: "def-1", status } as CarnivalEvent;
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
  vi.mocked(listEventTemplates).mockReset().mockResolvedValue([]);
  vi.mocked(applyCarnivalTemplate).mockReset();
  vi.mocked(openScheduledEvents).mockReset().mockResolvedValue({ opened: 0, eventIds: [] });
  signIn({ subject: "s1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
});

afterEach(() => {
  signOut();
});

describe("ManageScreen", () => {
  it("gathers the setup surfaces the deck no longer carries", async () => {
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect((await screen.findByRole("link", { name: /Divisions/ })).getAttribute("href")).toBe(
      "#/carnivals/c-1/setup/divisions",
    );
    expect(screen.getByRole("link", { name: /Events/ }).getAttribute("href")).toBe(
      "#/carnivals/c-1/setup/events",
    );
    expect(screen.getByRole("link", { name: /Program/ }).getAttribute("href")).toBe(
      "#/carnivals/c-1/schedule",
    );
  });

  // ---- The setup checklist (ADR-0054 §1) ----

  it("orders the setup steps and points at the first one still to do", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const roster = await screen.findByText(/^1\. Roster/);
    const row = roster.closest("li")!;
    expect(row.textContent).toContain("Done");
    expect(row.textContent).toContain("412 students in the promoted roster");
    expect(row.className).not.toContain("awaiting");

    // Nothing is configured past the roster, so Divisions is what to do next —
    // and it is the ONLY step marked so.
    const divisions = screen.getByText(/^2\. Divisions/).closest("li")!;
    expect(divisions.textContent).toContain("Do this next");
    expect(divisions.className).toContain("awaiting");
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

    await screen.findByText(/^1\. Roster/);
    expect(screen.getByText(/^2\. Divisions/).closest("li")!.textContent).toContain(
      "1 age · 1 category · 1 tier",
    );
    expect(screen.getByText(/^3\. Events/).closest("li")!.textContent).toContain(
      "1 in this carnival · 1 available to add",
    );
    expect(screen.getByText(/^4\. Program/).closest("li")!.textContent).toContain(
      "1 slot across 1 stream",
    );
    // Open: the carnival is Open, and one division is still Scheduled.
    expect(screen.getByText(/^5\. Open/).closest("li")!.textContent).toContain(
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

    const program = (await screen.findByText(/^4\. Program/)).closest("li")!;
    expect(program.textContent).toContain("Optional");
    expect(program.textContent).not.toContain("Do this next");
  });

  it("warns on the roster step when the roster's year is not the carnival's", async () => {
    vi.mocked(getProgress).mockResolvedValue(progress(412));
    vi.mocked(getCarnivalSettings).mockResolvedValue({ ...settings, rosterYear: 2026 });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const warning = await screen.findByText(/age groups are for 2026, but this carnival is in 2027/);
    expect(warning.className).toBe("alert warn");
    expect(warning.textContent).toContain("School → Roster import");
    const row = screen.getByText(/^1\. Roster/).closest("li")!;
    expect(row.contains(warning)).toBe(true);
    expect(row.textContent).toContain("Done");
  });

  it("raises no year warning when the roster is the carnival's year, or there is none", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({ ...settings, rosterYear: 2027 });
    // The lanes editor renders off the settings read, so the year has landed by then.
    const { unmount } = renderWithQuery(<ManageScreen carnivalId="c-1" />);
    await screen.findByRole("heading", { name: "Lanes" });
    expect(screen.queryByText(/age groups are for/)).toBeNull();
    unmount();

    vi.mocked(getCarnivalSettings).mockResolvedValue({ ...settings, rosterYear: null });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);
    await screen.findByRole("heading", { name: "Lanes" });
    expect(screen.queryByText(/age groups are for/)).toBeNull();
  });

  it("says so rather than guessing when the roster count can't be read", async () => {
    vi.mocked(getProgress).mockRejectedValue(new TypeError("Failed to fetch"));
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const roster = (await screen.findByText(/^1\. Roster/)).closest("li")!;
    expect(roster.textContent).toContain("Check School → Roster import");
    expect(roster.textContent).not.toContain("Done");
    // Unknown is not undone: the pointer moves on to the step that IS.
    expect(screen.getByText(/^2\. Divisions/).closest("li")!.textContent).toContain(
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

    await screen.findByRole("link", { name: /Divisions/ });
    expect(screen.getByText(/^1\. Roster/)).toBeTruthy();
    expect(screen.queryByRole("link", { name: /Roster/ })).toBeNull();
  });

  it("links the import screen for a system administrator", async () => {
    signIn({ subject: "s0", displayName: "Sam Sys", roles: ["SystemAdministrator"] });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    expect((await screen.findByRole("link", { name: /Roster/ })).getAttribute("href")).toBe(
      "#/import",
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

  // ---- Apply a template (ADR-0054 §2) ----

  it("pre-selects the template matching the carnival's type and states what it will do", async () => {
    vi.mocked(listEventTemplates).mockResolvedValue([
      { id: "athletics", name: "Athletics carnival", version: "v3", definitions: 14, readable: true },
      { id: "swimming", name: "Swimming carnival", version: "v3", definitions: 12, readable: true },
      { id: "broken", name: null, version: "v1", definitions: 0, readable: false },
    ]);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const picker = (await screen.findByLabelText("Carnival template")) as HTMLSelectElement;
    // A Swimming carnival, so swimming — not the first readable row.
    await waitFor(() => expect(picker.value).toBe("swimming"));
    expect([...picker.options].map((o) => o.value)).toEqual(["athletics", "swimming"]);
    const section = picker.closest("section")!;
    expect(section.textContent).toContain("created in the shared catalogue or reused");
    expect(section.textContent).toContain("divisions profile is seeded");
    expect(section.textContent).toContain("age × category × tier");
  });

  it("arms nothing when no template matches the carnival's type", async () => {
    vi.mocked(getCarnival).mockResolvedValue({ ...carnival(), type: "Other" });
    vi.mocked(listEventTemplates).mockResolvedValue([
      { id: "athletics", name: "Athletics carnival", version: "v3", definitions: 14, readable: true },
      { id: "swimming", name: "Swimming carnival", version: "v3", definitions: 12, readable: true },
    ]);
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    const picker = (await screen.findByLabelText("Carnival template")) as HTMLSelectElement;
    expect(picker.value).toBe("");
    expect(picker.options[0]!.text).toBe("Choose a template…");
    const apply = screen.getByRole("button", { name: "Apply template" }) as HTMLButtonElement;
    expect(apply.disabled).toBe(true);

    // And it arms once the organiser says which one.
    fireEvent.change(picker, { target: { value: "athletics" } });
    await waitFor(() => expect(apply.disabled).toBe(false));
  });

  it("applies the chosen template to the carnival and reports what it did", async () => {
    vi.mocked(listEventTemplates).mockResolvedValue([
      { id: "swimming", name: "Swimming carnival", version: "v3", definitions: 12, readable: true },
    ]);
    vi.mocked(applyCarnivalTemplate).mockResolvedValue({
      carnivalId: "c-1",
      template: "swimming",
      templateName: "Swimming carnival",
      templateVersion: "v3",
      definitionsCreated: 12,
      definitionsExisting: 0,
      definitionsSkippedInactive: [],
      divisionProfileSeeded: true,
      eventsCreated: 96,
      eventsAlreadyScheduled: 0,
      eventsPlaced: 96,
      slotsCreated: 9,
      streamsCreated: 2,
      ageGroups: [],
      categories: [],
      tiers: [],
    });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    await screen.findByLabelText("Carnival template");
    fireEvent.click(screen.getByRole("button", { name: "Apply template" }));

    await waitFor(() => expect(applyCarnivalTemplate).toHaveBeenCalledWith("c-1", "swimming"));
    expect(await screen.findByText(/96 divisions/)).toBeTruthy();
    expect(getToasts().map((t) => t.message)).toContain(
      "Swimming carnival applied — 96 divisions scheduled, 9 program slots built.",
    );
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

  it("offers the bulk open in the same press that opens the carnival", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("Draft"));
    vi.mocked(changeCarnivalStatus).mockResolvedValue(carnival("Open"));
    vi.mocked(listCarnivalEvents).mockResolvedValue([division("e-1"), division("e-2")]);
    vi.mocked(openScheduledEvents).mockResolvedValue({ opened: 2, eventIds: ["e-1", "e-2"] });
    renderWithQuery(<ManageScreen carnivalId="c-1" />);

    // Both offered: opening the carnival alone stays the plain press.
    expect(await screen.findByRole("button", { name: "Open carnival" })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Open carnival and all 2 divisions" }));

    await waitFor(() => expect(changeCarnivalStatus).toHaveBeenCalledWith("c-1", "Open"));
    await waitFor(() => expect(openScheduledEvents).toHaveBeenCalledWith("c-1"));
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
