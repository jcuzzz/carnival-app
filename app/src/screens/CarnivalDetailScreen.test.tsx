// @vitest-environment jsdom
import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { _resetReachabilityForTests } from "../api/reachability";
import type { Carnival, CarnivalEvent, CarnivalSettings } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { _resetSelectedCarnivalForTests } from "../carnivals/selectedCarnival";
import { renderWithQuery } from "../test/queryWrapper";

/**
 * The deck's empty state (ADR-0054 §1): a carnival with no events is a setup
 * problem, and the one button on it names the step that is actually blocking —
 * "Set the carnival up" was a link to four equal choices.
 */

vi.mock("../api/resources", () => ({
  assignOfficials: vi.fn(),
  changeEventStatus: vi.fn(),
  getCarnival: vi.fn(),
  getCarnivalSettings: vi.fn(),
  listCarnivalEvents: vi.fn(),
}));

import { getCarnival, getCarnivalSettings, listCarnivalEvents } from "../api/resources";
import CarnivalDetailScreen from "./CarnivalDetailScreen";

const carnival = {
  id: "c-1",
  name: "Preston Swimming",
  date: "2027-03-06",
  status: "Draft",
  type: "Swimming",
  ruleVersion: "2027.1",
} as Carnival;

const noProfile: CarnivalSettings = {
  divisionsDefault: null,
  rosterAgeGroups: [],
  rosterYear: null,
  scoring: {} as CarnivalSettings["scoring"],
  adjustmentCategories: [],
  laneCount: 8,
  autoFillLanes: true,
  divisions: null,
};

const withProfile: CarnivalSettings = {
  ...noProfile,
  divisions: { ageGroups: ["12"], categories: ["M"], tiers: ["Competitive"] },
};

beforeEach(() => {
  cleanup();
  _resetReachabilityForTests();
  _resetSelectedCarnivalForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  vi.mocked(getCarnival).mockReset().mockResolvedValue(carnival);
  vi.mocked(listCarnivalEvents).mockReset().mockResolvedValue([]);
  vi.mocked(getCarnivalSettings).mockReset().mockResolvedValue(noProfile);
  signIn({ subject: "s1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
});

afterEach(() => {
  signOut();
});

describe("CarnivalDetailScreen — the empty deck", () => {
  it("sends the organiser to divisions when the carnival has no profile", async () => {
    renderWithQuery(<CarnivalDetailScreen carnivalId="c-1" />);

    const link = await screen.findByRole("link", { name: "Add divisions" });
    expect(link.getAttribute("href")).toBe("#/carnivals/c-1/setup/divisions");
  });

  it("sends them to events once the divisions are set", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue(withProfile);
    renderWithQuery(<CarnivalDetailScreen carnivalId="c-1" />);

    const link = await screen.findByRole("link", { name: "Add events" });
    expect(link.getAttribute("href")).toBe("#/carnivals/c-1/setup/events");
  });

  it("reads nothing extra once the carnival has events", async () => {
    vi.mocked(listCarnivalEvents).mockResolvedValue([
      { id: "e-1", carnivalId: "c-1", eventDefinitionId: "def-1", status: "Scheduled" } as CarnivalEvent,
    ]);
    renderWithQuery(<CarnivalDetailScreen carnivalId="c-1" />);

    await screen.findByText(/Find an event/);
    expect(screen.queryByText(/No events scheduled yet/)).toBeNull();
    // The divisions profile is the empty state's read alone — the deck works
    // with no uplink, and every read it makes is one an official pays for.
    expect(getCarnivalSettings).not.toHaveBeenCalled();
  });

  it("offers no setup link to someone who could not act on it", async () => {
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    renderWithQuery(<CarnivalDetailScreen carnivalId="c-1" />);

    expect(await screen.findByText(/No events scheduled yet/)).toBeTruthy();
    expect(screen.queryByRole("link", { name: /Add/ })).toBeNull();
    expect(getCarnivalSettings).not.toHaveBeenCalled();
  });
});
