// @vitest-environment jsdom
import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import type { QueryClient } from "@tanstack/react-query";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { keys } from "../api/queryKeys";
import { _resetReachabilityForTests } from "../api/reachability";
import type { Carnival, CarnivalSettings, EventDefinition } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { _resetToastsForTests, getToasts } from "../shell/toast";
import { createTestQueryClient, QueryWrapper, renderWithQuery } from "../test/queryWrapper";

/**
 * Add an event (ADR-0022 §8). The common path is type a name and press the
 * button, so what must hold: the whole matrix arrives ticked and the button
 * counts it, one press does both calls, and a generate that fails after the
 * definition landed says so rather than inviting a duplicate code.
 */

vi.mock("../api/resources", () => ({
  createEventDefinition: vi.fn(),
  generateDivisions: vi.fn(),
  getCarnival: vi.fn(),
  getCarnivalSettings: vi.fn(),
  listEventDefinitions: vi.fn(),
  unscheduleDivision: vi.fn(),
  updateCarnivalSettings: vi.fn(),
}));

import {
  createEventDefinition,
  generateDivisions,
  getCarnival,
  getCarnivalSettings,
  listEventDefinitions,
  updateCarnivalSettings,
} from "../api/resources";
import NewEventScreen from "./NewEventScreen";

const carnival = {
  id: "c-1",
  name: "Swimming Carnival 2027",
  date: "2027-02-19",
  status: "Open",
  type: "Swimming",
} as Carnival;

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

const created = {
  id: "def-fly",
  code: "SW-50-FLY",
  name: "Butterfly 50m",
  sport: "Swimming",
  measureType: "Time",
  units: "seconds",
  isActive: true,
} as EventDefinition;

