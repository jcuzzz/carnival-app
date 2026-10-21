// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { _resetReachabilityForTests } from "../api/reachability";
import type { Carnival, CarnivalEvent, CarnivalSettings, EventDefinition } from "../api/types";
import { keys } from "../api/queryKeys";
import { signIn, signOut } from "../auth/session";
import { _resetToastsForTests, getToasts } from "../shell/toast";
import { createTestQueryClient, renderWithQuery } from "../test/queryWrapper";

/**
 * The setup list (ADR-0022 Stage A). One row per EVENT, never per division —
 * and the row an administrator came here for is the one with nothing behind it
 * yet, so a definition this carnival contests none of must still be listed.
 */

vi.mock("../api/resources", () => ({
  generateDivisions: vi.fn(),
  getCarnival: vi.fn(),
  getCarnivalSettings: vi.fn(),
  listCarnivalEvents: vi.fn(),
  listEventDefinitions: vi.fn(),
  updateCarnivalSettings: vi.fn(),
}));

import {
  generateDivisions,
  getCarnival,
  getCarnivalSettings,
  listCarnivalEvents,
  listEventDefinitions,
  updateCarnivalSettings,
} from "../api/resources";
import EventSetupScreen from "./EventSetupScreen";

const carnival = {
  id: "c-1",
  name: "Swimming Carnival 2027",
  date: "2027-02-19",
  status: "Open",
  type: "Swimming",
} as Carnival;

const definitions = [
  {
    id: "def-free",
    code: "SW-50-FREE",
    name: "Freestyle 50m",
    sport: "Swimming",
    measureType: "Time",
    units: "seconds",
    isActive: true,
    scoringWeight: 1,
    format: "Individual",
    usage: { carnivals: 3, divisions: 84, schoolRecords: 2, results: 0 },
  } as EventDefinition,
  {
    id: "def-fly",
    code: "SW-50-FLY",
    name: "Butterfly 50m",
    sport: "Swimming",
    measureType: "Time",
    units: "seconds",
    isActive: true,
    scoringWeight: 1,
    format: "Individual",
    usage: { carnivals: 0, divisions: 0, schoolRecords: 0, results: 0 },
  } as EventDefinition,
];

const events = ["12", "13"].flatMap((age) =>
  (["M", "F"] as const).map(
    (category) =>
      ({
        id: `ev-${age}-${category}`,
        carnivalId: "c-1",
        eventDefinitionId: "def-free",
        definitionCode: "SW-50-FREE",
        definitionName: "Freestyle 50m",
        ageGroup: age,
        category,
        tier: "Competitive",
        status: "Open",
      }) as CarnivalEvent,
  ),
);

/** The profile the events above exactly cover — so nothing is outstanding. */
const settings: CarnivalSettings = {
  divisionsDefault: null,
  rosterAgeGroups: [],
  rosterYear: null,
  scoring: {} as CarnivalSettings["scoring"],
  adjustmentCategories: [],
  laneCount: null,
  autoFillLanes: false,
  divisions: {
    ageGroups: ["12", "13"],
    categories: ["M", "F"],
    tiers: ["Competitive"],
  },
};

beforeEach(() => {
  cleanup();
  _resetReachabilityForTests();
  _resetToastsForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  vi.mocked(generateDivisions)
    .mockReset()
    .mockResolvedValue({ created: 4, skipped: 0, eventDefinitionId: "def-fly", divisions: [] });
  vi.mocked(updateCarnivalSettings).mockReset().mockResolvedValue(settings);
  vi.mocked(getCarnival).mockReset().mockResolvedValue(carnival);
  vi.mocked(getCarnivalSettings).mockReset().mockResolvedValue(settings);
  vi.mocked(listCarnivalEvents).mockReset().mockResolvedValue(events);
  vi.mocked(listEventDefinitions).mockReset().mockResolvedValue(definitions);
});

afterEach(() => {
  signOut();
});

