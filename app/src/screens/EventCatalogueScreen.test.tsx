// @vitest-environment jsdom
import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { _resetReachabilityForTests } from "../api/reachability";
import type { EventDefinition } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { renderWithQuery } from "../test/queryWrapper";

/**
 * The catalogue's carnival-less home (ADR-0055 §2): one list across sports,
 * grouped, filtered, and carrying the blast radius of each definition.
 */

vi.mock("../api/resources", () => ({
  listEventDefinitions: vi.fn(),
}));

import { listEventDefinitions } from "../api/resources";
import EventCatalogueScreen from "./EventCatalogueScreen";

function definition(over: Partial<EventDefinition>): EventDefinition {
  return {
    id: "def-1",
    code: "SW-50-FREE",
    name: "Freestyle 50m",
    sport: "Swimming",
    measureType: "Time",
    units: "seconds",
    attemptCount: null,
    ladder: null,
    attemptsPerHeight: null,
    isActive: true,
    scoringWeight: 1,
    format: "Individual",
    eligibilityJson: null,
    legsJson: null,
    recordCategoriesJson: null,
    tagsJson: null,
    teamSize: null,
    usage: { carnivals: 0, divisions: 0, schoolRecords: 0, results: 0 },
    ...over,
  };
}

const catalogue = [
  definition({
    id: "def-free",
    usage: { carnivals: 3, divisions: 84, schoolRecords: 2, results: 0 },
  }),
  definition({ id: "def-fly", code: "SW-50-FLY", name: "Butterfly 50m" }),
  definition({
    id: "def-lj",
    code: "AT-LJ",
    name: "Long jump",
    sport: "Athletics",
    measureType: "Distance",
    units: "metres",
    attemptCount: 3,
  }),
  definition({ id: "def-novelty", code: "XX-SACK", name: "Sack race", sport: "Novelty" }),
  definition({ id: "def-old", code: "SW-OLD", name: "Retired swim", isActive: false }),
];

function groupHeadings(): (string | null)[] {
  return [...document.querySelectorAll("h2")].map((h) => h.textContent);
}

function rowTitles(): (string | null)[] {
  return [...document.querySelectorAll(".row-list .row-title")].map((row) => row.textContent);
}

beforeEach(() => {
  cleanup();
  _resetReachabilityForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  signIn({ subject: "s1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
  vi.mocked(listEventDefinitions).mockResolvedValue(catalogue);
});

afterEach(() => {
  cleanup();
  signOut();
  vi.clearAllMocks();
});

describe("EventCatalogueScreen", () => {
  it("groups every definition by sport, unsported last, and reads inactive in", async () => {
    renderWithQuery(<EventCatalogueScreen />);

    await screen.findByText("Long jump");
    expect(vi.mocked(listEventDefinitions).mock.calls[0]?.[0]).toBe(true);
    expect(groupHeadings()).toEqual(["Athletics", "Novelty", "Swimming"]);
    expect(rowTitles()).toEqual([
      "Long jump",
      "Sack race",
      "Butterfly 50m",
      "Freestyle 50m",
      "Retired swim",
    ]);
    expect(screen.getByText("Retired")).toBeTruthy();
  });

  it("writes the event type, the attempt count and the carnivals each is used by", async () => {
    renderWithQuery(<EventCatalogueScreen />);

    const jump = await screen.findByText("Long jump");
    const row = jump.closest("li")!;
    expect(row.textContent).toContain("AT-LJ · Best of 3 attempts (metres)");
    expect(row.textContent).toContain("Not used by any carnival yet");

    const free = screen.getByText("Freestyle 50m").closest("li")!;
    expect(free.textContent).toContain("SW-50-FREE · Timed (seconds)");
    expect(free.textContent).not.toContain("attempts");
    expect(free.textContent).toContain("Used by 3 carnivals");
  });

  it("narrows by sport and by name, and says how much is showing", async () => {
    renderWithQuery(<EventCatalogueScreen />);
    await screen.findByText("Long jump");
    expect(screen.getByText("Showing 5 of 5 events")).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Sport"), { target: { value: "Swimming" } });
    expect(groupHeadings()).toEqual(["Swimming"]);
    expect(screen.getByText("Showing 3 of 5 events")).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Filter events by name"), {
      target: { value: "fly" },
    });
    expect(rowTitles()).toEqual(["Butterfly 50m"]);

    fireEvent.click(screen.getByRole("button", { name: "Clear" }));
    expect(screen.getByText("Showing 5 of 5 events")).toBeTruthy();
  });

  it("says nothing matches rather than showing an empty group", async () => {
    renderWithQuery(<EventCatalogueScreen />);
    await screen.findByText("Long jump");

    fireEvent.change(screen.getByLabelText("Filter events by name"), {
      target: { value: "kayak" },
    });
    expect(screen.getByText("No events match these filters.")).toBeTruthy();
    expect(groupHeadings()).toEqual([]);
  });

  it("offers the create as a primary link into /school/events/new", async () => {
    renderWithQuery(<EventCatalogueScreen />);
    await screen.findByText("Freestyle 50m");

    const link = screen.getByRole("link", { name: "New definition" });
    expect(link.getAttribute("href")).toBe("#/school/events/new");
    expect(link.className).toContain("primary-link");
    expect(screen.getByText("Freestyle 50m").closest("a")!.getAttribute("href")).toBe(
      "#/school/events/def-free",
    );
  });

  it("names the missing role and never asks the server", async () => {
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    renderWithQuery(<EventCatalogueScreen />);

    expect(
      await screen.findByText(/Administering the event catalogue needs the carnival administrator/),
    ).toBeTruthy();
    expect(vi.mocked(listEventDefinitions)).not.toHaveBeenCalled();
  });

  // A lapsed session is the ordinary way to arrive here, and a named role is
  // unanswerable advice to somebody who is simply signed out.
  it("offers a sign-in to a signed-out reader rather than naming a role", async () => {
    signOut();
    renderWithQuery(<EventCatalogueScreen />);

    expect(await screen.findByRole("link", { name: "Sign in" })).toBeTruthy();
    expect(screen.queryByText(/needs the carnival administrator role/)).toBeNull();
    expect(vi.mocked(listEventDefinitions)).not.toHaveBeenCalled();
  });
});