function type(label: string, value: string): void {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

/** Read off the option rather than pinned here: it is deliberately unspellable. */
function otherSportValue(select: HTMLSelectElement): string {
  return [...select.options].find((o) => o.textContent === "Another sport…")!.value;
}

/** The profile a focus or reconnect refetch brings back. */
async function reprofile(
  client: QueryClient,
  ageGroups: NonNullable<CarnivalSettings["divisions"]>["ageGroups"],
): Promise<void> {
  vi.mocked(getCarnivalSettings).mockResolvedValue({
    ...settings,
    divisions: { ...settings.divisions!, ageGroups },
  });
  await act(async () => {
    await client.invalidateQueries({ queryKey: keys.carnivalSettings("c-1") });
  });
}

beforeEach(() => {
  cleanup();
  _resetReachabilityForTests();
  _resetToastsForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  location.hash = "#/carnivals/c-1/setup/events/new";
  signIn({ subject: "admin-1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
  vi.mocked(getCarnival).mockReset().mockResolvedValue(carnival);
  vi.mocked(getCarnivalSettings).mockReset().mockResolvedValue(settings);
  vi.mocked(createEventDefinition).mockReset().mockResolvedValue(created);
  vi.mocked(listEventDefinitions).mockReset().mockResolvedValue([created]);
  vi.mocked(generateDivisions).mockReset().mockResolvedValue({
    eventDefinitionId: "def-fly",
    divisions: [],
    created: 4,
    skipped: 0,
  });
  vi.mocked(updateCarnivalSettings).mockReset().mockResolvedValue(settings);
});

afterEach(() => {
  signOut();
});

describe("NewEventScreen", () => {
  it("arrives with the whole matrix ticked, and the button counts what it will make", async () => {
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    const boxes = await screen.findAllByRole("checkbox");
    expect(boxes).toHaveLength(4);
    expect(boxes.every((box) => (box as HTMLInputElement).checked)).toBe(true);
    expect(screen.getByRole("button", { name: "Create 4 divisions" })).toBeTruthy();

    fireEvent.click(screen.getByLabelText("12 · Boys"));
    expect(screen.getByRole("button", { name: "Create 3 divisions" })).toBeTruthy();
  });

  it("starts a fresh form when the route moves to another carnival", async () => {
    const other = { ...carnival, id: "c-2", name: "Athletics Carnival 2027" };
    const client = createTestQueryClient();
    // Both carnivals already cached, so nothing reloads in between to remount the form.
    client.setQueryData(keys.carnival("c-1"), carnival);
    client.setQueryData(keys.carnivalSettings("c-1"), settings);
    client.setQueryData(keys.carnival("c-2"), other);
    client.setQueryData(keys.carnivalSettings("c-2"), settings);
    const { rerender } = renderWithQuery(<NewEventScreen carnivalId="c-1" />, client);

    await screen.findByLabelText("Name");
    type("Name", "Butterfly 50m");
    rerender(
      <QueryWrapper client={client}>
        <NewEventScreen carnivalId="c-2" />
      </QueryWrapper>,
    );

    await screen.findByText(/Athletics Carnival 2027/);
    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("");
  });

  it("defaults the sport from the carnival rather than asking for it again", async () => {
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    const sport = (await screen.findByLabelText("Sport")) as HTMLInputElement;
    expect(sport.value).toBe("Swimming");
  });

  it("creates the definition, then generates exactly the ticked divisions", async () => {
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    await screen.findByLabelText("Name");
    type("Name", "Butterfly 50m");
    type("Code", "SW-50-FLY");
    fireEvent.click(screen.getByLabelText("13 · Girls"));
    fireEvent.click(screen.getByRole("button", { name: "Create 3 divisions" }));

    await waitFor(() => expect(createEventDefinition).toHaveBeenCalledTimes(1));
    expect(vi.mocked(createEventDefinition).mock.calls[0]![0]).toEqual({
      code: "SW-50-FLY",
      name: "Butterfly 50m",
      sport: "Swimming",
      measureType: "Time",
      units: "seconds",
      tagsJson: "[]",
    });
    await waitFor(() => expect(generateDivisions).toHaveBeenCalledTimes(1));
    expect(vi.mocked(generateDivisions).mock.calls[0]).toEqual([
      "c-1",
      {
        eventDefinitionId: "def-fly",
        divisions: [
          { ageGroup: "12", category: "M", tier: "Competitive" },
          { ageGroup: "12", category: "F", tier: "Competitive" },
          { ageGroup: "13", category: "M", tier: "Competitive" },
        ],
      },
    ]);
    await waitFor(() => expect(location.hash).toBe("#/carnivals/c-1/setup/events/def-fly"));
  });

  // The half-done state: the definition is real, so a retry must not send the
  // code a second time and collect EVENT_DEFINITION_CODE_EXISTS for it.
  it("keeps the created event when the generate fails, and retries only the generate", async () => {
    vi.mocked(generateDivisions).mockRejectedValueOnce(
      new ApiError(
        409,
        { error: "EVENT_DEFINITION_INACTIVE", detail: "That event is retired." },
        "/x",
      ),
    );
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    await screen.findByLabelText("Name");
    type("Name", "Butterfly 50m");
    type("Code", "SW-50-FLY");
    fireEvent.click(screen.getByRole("button", { name: "Create 4 divisions" }));

    const notice = await screen.findByRole("alert");
    expect(notice.textContent).toContain("Butterfly 50m (SW-50-FLY) was created");
    expect(notice.textContent).toContain("That event is retired.");
    expect(location.hash).toBe("#/carnivals/c-1/setup/events/new");
    expect((screen.getByLabelText("Code") as HTMLInputElement).disabled).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: "Add 4 divisions" }));
    await waitFor(() => expect(generateDivisions).toHaveBeenCalledTimes(2));
    expect(createEventDefinition).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(location.hash).toBe("#/carnivals/c-1/setup/events/def-fly"));
  });

  it("reports a failed create as a failure, with nothing created", async () => {
    vi.mocked(createEventDefinition).mockRejectedValue(
      new ApiError(
        409,
        { error: "EVENT_DEFINITION_CODE_EXISTS", detail: "Code SW-50-FLY is taken." },
        "/x",
      ),
    );
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    await screen.findByLabelText("Name");
    type("Name", "Butterfly 50m");
    type("Code", "SW-50-FLY");
    fireEvent.click(screen.getByRole("button", { name: "Create 4 divisions" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("Code SW-50-FLY is taken.");
    expect(alert.textContent).not.toContain("was created");
    expect(generateDivisions).not.toHaveBeenCalled();
    expect((screen.getByLabelText("Code") as HTMLInputElement).disabled).toBe(false);
  });

  // The tag field lives inside the <form>, so Enter is implicit submission: it
  // would create the event with the typed tag thrown away.
  it("adds the typed tag on Enter instead of submitting the form without it", async () => {
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    await screen.findByLabelText("Name");
    type("Name", "Butterfly 50m");
    type("Code", "SW-50-FLY");
    const field = screen.getByLabelText("Add a tag");
    fireEvent.change(field, { target: { value: "distance:50m" } });
    expect(fireEvent.keyDown(field, { key: "Enter" })).toBe(false);

    expect(screen.getByText("Distance: 50m")).toBeTruthy();
    expect((field as HTMLInputElement).value).toBe("");
    expect(createEventDefinition).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Create 4 divisions" }));
    await waitFor(() => expect(createEventDefinition).toHaveBeenCalledTimes(1));
    expect(vi.mocked(createEventDefinition).mock.calls[0]![0].tagsJson).toBe('["distance:50m"]');
  });

  // ADR-0054 §3: the defect this replaces is a press that created a catalogue
  // definition and scheduled NOTHING into the carnival, with a toast that said
  // "added".
  it("refuses to finish with nothing scheduled when every box is unticked", async () => {
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    await screen.findByLabelText("Name");
    type("Name", "Butterfly 50m");
    type("Code", "SW-50-FLY");
    for (const box of screen.getAllByRole("checkbox")) fireEvent.click(box);

    const submit = screen.getByRole("button", { name: "Tick at least one division" });
    expect((submit as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(submit);
    expect(createEventDefinition).not.toHaveBeenCalled();
  });

  it("says what happened to THIS carnival, not to the catalogue", async () => {
    vi.mocked(generateDivisions).mockResolvedValue({
      eventDefinitionId: "def-fly",
      divisions: [],
      created: 4,
      skipped: 0,
    });
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    await screen.findByLabelText("Name");
    type("Name", "Butterfly 50m");
    type("Code", "SW-50-FLY");
    fireEvent.click(screen.getByRole("button", { name: "Create 4 divisions" }));

    await waitFor(() => expect(generateDivisions).toHaveBeenCalledTimes(1));
    expect(getToasts().map((t) => t.message)).toContain("Butterfly 50m — 4 divisions scheduled.");
  });

  // A focus or reconnect refetch re-reads the profile under a mounted form. The
  // grid follows it; the button must count the grid, not a seeding from before.
  it("follows a profile that changes under a refetch, keeping deliberate unticks", async () => {
    const { client } = renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    await screen.findByRole("button", { name: "Create 4 divisions" });
    fireEvent.click(screen.getByLabelText("12 · Boys"));
    expect(screen.getByRole("button", { name: "Create 3 divisions" })).toBeTruthy();

    await reprofile(client, ["12", "13", "14"]);

    // The new age's boxes arrive ticked; the cleared one stays cleared.
    await waitFor(() => expect(screen.getAllByRole("checkbox")).toHaveLength(6));
    expect((screen.getByLabelText("12 · Boys") as HTMLInputElement).checked).toBe(false);
    expect((screen.getByLabelText("14 · Boys") as HTMLInputElement).checked).toBe(true);
    expect(screen.getByRole("button", { name: "Create 5 divisions" })).toBeTruthy();

    // And an age that goes away takes its count with it, rather than leaving the
    // button promising divisions the grid no longer has.
    await reprofile(client, ["13"]);
    await waitFor(() => expect(screen.getAllByRole("checkbox")).toHaveLength(2));
    expect(screen.getByRole("button", { name: "Create 2 divisions" })).toBeTruthy();
  });

  it("pre-ticks a profile that only arrives on the second try", async () => {
    vi.mocked(getCarnivalSettings).mockRejectedValueOnce(new TypeError("Failed to fetch"));
    const { client } = renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    // The read failed, so the screen does not KNOW there are no divisions: the
    // guard's claim would be false, but nothing can be created either.
    await screen.findByRole("button", { name: "Add divisions first" });
    expect(screen.queryByText(/add ages first/)).toBeNull();
    expect(screen.queryAllByRole("checkbox")).toHaveLength(0);

    await act(async () => {
      await client.invalidateQueries({ queryKey: keys.carnivalSettings("c-1") });
    });

    const boxes = await screen.findAllByRole("checkbox");
    expect(boxes).toHaveLength(4);
    expect(boxes.every((box) => (box as HTMLInputElement).checked)).toBe(true);
    expect(screen.getByRole("button", { name: "Create 4 divisions" })).toBeTruthy();
  });

  /**
   * ADR-0053 §1 under the 2026-09-17 amendment: Attempts belongs to the
   * best-of-attempts type alone. Blank there means a single typed mark.
   */
  it("offers Attempts on the attempts type only, defaulting it to three", async () => {
    vi.mocked(createEventDefinition).mockResolvedValue(created);
    vi.mocked(generateDivisions).mockResolvedValue({ created: 0, skipped: 0, divisions: [] } as never);
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);
    await screen.findByLabelText("Name");

    expect(screen.queryByLabelText("Attempts")).toBeNull();

    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Distance" } });
    expect((screen.getByLabelText("Attempts") as HTMLInputElement).value).toBe("3");

    type("Name", "Long Jump");
    type("Code", "AT-LONG-JUMP");
    type("Attempts", "2");
    fireEvent.click(screen.getByRole("button", { name: /^Create/ }));

    await waitFor(() => expect(createEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(createEventDefinition).mock.calls[0]![0]).toMatchObject({
      measureType: "Distance",
      attemptCount: 2,
    });
  });

  /**
   * The amendment: the bar-ladder type takes the two deck SUGGESTIONS in the
   * Attempts field's place, and seeds the shipped bar.
   */
  it("offers the suggestions in the Attempts field's place on a bar ladder", async () => {
    vi.mocked(createEventDefinition).mockResolvedValue(created);
    vi.mocked(generateDivisions).mockResolvedValue({ created: 0, skipped: 0, divisions: [] } as never);
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);
    await screen.findByLabelText("Name");

    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Height" } });
    expect(screen.queryByLabelText("Attempts")).toBeNull();
    expect((screen.getByLabelText("Suggested opening height") as HTMLInputElement).value).toBe("1.20");
    expect((screen.getByLabelText("Usual rise") as HTMLInputElement).value).toBe("0.05");

    type("Name", "High Jump");
    type("Code", "AT-HIGH-JUMP");
    fireEvent.click(screen.getByRole("button", { name: /^Create/ }));

    await waitFor(() => expect(createEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(createEventDefinition).mock.calls[0]![0]).toMatchObject({
      measureType: "Height",
      ladder: { start: 1.2, step: 0.05 },
    });
    expect(Object.keys(vi.mocked(createEventDefinition).mock.calls[0]![0])).not.toContain(
      "attemptCount",
    );
  });

  /**
   * The 2026-09-17 amendment: the capture mode is chosen by NAME and stored as
   * the measure, and both suggestions may be left blank without changing it.
   */
  it("names the three event types, and sends a bar ladder with no suggestions", async () => {
    vi.mocked(createEventDefinition).mockResolvedValue(created);
    vi.mocked(generateDivisions).mockResolvedValue({ created: 0, skipped: 0, divisions: [] } as never);
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);
    await screen.findByLabelText("Name");

    const select = screen.getByLabelText("Event type") as HTMLSelectElement;
    expect([...select.options].map((option) => option.text)).toEqual([
      "Race (timed)",
      "Field — best of attempts (distance)",
      "Field — bar ladder (vertical jump)",
    ]);

    fireEvent.change(select, { target: { value: "Height" } });
    expect(screen.getByText(/Suggestions for the deck/)).toBeTruthy();
    fireEvent.change(screen.getByLabelText("Suggested opening height"), { target: { value: "" } });
    fireEvent.change(screen.getByLabelText("Usual rise"), { target: { value: "" } });
    type("Name", "High Jump");
    type("Code", "AT-HIGH-JUMP");
    fireEvent.click(screen.getByRole("button", { name: /^Create/ }));

    await waitFor(() => expect(createEventDefinition).toHaveBeenCalled());
    const request = vi.mocked(createEventDefinition).mock.calls[0]![0];
    expect(request).toMatchObject({ measureType: "Height" });
    expect(Object.keys(request)).not.toContain("ladder");
    expect(Object.keys(request)).not.toContain("attemptCount");
    expect(request).toMatchObject({ attemptsPerHeight: 3 });
  });

  it("sends two attempts per height on a Height event, and none on any other", async () => {
    vi.mocked(createEventDefinition).mockResolvedValue(created);
    vi.mocked(generateDivisions).mockResolvedValue({ created: 0, skipped: 0, divisions: [] } as never);
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);
    await screen.findByLabelText("Name");

    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Height" } });
    fireEvent.change(screen.getByLabelText("Attempts per height"), { target: { value: "2" } });
    type("Name", "High Jump");
    type("Code", "AT-HIGH-JUMP");
    fireEvent.click(screen.getByRole("button", { name: /^Create/ }));
    await waitFor(() => expect(createEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(createEventDefinition).mock.calls[0]![0]).toMatchObject({
      attemptsPerHeight: 2,
    });

    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Distance" } });
    expect(screen.queryByLabelText("Attempts per height")).toBeNull();
  });

  it("clears the suggestions when the type moves off the bar ladder", async () => {
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);
    await screen.findByLabelText("Name");

    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Height" } });
    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Distance" } });
    expect(screen.queryByLabelText("Suggested opening height")).toBeNull();
    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Height" } });
    expect((screen.getByLabelText("Suggested opening height") as HTMLInputElement).value).toBe("1.20");
  });

  it("refuses a bar that is not whole centimetres, in the form", async () => {
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);
    await screen.findByLabelText("Name");
    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Height" } });
    type("Name", "High Jump");
    type("Code", "AT-HIGH-JUMP");
    fireEvent.change(screen.getByLabelText("Usual rise"), { target: { value: "0.055" } });
    expect(
      (screen.getByRole("button", { name: /^Create/ }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });

  it("drops a typed attempt count when the type goes back to a race", async () => {
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);
    await screen.findByLabelText("Name");

    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Distance" } });
    fireEvent.change(screen.getByLabelText("Attempts"), { target: { value: "5" } });
    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Time" } });
    expect(screen.queryByLabelText("Attempts")).toBeNull();

    // Back to the form's own default, never the 5 it briefly held.
    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Distance" } });
    expect((screen.getByLabelText("Attempts") as HTMLInputElement).value).toBe("3");
  });

  it("says why, when the carnival read failed and the lock state is unknown", async () => {
    vi.mocked(getCarnival).mockRejectedValue(new TypeError("Failed to fetch"));
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("this carnival couldn’t be read");
    expect(alert.textContent).toContain("the server decides");
  });

  // ADR-0054 §3: a blocking notice above the fields, both ways out of it named,
  // and a submit that cannot fire.
  it("blocks the whole form with neither a profile nor a roster to derive one", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({ ...settings, divisions: null });
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    expect(await screen.findByText("Import a roster or add ages first.")).toBeTruthy();
    // Both escapes are 44px targets in a button row, not inline text links.
    const divisions = screen.getByRole("link", { name: "Divisions setup" });
    expect(divisions.getAttribute("href")).toBe("#/carnivals/c-1/setup/divisions");
    expect(divisions.className).toBe("button primary-link");
    const template = screen.getByRole("link", { name: "Apply a template on Manage" });
    expect(template.getAttribute("href")).toBe("#/carnivals/c-1/manage");
    expect(template.className).toBe("button");
    expect(divisions.closest(".button-row")).toBe(template.closest(".button-row"));
    expect(screen.queryAllByRole("checkbox")).toHaveLength(0);

    const submit = screen.getByRole("button", { name: "Add divisions first" });
    expect((submit as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(submit);
    expect(createEventDefinition).not.toHaveBeenCalled();
  });
});

/**
 * ADR-0057 §2: a carnival with a roster but no saved profile is no longer
 * blocked — the grid is drawn from the roster default, and the first create
 * SAVES that default before it creates anything.
 */
describe("NewEventScreen on the roster default", () => {
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

  beforeEach(() => {
    vi.mocked(getCarnivalSettings).mockResolvedValue(onDefault);
  });

  it("draws the grid from the roster and says where the ages came from", async () => {
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    expect(
      await screen.findByText(/^Ages from the roster: 12–13 · Boys, Girls · Competitive\./),
    ).toBeTruthy();
    expect(screen.getByRole("link", { name: "Change" }).getAttribute("href")).toBe(
      "#/carnivals/c-1/setup/divisions",
    );
    expect(screen.queryByText(/add ages first/)).toBeNull();
    expect(await screen.findAllByRole("checkbox")).toHaveLength(4);
    expect(screen.getByRole("button", { name: "Create 4 divisions" })).toBeTruthy();
  });

  it("saves the default as the profile BEFORE it creates the event", async () => {
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    await screen.findByLabelText("Name");
    type("Name", "Butterfly 50m");
    type("Code", "SW-50-FLY");
    fireEvent.click(screen.getByRole("button", { name: "Create 4 divisions" }));

    await waitFor(() => expect(generateDivisions).toHaveBeenCalledTimes(1));
    expect(vi.mocked(updateCarnivalSettings).mock.calls[0]).toEqual([
      "c-1",
      { divisions: rosterDefault },
    ]);
    const [saved, made] = [
      vi.mocked(updateCarnivalSettings).mock.invocationCallOrder[0]!,
      vi.mocked(createEventDefinition).mock.invocationCallOrder[0]!,
    ];
    expect(saved).toBeLessThan(made);
  });

  // A profile write that fails must not leave a catalogue definition behind:
  // there is no hand-create path back out of that (ADR-0054 §3).
  it("creates nothing when the profile write is refused", async () => {
    vi.mocked(updateCarnivalSettings).mockRejectedValue(
      new ApiError(
        400,
        { error: "AGE_SCOPE_INVALID", detail: "Age '18': not a known code." },
        "/x",
      ),
    );
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    await screen.findByLabelText("Name");
    type("Name", "Butterfly 50m");
    type("Code", "SW-50-FLY");
    fireEvent.click(screen.getByRole("button", { name: "Create 4 divisions" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("nothing was created");
    expect(alert.textContent).toContain("not a known code.");
    expect(createEventDefinition).not.toHaveBeenCalled();
    expect(generateDivisions).not.toHaveBeenCalled();
    expect(location.hash).toBe("#/carnivals/c-1/setup/events/new");
  });

  // Two organisers on the same carnival: the other one saved a profile while
  // this grid was still drawing the default, and this screen's ages are now
  // neither what is stored nor what anyone ticked.
  it("neither writes nor creates when a profile was saved since the screen loaded", async () => {
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    await screen.findByLabelText("Name");
    type("Name", "Butterfly 50m");
    type("Code", "SW-50-FLY");
    vi.mocked(getCarnivalSettings).mockResolvedValue({
      ...settings,
      divisions: {
        ageGroups: ["16", "17"],
        categories: ["M"],
        tiers: ["Competitive"],
      },
    });
    fireEvent.click(screen.getByRole("button", { name: "Create 4 divisions" }));

    expect(
      (await screen.findByText(/The ages were set elsewhere since this screen loaded/))
        .textContent,
    ).toContain("tick again and press Create");
    expect(updateCarnivalSettings).not.toHaveBeenCalled();
    expect(createEventDefinition).not.toHaveBeenCalled();
    expect(generateDivisions).not.toHaveBeenCalled();
    // The invalidate landed, so the grid is now the saved profile's.
    await waitFor(() => expect(screen.queryByText(/Ages from the roster/)).toBeNull());
  });

  it("writes the profile once, not again on a retry after a failed generate", async () => {
    vi.mocked(generateDivisions).mockRejectedValueOnce(
      new ApiError(409, { error: "EVENT_DEFINITION_INACTIVE", detail: "Retired." }, "/x"),
    );
    renderWithQuery(<NewEventScreen carnivalId="c-1" />);

    await screen.findByLabelText("Name");
    type("Name", "Butterfly 50m");
    type("Code", "SW-50-FLY");
    fireEvent.click(screen.getByRole("button", { name: "Create 4 divisions" }));

    await screen.findByRole("alert");
    fireEvent.click(screen.getByRole("button", { name: "Add 4 divisions" }));
    await waitFor(() => expect(generateDivisions).toHaveBeenCalledTimes(2));
    expect(updateCarnivalSettings).toHaveBeenCalledTimes(1);
  });
});

/** ADR-0055 §2: the same fields with no carnival to schedule into. */
describe("NewEventScreen in the catalogue", () => {
  beforeEach(() => {
    location.hash = "#/school/events/new";
  });

  it("drops the grid and the ADR-0054 guard, and never reads a carnival", async () => {
    renderWithQuery(<NewEventScreen carnivalId={null} />);

    await screen.findByRole("heading", { name: "New definition", level: 1 });
    expect(screen.queryByRole("checkbox")).toBeNull();
    expect(screen.queryByText(/add ages first/)).toBeNull();
    expect(vi.mocked(getCarnival)).not.toHaveBeenCalled();
    expect(vi.mocked(getCarnivalSettings)).not.toHaveBeenCalled();
    expect(screen.getByRole("link", { name: "← Event catalogue" }).getAttribute("href")).toBe(
      "#/school/events",
    );
  });

  it("offers the sports the catalogue already holds, and a free-typed one", async () => {
    renderWithQuery(<NewEventScreen carnivalId={null} />);

    await screen.findByRole("option", { name: "Swimming" });
    const sport = screen.getByLabelText("Sport") as HTMLSelectElement;
    expect([...sport.options].map((o) => o.textContent)).toEqual([
      "Choose a sport",
      "Swimming",
      "Another sport…",
    ]);
    expect(screen.queryByLabelText("Name the sport")).toBeNull();

    fireEvent.change(sport, { target: { value: otherSportValue(sport) } });
    expect(screen.getByLabelText("Name the sport")).toBeTruthy();
  });

  // A sport is a grouping key, so "swimming" and "Swimming" must not become two
  // sections of the catalogue; the sentinel itself is a value no sport can hold.
  it("resolves a typed sport onto the spelling the catalogue already holds", async () => {
    renderWithQuery(<NewEventScreen carnivalId={null} />);
    await screen.findByRole("option", { name: "Swimming" });

    const sport = screen.getByLabelText("Sport") as HTMLSelectElement;
    const other = otherSportValue(sport);
    expect(other.charCodeAt(0)).toBe(0);
    fireEvent.change(sport, { target: { value: other } });
    fireEvent.change(screen.getByLabelText("Name the sport"), {
      target: { value: "  swimming " },
    });
    type("Name", "Butterfly 50m");
    type("Code", "SW-50-FLY");
    fireEvent.click(screen.getByRole("button", { name: "Create definition" }));

    await waitFor(() => expect(vi.mocked(createEventDefinition)).toHaveBeenCalled());
    expect(vi.mocked(createEventDefinition).mock.calls[0]![0]).toMatchObject({
      sport: "Swimming",
    });
  });

  it("trims and keeps a sport the catalogue does not hold yet", async () => {
    renderWithQuery(<NewEventScreen carnivalId={null} />);
    await screen.findByRole("option", { name: "Swimming" });

    const sport = screen.getByLabelText("Sport") as HTMLSelectElement;
    fireEvent.change(sport, { target: { value: otherSportValue(sport) } });
    fireEvent.change(screen.getByLabelText("Name the sport"), {
      target: { value: " Cross country " },
    });
    type("Name", "3km");
    type("Code", "XC-3K");
    fireEvent.click(screen.getByRole("button", { name: "Create definition" }));

    await waitFor(() => expect(vi.mocked(createEventDefinition)).toHaveBeenCalled());
    expect(vi.mocked(createEventDefinition).mock.calls[0]![0]).toMatchObject({
      sport: "Cross country",
    });
  });

  it("creates the definition, says where it landed, and opens its editor", async () => {
    renderWithQuery(<NewEventScreen carnivalId={null} />);
    await screen.findByRole("option", { name: "Swimming" });

    const create = screen.getByRole("button", { name: "Create definition" }) as HTMLButtonElement;
    expect(create.disabled).toBe(true);
    type("Name", "Butterfly 50m");
    type("Code", "SW-50-FLY");
    fireEvent.change(screen.getByLabelText("Sport"), { target: { value: "Swimming" } });
    expect(create.disabled).toBe(false);
    fireEvent.click(create);

    await waitFor(() => expect(vi.mocked(createEventDefinition)).toHaveBeenCalled());
    expect(vi.mocked(createEventDefinition).mock.calls[0]![0]).toMatchObject({
      code: "SW-50-FLY",
      name: "Butterfly 50m",
      sport: "Swimming",
      measureType: "Time",
      units: "seconds",
    });
    expect(vi.mocked(generateDivisions)).not.toHaveBeenCalled();
    await waitFor(() => expect(location.hash).toBe("#/school/events/def-fly"));
    expect(getToasts().map((t) => t.message)).toContain("Butterfly 50m added to the catalogue.");
  });

  it("keeps ADR-0053's attempt default on a Distance event", async () => {
    renderWithQuery(<NewEventScreen carnivalId={null} />);
    await screen.findByLabelText("Sport");

    expect(screen.queryByLabelText("Attempts")).toBeNull();
    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Distance" } });
    expect((screen.getByLabelText("Attempts") as HTMLInputElement).value).toBe("3");
    expect((screen.getByLabelText("Units") as HTMLInputElement).value).toBe("metres");
  });

  it("names the missing role instead of offering the form", async () => {
    signIn({ subject: "rec-1", displayName: "Rae Recorder", roles: ["Recorder"] });
    renderWithQuery(<NewEventScreen carnivalId={null} />);

    expect(
      await screen.findByText(/Adding to the event catalogue needs the carnival administrator/),
    ).toBeTruthy();
    expect(vi.mocked(listEventDefinitions)).not.toHaveBeenCalled();
  });

  // A lapsed session is the ordinary way to arrive here, and a named role is
  // unanswerable advice to somebody who is simply signed out.
  it("offers a sign-in to a signed-out reader rather than naming a role", async () => {
    signOut();
    renderWithQuery(<NewEventScreen carnivalId={null} />);

    expect(await screen.findByRole("link", { name: "Sign in" })).toBeTruthy();
    expect(screen.queryByText(/needs the carnival administrator role/)).toBeNull();
    expect(vi.mocked(listEventDefinitions)).not.toHaveBeenCalled();
  });
});