describe("EventSetupScreen", () => {
  it("summarises each event's divisions on one row and links to its editor", async () => {
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    const row = (await screen.findByText("Freestyle 50m")).closest("a")!;
    expect(row.textContent).toContain("SW-50-FREE");
    expect(row.textContent).toContain("4 divisions");
    expect(row.textContent).toContain("12–13");
    expect(row.textContent).toContain("Boys/Girls");
    expect(row.textContent).toContain("Comp");
    expect(row.getAttribute("href")).toBe("#/carnivals/c-1/setup/events/def-free");
  });

  it("lists an event the carnival contests nothing of, linked to its editor", async () => {
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    const row = (await screen.findByText("Butterfly 50m")).closest("a")!;
    // The list it is under says the carnival runs none of it; the line names
    // only the definition's own type.
    expect(row.querySelector(".row-sub")!.textContent).toBe("SW-50-FLY · Timed");
    expect(row.getAttribute("href")).toBe("#/carnivals/c-1/setup/events/def-fly");
  });

  it("keeps a retired event this carnival contests on the list, badged", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      { ...definitions[0]!, isActive: false },
      definitions[1]!,
    ]);
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    await screen.findByText("Freestyle 50m");
    expect(screen.getByText("Retired")).toBeTruthy();
    // The catalogue read must ASK for inactive rows, or the badge is unreachable.
    expect(vi.mocked(listEventDefinitions).mock.calls[0]).toEqual([true]);
  });

  it("carries the same usage counts the editor states", async () => {
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    const used = (await screen.findByText("Freestyle 50m")).closest("a")!;
    expect(used.textContent).toContain("Used by 3 carnivals · 84 divisions · 2 school records");
    const unused = screen.getByText("Butterfly 50m").closest("a")!;
    expect(unused.textContent).toContain("Not used by any carnival yet");
  });

  it("claims nothing about a row while the carnival's divisions are still in flight", async () => {
    vi.mocked(listCarnivalEvents).mockReturnValue(new Promise<CarnivalEvent[]>(() => {}));
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    const row = (await screen.findByText("Freestyle 50m")).closest("a")!;
    expect(row.querySelector(".row-sub")!.textContent).toBe("SW-50-FREE · Timed");
    expect(screen.queryByRole("heading", { name: "In this carnival" })).toBeNull();
    expect(screen.getByText(/Counting this carnival/)).toBeTruthy();
  });

  // The trap: the catalogue answers from cache, so nothing is "loading", while
  // the events read is still out and every row would read as uncontested.
  it("still says nothing when the catalogue is cached and the divisions are not", async () => {
    const client = createTestQueryClient();
    client.setQueryData(keys.eventDefinitions(true), definitions);
    vi.mocked(listCarnivalEvents).mockReturnValue(new Promise<CarnivalEvent[]>(() => {}));
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />, client);

    const row = (await screen.findByText("Butterfly 50m")).closest("a")!;
    expect(row.querySelector(".row-sub")!.textContent).toBe("SW-50-FLY · Timed");
    expect(screen.queryByRole("heading", { name: "Available to add" })).toBeNull();
  });
});

