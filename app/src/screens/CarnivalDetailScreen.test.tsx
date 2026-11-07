// @vitest-environment jsdom
import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { _resetReachabilityForTests } from "../api/reachability";
import type { Carnival, CarnivalEvent } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { _resetSelectedCarnivalForTests } from "../carnivals/selectedCarnival";
import { renderWithQuery } from "../test/queryWrapper";

/**
 * The Deck's empty state (ADR-0066 §7): the Deck is for the day, so a carnival
 * with no events sends a manager to Manage with one link and tells anyone else
 * it is still being set up. It never names a setup step.
 */

vi.mock("../api/resources", () => ({
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

beforeEach(() => {
  cleanup();
  _resetReachabilityForTests();
  _resetSelectedCarnivalForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  vi.mocked(getCarnival).mockReset().mockResolvedValue(carnival);
  vi.mocked(listCarnivalEvents).mockReset().mockResolvedValue([]);
  vi.mocked(getCarnivalSettings).mockReset();
  signIn({ subject: "s1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
});

afterEach(() => {
  signOut();
});

describe("CarnivalDetailScreen — the empty deck", () => {
  it("sends a manager to Manage with one link", async () => {
    renderWithQuery(<CarnivalDetailScreen carnivalId="c-1" />);

    const link = await screen.findByRole("link", { name: "Set the carnival up" });
    expect(link.getAttribute("href")).toBe("#/carnivals/c-1/manage");
    expect(link.className).toBe("button primary-link");
    expect(screen.getByText("This carnival isn't set up yet.")).toBeTruthy();
    expect(screen.getAllByRole("link")).toHaveLength(1);
    expect(screen.queryByRole("link", { name: /Add divisions/ })).toBeNull();
    // The Deck reads nothing for setup: every read it makes is one an official pays for.
    expect(getCarnivalSettings).not.toHaveBeenCalled();
  });

  it("reads nothing extra once the carnival has events", async () => {
    vi.mocked(listCarnivalEvents).mockResolvedValue([
      { id: "e-1", carnivalId: "c-1", eventDefinitionId: "def-1", status: "Scheduled" } as CarnivalEvent,
    ]);
    renderWithQuery(<CarnivalDetailScreen carnivalId="c-1" />);

    await screen.findByText(/Find an event/);
    expect(screen.queryByText("This carnival isn't set up yet.")).toBeNull();
    expect(getCarnivalSettings).not.toHaveBeenCalled();
  });

  it("tells anyone else the carnival is still being set up, with no link", async () => {
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    renderWithQuery(<CarnivalDetailScreen carnivalId="c-1" />);

    expect(
      await screen.findByText("Nothing to capture yet: this carnival is still being set up."),
    ).toBeTruthy();
    expect(screen.queryByRole("link")).toBeNull();
  });

  it.each(["Open", "Closed", "Finalised"] as const)(
    "says only that an empty %s carnival has no events",
    async (status) => {
      vi.mocked(getCarnival).mockResolvedValue({ ...carnival, status });
      renderWithQuery(<CarnivalDetailScreen carnivalId="c-1" />);

      expect(await screen.findByText("No events in this carnival.")).toBeTruthy();
      expect(screen.queryByText("This carnival isn't set up yet.")).toBeNull();
      expect(screen.queryByRole("link")).toBeNull();
    },
  );
});