describe("EventSetupScreen — the sport dimension (ADR-0052 §9)", () => {
  const shotPut = {
    id: "def-shot",
    code: "AT-SHOT",
    name: "Shot Put",
    sport: "Athletics",
    measureType: "Distance",
    units: "m",
    isActive: true,
    scoringWeight: 1,
    format: "Individual",
    usage: { carnivals: 0, divisions: 0, schoolRecords: 0, results: 0 },
  } as EventDefinition;

  beforeEach(() => {
    vi.mocked(listEventDefinitions).mockResolvedValue([...definitions, shotPut]);
  });

  it("folds another sport's definitions behind a count, and shows them on request", async () => {
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    await screen.findByText("Butterfly 50m");
    expect(screen.queryByText("Shot Put")).toBeNull();
    const toggle = screen.getByRole("button", { name: "Show 1 from other sports" });
    expect(toggle.getAttribute("aria-expanded")).toBe("false");

    fireEvent.click(toggle);
    expect(screen.getByText("Shot Put").closest("a")!.getAttribute("href")).toBe(
      "#/carnivals/c-1/setup/events/def-shot",
    );
    fireEvent.click(screen.getByRole("button", { name: "Hide 1 from other sports" }));
    expect(screen.queryByText("Shot Put")).toBeNull();
  });

  it("says the catalogue holds no event of this sport rather than showing nothing", async () => {
    // An athletics carnival against a swimming-only catalogue: every row folds,
    // and a bare "Show 2 from other sports" button reads as a broken screen.
    vi.mocked(getCarnival).mockResolvedValue({ ...carnival, type: "Athletics" } as Carnival);
    vi.mocked(listCarnivalEvents).mockResolvedValue([]);
    vi.mocked(listEventDefinitions).mockResolvedValue(definitions);
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    expect(await screen.findByText("No Athletics events in the catalogue yet.")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Show 2 from other sports" })).toBeTruthy();
  });

  it("says nothing of the kind when the sport's own events are listed", async () => {
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    await screen.findByText("Butterfly 50m");
    expect(screen.queryByText(/in the catalogue yet/)).toBeNull();
  });

  it("folds nothing while the carnival's divisions are still unread", async () => {
    // Absence from this carnival is what makes a foreign-sport row foldable,
    // and an unanswered events read cannot claim it.
    vi.mocked(listCarnivalEvents).mockReturnValue(new Promise<CarnivalEvent[]>(() => {}));
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    await screen.findByText("Shot Put");
    expect(screen.queryByRole("button", { name: /from other sports/ })).toBeNull();
  });
});

describe("EventSetupScreen — the profile review prompt (ADR-0022 §8)", () => {
  function withAge(code: string) {
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...settings,
      divisions: {
        ...settings.divisions!,
        ageGroups: [...settings.divisions!.ageGroups!, code],
      },
    });
  }

  it("names the event that does not contest an age the profile declares", async () => {
    withAge("14");
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    const prompt = await screen.findByRole("status");
    expect(prompt.textContent).toContain("A profile edit creates no divisions");
    const link = screen.getByText(/Freestyle 50m — no divisions for 14/);
    expect(link.getAttribute("href")).toBe("#/carnivals/c-1/setup/events/def-free");
    // Butterfly is in no division of this carnival, so it is not behind on one.
    expect(prompt.textContent).not.toContain("Butterfly 50m");
  });

  it("renders nothing at all when nothing is outstanding", async () => {
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    await screen.findByText("Freestyle 50m");
    expect(screen.queryByRole("status")).toBeNull();
  });

  it("claims nothing while the carnival's divisions are still in flight", async () => {
    withAge("14");
    vi.mocked(listCarnivalEvents).mockReturnValue(new Promise<CarnivalEvent[]>(() => {}));
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    await screen.findByText("Freestyle 50m");
    expect(screen.queryByRole("status")).toBeNull();
  });
});

describe("EventSetupScreen — adding an event", () => {
  it("offers the control to an administrator of an open carnival", async () => {
    signIn({ subject: "admin-1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    const add = await screen.findByText("New event…");
    expect(add.getAttribute("href")).toBe("#/carnivals/c-1/setup/events/new");
    expect(screen.getByText("Divisions profile").getAttribute("href")).toBe(
      "#/carnivals/c-1/setup/divisions",
    );
  });

  it("withholds it from someone who could not use it, and from a closed carnival", async () => {
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);
    await screen.findByText("Freestyle 50m");
    expect(screen.queryByText("New event…")).toBeNull();

    cleanup();
    signIn({ subject: "admin-1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
    vi.mocked(getCarnival).mockResolvedValue({ ...carnival, status: "Closed" });
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);
    await screen.findByText("Freestyle 50m");
    expect(screen.queryByText("New event…")).toBeNull();
  });
});

/**
 * ADR-0059: the screen is two lists, and putting a catalogue event in this
 * carnival is one press — the whole profile matrix, through the endpoint the
 * new-event screen's Create already uses.
 */
describe("EventSetupScreen — Available to add (ADR-0059)", () => {
  const rosterDefault: NonNullable<CarnivalSettings["divisions"]> = {
    ageGroups: ["12", "13"],
    categories: ["M", "F"],
    tiers: ["Competitive"],
  };
  const onDefault: CarnivalSettings = {
    ...settings,
    divisions: null,
    divisionsDefault: rosterDefault,
    rosterAgeGroups: ["12", "13"],
  };
  const fullMatrix = [
    { ageGroup: "12", category: "M", tier: "Competitive" },
    { ageGroup: "12", category: "F", tier: "Competitive" },
    { ageGroup: "13", category: "M", tier: "Competitive" },
    { ageGroup: "13", category: "F", tier: "Competitive" },
  ];

  function admin(): void {
    signIn({ subject: "admin-1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
  }

  function list(name: string): HTMLElement {
    return screen.getByRole("heading", { name }).closest("section")!;
  }

  /** What the events read answers once the press has landed. */
  function withButterfly(): CarnivalEvent[] {
    return [
      ...events,
      ...fullMatrix.map(
        (spec, i) =>
          ({
            id: `ev-fly-${i}`,
            carnivalId: "c-1",
            eventDefinitionId: "def-fly",
            definitionCode: "SW-50-FLY",
            definitionName: "Butterfly 50m",
            ageGroup: spec.ageGroup,
            category: spec.category,
            tier: "Competitive",
            status: "Scheduled",
          }) as CarnivalEvent,
      ),
    ];
  }

  it("splits the catalogue into what runs here and what is one press away", async () => {
    admin();
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    await screen.findByText("Freestyle 50m");
    expect(within(list("In this carnival")).getByText("Freestyle 50m")).toBeTruthy();
    expect(within(list("In this carnival")).queryByText("Butterfly 50m")).toBeNull();
    const available = list("Available to add");
    expect(within(available).getByText("Butterfly 50m")).toBeTruthy();
    // The row an administrator is here for carries the press, and names it.
    expect(within(available).getByRole("button", { name: "Add Butterfly 50m" })).toBeTruthy();
    expect(within(list("In this carnival")).queryByRole("button", { name: /^Add / })).toBeNull();
  });

  it("generates every division the profile contests and moves the row", async () => {
    admin();
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);
    const add = await screen.findByRole("button", { name: "Add Butterfly 50m" });

    vi.mocked(listCarnivalEvents).mockResolvedValue(withButterfly());
    fireEvent.click(add);

    await waitFor(() => expect(generateDivisions).toHaveBeenCalledTimes(1));
    expect(vi.mocked(generateDivisions).mock.calls[0]).toEqual([
      "c-1",
      { eventDefinitionId: "def-fly", divisions: fullMatrix },
    ]);
    expect(getToasts()[0]!.message).toBe("Butterfly 50m — 4 divisions added.");
    await waitFor(() =>
      expect(within(list("In this carnival")).getByText("Butterfly 50m")).toBeTruthy(),
    );
    expect(
      within(list("In this carnival")).getByText("Butterfly 50m").closest("a")!.textContent,
    ).toContain("4 divisions");
  });

  // ADR-0057 §2, exactly as Create does it: the ages are saved first, so the
  // divisions that follow are the ones the carnival now declares.
  it("saves the roster default as the profile before it generates", async () => {
    admin();
    vi.mocked(getCarnivalSettings).mockResolvedValue(onDefault);
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Add Butterfly 50m" }));

    await waitFor(() => expect(generateDivisions).toHaveBeenCalledTimes(1));
    expect(vi.mocked(updateCarnivalSettings).mock.calls[0]).toEqual([
      "c-1",
      { divisions: rosterDefault },
    ]);
    expect(vi.mocked(updateCarnivalSettings).mock.invocationCallOrder[0]!).toBeLessThan(
      vi.mocked(generateDivisions).mock.invocationCallOrder[0]!,
    );
  });

  it("writes nothing when a profile was saved elsewhere since the screen loaded", async () => {
    admin();
    vi.mocked(getCarnivalSettings).mockResolvedValue(onDefault);
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);
    const add = await screen.findByRole("button", { name: "Add Butterfly 50m" });

    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...settings,
      divisions: { ageGroups: ["16"], categories: ["M"], tiers: ["Competitive"] },
    });
    fireEvent.click(add);

    expect(
      (await screen.findByText(/The ages were set elsewhere since this screen loaded/)).textContent,
    ).toContain("press Add again");
    expect(updateCarnivalSettings).not.toHaveBeenCalled();
    expect(generateDivisions).not.toHaveBeenCalled();
  });

  it("shows the server's refusal beside the row and adds nothing", async () => {
    admin();
    vi.mocked(generateDivisions).mockRejectedValue(
      new ApiError(
        409,
        { error: "EVENT_DEFINITION_INACTIVE", detail: "That event is retired." },
        "/x",
      ),
    );
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Add Butterfly 50m" }));

    const refusal = await screen.findByRole("alert");
    expect(refusal.textContent).toBe("That event is retired.");
    expect(refusal.closest("li")!.textContent).toContain("Butterfly 50m");
  });

  it("disables Add with neither a profile nor a roster default, and says what to do", async () => {
    admin();
    vi.mocked(getCarnivalSettings).mockResolvedValue({ ...settings, divisions: null });
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    const add = await screen.findByRole("button", { name: "Add Butterfly 50m" });
    expect((add as HTMLButtonElement).disabled).toBe(true);
    const notice = within(list("Available to add")).getByRole("status");
    expect(notice.textContent).toContain("Import a roster or add ages first.");
    expect(within(notice).getByRole("link", { name: "Divisions setup" }).getAttribute("href")).toBe(
      "#/carnivals/c-1/setup/divisions",
    );
    fireEvent.click(add);
    expect(generateDivisions).not.toHaveBeenCalled();
  });

  it("disables Add with no server to add against", async () => {
    admin();
    Object.defineProperty(navigator, "onLine", { value: false, configurable: true });
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    const add = await screen.findByRole("button", { name: "Add Butterfly 50m" });
    expect((add as HTMLButtonElement).disabled).toBe(true);
    expect(screen.getByText(/Can’t reach the server/)).toBeTruthy();
  });

  // Withheld, not disabled — as New event… is, and with it the notice that
  // only tells a manager what to do.
  it("shows no Add at all to someone who could not use it, and no notice either", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({ ...settings, divisions: null });
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    await screen.findByText("Butterfly 50m");
    expect(screen.queryByRole("button", { name: /^Add / })).toBeNull();
    expect(screen.queryByText("Import a roster or add ages first.")).toBeNull();
  });

  it("disables Add on a closed carnival", async () => {
    admin();
    vi.mocked(getCarnival).mockResolvedValue({ ...carnival, status: "Closed" });
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    await screen.findByText("Butterfly 50m");
    expect(
      ((await screen.findByRole("button", { name: "Add Butterfly 50m" })) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
  });

  // The press's own row has gone to the other list, so the keyboard needs
  // somewhere to be — the list it went to.
  it("moves focus to In this carnival once the added row lands there", async () => {
    admin();
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);
    const add = await screen.findByRole("button", { name: "Add Butterfly 50m" });

    vi.mocked(listCarnivalEvents).mockResolvedValue(withButterfly());
    fireEvent.click(add);

    await waitFor(() =>
      expect(within(list("In this carnival")).getByText("Butterfly 50m")).toBeTruthy(),
    );
    expect(document.activeElement).toBe(screen.getByRole("heading", { name: "In this carnival" }));
  });

  it("says nothing was ADDED when the ages could not be saved", async () => {
    admin();
    vi.mocked(getCarnivalSettings).mockResolvedValue(onDefault);
    vi.mocked(updateCarnivalSettings).mockRejectedValue(new ApiError(500, {}, "/x"));
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Add Butterfly 50m" }));

    expect((await screen.findByRole("alert")).textContent).toContain(
      "The ages couldn’t be saved, so nothing was added",
    );
    expect(generateDivisions).not.toHaveBeenCalled();
  });

  // Add generates divisions and a retired definition takes none, so it is on no
  // add list at all (ADR-0059 §1).
  it("never offers a retired definition to add", async () => {
    admin();
    vi.mocked(listEventDefinitions).mockResolvedValue([
      definitions[0]!,
      { ...definitions[1]!, isActive: false },
    ]);
    renderWithQuery(<EventSetupScreen carnivalId="c-1" />);

    await screen.findByText("Freestyle 50m");
    expect(screen.queryByText("Butterfly 50m")).toBeNull();
    expect(screen.queryByRole("button", { name: /^Add / })).toBeNull();
  });
});
