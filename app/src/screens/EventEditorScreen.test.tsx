// @vitest-environment jsdom
import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { _resetReachabilityForTests, reportUnreachable } from "../api/reachability";
import { keys } from "../api/queryKeys";
import type {
  Carnival,
  CarnivalEvent,
  CarnivalSettings,
  EventDefinition,
} from "../api/types";
import { signIn, signOut } from "../auth/session";
import { createTestQueryClient, renderWithQuery } from "../test/queryWrapper";

/**
 * The event editor (ADR-0022 §6/§7). Three things a user can observe and must
 * be able to rely on: the rename is stated as global BEFORE they type, a save
 * never wipes configuration the screen cannot show, and retiring takes two
 * deliberate presses and never says "delete".
 */

vi.mock("../api/resources", () => ({
  deactivateEventDefinition: vi.fn(),
  generateDivisions: vi.fn(),
  getCarnival: vi.fn(),
  getCarnivalSettings: vi.fn(),
  listCarnivalEvents: vi.fn(),
  listEventDefinitions: vi.fn(),
  unscheduleDivision: vi.fn(),
  updateEventDefinition: vi.fn(),
}));

import {
  deactivateEventDefinition,
  generateDivisions,
  getCarnival,
  getCarnivalSettings,
  listCarnivalEvents,
  listEventDefinitions,
  updateEventDefinition,
} from "../api/resources";
import EventEditorScreen from "./EventEditorScreen";

const definition: EventDefinition = {
  id: "def-free",
  attemptCount: null,
  ladder: null,
  attemptsPerHeight: null,
  code: "SW-50-FREE",
  name: "Freestyle 50m",
  sport: "Swimming",
  measureType: "Time",
  units: "seconds",
  isActive: true,
  eligibilityJson: null,
  recordCategoriesJson: '{"12":["M"]}',
  format: "Individual",
  teamSize: null,
  legsJson: null,
  tagsJson: '["discipline:Freestyle","distance:50m"]',
  scoringWeight: 1,
  usage: { carnivals: 3, divisions: 84, schoolRecords: 2, results: 0 },
};

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
    ageGroups: ["13"],
    categories: ["M", "F"],
    tiers: ["Competitive"],
  },
};

const thirteenM = {
  id: "ev-13M",
  carnivalId: "c-1",
  eventDefinitionId: "def-free",
  ageGroup: "13",
  category: "M",
  tier: "Competitive",
  status: "Scheduled",
  entryCount: 7,
} as CarnivalEvent;

function setOnline(online: boolean): void {
  Object.defineProperty(navigator, "onLine", { value: online, configurable: true });
}

beforeEach(() => {
  cleanup();
  _resetReachabilityForTests();
  setOnline(true);
  signIn({ subject: "admin-1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
  vi.mocked(listEventDefinitions).mockReset().mockResolvedValue([definition]);
  vi.mocked(updateEventDefinition).mockReset().mockResolvedValue(definition);
  vi.mocked(deactivateEventDefinition).mockReset().mockResolvedValue(undefined);
  vi.mocked(getCarnival).mockReset().mockResolvedValue(carnival);
  vi.mocked(getCarnivalSettings).mockReset().mockResolvedValue(settings);
  vi.mocked(listCarnivalEvents).mockReset().mockResolvedValue([thirteenM]);
  vi.mocked(generateDivisions).mockReset().mockResolvedValue({
    eventDefinitionId: "def-free",
    divisions: [],
    created: 1,
    skipped: 0,
  });
});

afterEach(() => {
  signOut();
});

describe("EventEditorScreen", () => {
  it("states the rename cascade beside the name, before anyone types", async () => {
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const notice = await screen.findByText(/Renaming this event renames every division/);
    expect(notice.textContent).toMatch(/every carnival/);
    expect(notice.textContent).toMatch(/record book/);
  });

  it("saves the editable fields and leaves the blobs it cannot show alone", async () => {
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const name = await screen.findByDisplayValue("Freestyle 50m");
    fireEvent.change(name, { target: { value: "Freestyle 50 metres" } });
    fireEvent.change(screen.getByDisplayValue("seconds"), { target: { value: "s" } });
    fireEvent.click(screen.getByRole("button", { name: "Save event" }));

    await waitFor(() => expect(vi.mocked(updateEventDefinition)).toHaveBeenCalled());
    const [id, request] = vi.mocked(updateEventDefinition).mock.calls[0]!;
    expect(id).toBe("def-free");
    // Sport rides along because the PUT requires it; an ABSENT blob is the only
    // way to say "leave it" — sending null would clear template-authored data.
    expect(request).toEqual({
      code: "SW-50-FREE",
      name: "Freestyle 50 metres",
      sport: "Swimming",
      measureType: "Time",
      units: "s",
      // ADR-0053 §1: a Time event has no attempt series, and the editor says so.
      attemptCount: null,
      // ADR-0056 §5: and no heights ladder, said the same way — absent clears it.
      ladder: null,
    });
    expect(Object.keys(request)).not.toContain("recordCategoriesJson");
    // Tags were not touched, so they are not sent: absent means unchanged.
    expect(Object.keys(request)).not.toContain("tagsJson");
  });

  /** ADR-0053 §1: the count is a field of the definition, not of a carnival. */
  it("edits the attempt count on a field event and sends it", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      { ...definition, measureType: "Distance", units: "m", attemptCount: 3 },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const attempts = (await screen.findByLabelText("Attempts")) as HTMLInputElement;
    expect(attempts.value).toBe("3");
    fireEvent.change(attempts, { target: { value: "2" } });
    fireEvent.click(screen.getByRole("button", { name: "Save event" }));

    await waitFor(() => expect(updateEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(updateEventDefinition).mock.calls[0]![1]).toMatchObject({ attemptCount: 2 });
  });

  /** ADR-0056 §5: the suggestions replace Attempts on a Height event, always sent. */
  it("edits the heights ladder on a Height event and sends it", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      {
        ...definition,
        measureType: "Height",
        units: "m",
        attemptCount: null,
        ladder: { start: 1.2, step: 0.05 },
      },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const start = (await screen.findByLabelText("Suggested opening height")) as HTMLInputElement;
    expect(start.value).toBe("1.20");
    expect(screen.queryByLabelText("Attempts")).toBeNull();
    fireEvent.change(screen.getByLabelText("Usual rise"), { target: { value: "0.03" } });
    fireEvent.click(screen.getByRole("button", { name: "Save event" }));

    await waitFor(() => expect(updateEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(updateEventDefinition).mock.calls[0]![1]).toMatchObject({
      ladder: { start: 1.2, step: 0.03 },
      attemptCount: null,
    });
  });

  it("reads and sends a Height event's attempts per height, and none on a race", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      { ...definition, measureType: "Height", units: "m", attemptsPerHeight: 3 },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const perHeight = (await screen.findByLabelText("Attempts per height")) as HTMLSelectElement;
    expect(perHeight.value).toBe("3");
    fireEvent.change(perHeight, { target: { value: "2" } });
    fireEvent.click(screen.getByRole("button", { name: "Save event" }));
    await waitFor(() => expect(updateEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(updateEventDefinition).mock.calls[0]![1]).toMatchObject({
      attemptsPerHeight: 2,
    });

    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Time" } });
    expect(screen.queryByLabelText("Attempts per height")).toBeNull();
  });

  it("clears the ladder by emptying both fields — an absent one is how it goes", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      {
        ...definition,
        measureType: "Height",
        units: "m",
        attemptCount: null,
        ladder: { start: 1.2, step: 0.05 },
      },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    fireEvent.change(await screen.findByLabelText("Suggested opening height"), { target: { value: "" } });
    fireEvent.change(screen.getByLabelText("Usual rise"), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "Save event" }));

    await waitFor(() => expect(updateEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(updateEventDefinition).mock.calls[0]![1]).toMatchObject({ ladder: null });
  });

  /** Suggestions or none, a stored count is re-sent for the same reason. */
  it("re-sends a stored attempt count when the definition also has suggestions", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      {
        ...definition,
        measureType: "Height",
        units: "m",
        attemptCount: 3,
        ladder: { start: 1.2, step: 0.05 },
      },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    fireEvent.click(await screen.findByRole("button", { name: "Save event" }));
    await waitFor(() => expect(updateEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(updateEventDefinition).mock.calls[0]![1]).toMatchObject({
      attemptCount: 3,
      ladder: { start: 1.2, step: 0.05 },
    });
  });

  it("empties the Attempts field when the type is switched to a bar ladder", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      { ...definition, measureType: "Distance", units: "m", attemptCount: 3 },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    expect(((await screen.findByLabelText("Attempts")) as HTMLInputElement).value).toBe("3");
    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Height" } });
    expect(screen.queryByLabelText("Attempts")).toBeNull();
    // And back to the stored measure is back to the stored count, not a blank
    // field or the new-event default.
    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Distance" } });
    expect(((await screen.findByLabelText("Attempts")) as HTMLInputElement).value).toBe("3");
  });

  // Flicking the measure must not quietly rewrite a bar the event already has:
  // with results behind it the save is refused, without them it is a silent edit.
  it("restores the stored ladder when the measure comes back to Height", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      {
        ...definition,
        measureType: "Height",
        units: "m",
        attemptCount: null,
        ladder: { start: 1, step: 0.05 },
      },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    expect(((await screen.findByLabelText("Suggested opening height")) as HTMLInputElement).value).toBe(
      "1.00",
    );
    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Time" } });
    fireEvent.change(screen.getByLabelText("Event type"), { target: { value: "Height" } });
    expect((screen.getByLabelText("Suggested opening height") as HTMLInputElement).value).toBe("1.00");
    expect((screen.getByLabelText("Usual rise") as HTMLInputElement).value).toBe("0.05");

    fireEvent.click(screen.getByRole("button", { name: "Save event" }));
    await waitFor(() => expect(updateEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(updateEventDefinition).mock.calls[0]![1]).toMatchObject({
      ladder: { start: 1, step: 0.05 },
    });
  });

  /** ADR-0056 §5: the same shipped bar the new-event form seeds. */
  it("seeds the default suggestions when the type is switched to a bar ladder", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      { ...definition, measureType: "Distance", units: "m", attemptCount: 3 },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    fireEvent.change(await screen.findByLabelText("Event type"), { target: { value: "Height" } });
    expect((screen.getByLabelText("Suggested opening height") as HTMLInputElement).value).toBe("1.20");
    expect((screen.getByLabelText("Usual rise") as HTMLInputElement).value).toBe("0.05");

    fireEvent.click(screen.getByRole("button", { name: "Save event" }));
    await waitFor(() => expect(updateEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(updateEventDefinition).mock.calls[0]![1]).toMatchObject({
      measureType: "Height",
      ladder: { start: 1.2, step: 0.05 },
      attemptCount: null,
    });
  });

  /**
   * The amendment took the Attempts field off this type, but a count stored
   * before it is frozen once results exist: a rename must re-send it, not clear
   * it, or the save draws ATTEMPT_COUNT_FROZEN with no field to put back.
   */
  it("re-sends a legacy Height attempt count through an unrelated save", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      { ...definition, measureType: "Height", units: "m", attemptCount: 3, ladder: null },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    expect(await screen.findByLabelText("Suggested opening height")).toBeTruthy();
    expect(screen.queryByLabelText("Attempts")).toBeNull();
    expect(screen.getByText(/Stored attempt count: 3/)).toBeTruthy();
    fireEvent.change(await screen.findByDisplayValue("Freestyle 50m"), {
      target: { value: "High Jump" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save event" }));

    await waitFor(() => expect(updateEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(updateEventDefinition).mock.calls[0]![1]).toMatchObject({
      name: "High Jump",
      attemptCount: 3,
      ladder: null,
    });
  });

  /**
   * The server answers a changed type with MEASURE_FROZEN once results exist
   * (a stored series, card or time cannot be re-read as another type), so the
   * select says so rather than letting the save draw the refusal.
   */
  it("freezes the event type once results are recorded under it", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      { ...definition, usage: { ...definition.usage, results: 12 } },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const select = (await screen.findByLabelText("Event type")) as HTMLSelectElement;
    expect(select.disabled).toBe(true);
    expect(screen.getByText("Frozen: results are recorded under this type.")).toBeTruthy();
  });

  it("leaves the event type open while no result reads it", async () => {
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const select = (await screen.findByLabelText("Event type")) as HTMLSelectElement;
    expect(select.disabled).toBe(false);
    expect(screen.queryByText("Frozen: results are recorded under this type.")).toBeNull();
  });

  /** The server refuses changed units with MEASURE_FROZEN once results exist. */
  it("freezes Units once results are recorded in them, and not before", async () => {
    const note = "Frozen: results are recorded in these units.";
    vi.mocked(listEventDefinitions).mockResolvedValue([
      { ...definition, usage: { ...definition.usage, results: 12 } },
    ]);
    const frozen = renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);
    expect(((await screen.findByLabelText("Units")) as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText(note)).toBeTruthy();
    frozen.unmount();

    vi.mocked(listEventDefinitions).mockResolvedValue([definition]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);
    expect(((await screen.findByLabelText("Units")) as HTMLInputElement).disabled).toBe(false);
    expect(screen.queryByText(note)).toBeNull();
  });

  it("freezes Attempts on a Distance definition with results, and not without", async () => {
    const distance = {
      ...definition,
      measureType: "Distance" as const,
      units: "m",
      attemptCount: 3,
    };
    vi.mocked(listEventDefinitions).mockResolvedValue([
      { ...distance, usage: { ...distance.usage, results: 12 } },
    ]);
    const note = "Frozen: results are recorded under this type.";
    const frozen = renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);
    expect(((await screen.findByLabelText("Attempts")) as HTMLInputElement).disabled).toBe(true);
    // One under the type select, one under Attempts.
    expect(screen.getAllByText(note)).toHaveLength(2);
    frozen.unmount();

    vi.mocked(listEventDefinitions).mockResolvedValue([distance]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);
    expect(((await screen.findByLabelText("Attempts")) as HTMLInputElement).disabled).toBe(false);
    expect(screen.queryAllByText(note)).toHaveLength(0);
  });

  it("keeps a Height definition's suggestions and per-height count open", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      {
        ...definition,
        measureType: "Height",
        units: "m",
        attemptsPerHeight: 3,
        usage: { ...definition.usage, results: 12 },
      },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);
    const perHeight = (await screen.findByLabelText("Attempts per height")) as HTMLSelectElement;
    expect(perHeight.disabled).toBe(false);
    expect((screen.getByLabelText("Suggested opening height") as HTMLInputElement).disabled).toBe(
      false,
    );
  });

  /** And the count is still Distance's to clear: blanking the field means none. */
  it("clears the count when the attempts type blanks the field", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      { ...definition, measureType: "Distance", units: "m", attemptCount: 3 },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    fireEvent.change(await screen.findByLabelText("Attempts"), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "Save event" }));

    await waitFor(() => expect(updateEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(updateEventDefinition).mock.calls[0]![1]).toMatchObject({
      attemptCount: null,
    });
  });

  /** Both suggestions blank is a whole Height event — the type is the mode. */
  it("saves a Height event with no suggestions at all", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      {
        ...definition,
        measureType: "Height",
        units: "m",
        attemptCount: null,
        ladder: { start: 1.2, step: 0.05 },
      },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    fireEvent.change(await screen.findByLabelText("Suggested opening height"), {
      target: { value: "" },
    });
    fireEvent.change(screen.getByLabelText("Usual rise"), { target: { value: "" } });
    const save = screen.getByRole("button", { name: "Save event" }) as HTMLButtonElement;
    expect(save.disabled).toBe(false);
    fireEvent.click(save);

    await waitFor(() => expect(updateEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(updateEventDefinition).mock.calls[0]![1]).toMatchObject({
      measureType: "Height",
      ladder: null,
      attemptCount: null,
    });
  });

  it("refuses a half-filled ladder in the form, before the server has to", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      {
        ...definition,
        measureType: "Height",
        units: "m",
        attemptCount: null,
        ladder: { start: 1.2, step: 0.05 },
      },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    fireEvent.change(await screen.findByLabelText("Usual rise"), { target: { value: "" } });
    expect(
      (screen.getByRole("button", { name: "Save event" }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });

  it("edits the attempt count on a stored Count definition, as the server allows", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([
      { ...definition, measureType: "Count", units: "count", attemptCount: 2 },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const attempts = (await screen.findByLabelText("Attempts")) as HTMLInputElement;
    expect(attempts.value).toBe("2");
    fireEvent.change(attempts, { target: { value: "4" } });
    fireEvent.click(screen.getByRole("button", { name: "Save event" }));

    await waitFor(() => expect(updateEventDefinition).toHaveBeenCalled());
    expect(vi.mocked(updateEventDefinition).mock.calls[0]![1]).toMatchObject({
      measureType: "Count",
      attemptCount: 4,
    });
  });

  it("offers no attempt count on a Time event", async () => {
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);
    await screen.findByDisplayValue("Freestyle 50m");
    expect(screen.queryByLabelText("Attempts")).toBeNull();
  });

  it("adds and removes tags, and sends the whole array once they moved", async () => {
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    await screen.findByText("Distance: 50m");
    fireEvent.click(screen.getByRole("button", { name: "Remove Distance: 50m" }));
    fireEvent.change(screen.getByLabelText("Add a tag"), {
      target: { value: " surface:Pool " },
    });
    fireEvent.click(screen.getByRole("button", { name: "Add tag" }));
    expect(screen.getByText("Surface: Pool")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Save event" }));
    await waitFor(() => expect(vi.mocked(updateEventDefinition)).toHaveBeenCalled());
    const [, request] = vi.mocked(updateEventDefinition).mock.calls[0]!;
    expect(request.tagsJson).toBe('["discipline:Freestyle","surface:Pool"]');
  });

  it("takes a tag on Enter rather than saving the event without it", async () => {
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const field = await screen.findByLabelText("Add a tag");
    fireEvent.change(field, { target: { value: "surface:Pool" } });
    expect(fireEvent.keyDown(field, { key: "Enter" })).toBe(false);
    expect(screen.getByText("Surface: Pool")).toBeTruthy();
    expect(vi.mocked(updateEventDefinition)).not.toHaveBeenCalled();
  });

  it("sends an empty array when the last tag is removed, so it can be cleared", async () => {
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    fireEvent.click(await screen.findByRole("button", { name: "Remove Discipline: Freestyle" }));
    fireEvent.click(screen.getByRole("button", { name: "Remove Distance: 50m" }));
    fireEvent.click(screen.getByRole("button", { name: "Save event" }));

    await waitFor(() => expect(vi.mocked(updateEventDefinition)).toHaveBeenCalled());
    expect(vi.mocked(updateEventDefinition).mock.calls[0]![1].tagsJson).toBe("[]");
  });

  it("ticks a division of this event, and only this event’s divisions are shown", async () => {
    vi.mocked(listCarnivalEvents).mockResolvedValue([
      thirteenM,
      { ...thirteenM, id: "ev-other", eventDefinitionId: "def-back", category: "F" },
    ]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const boy = (await screen.findByRole("checkbox", { name: "13 · Boys" })) as HTMLInputElement;
    expect(boy.checked).toBe(true);
    expect(screen.getByText("7")).toBeTruthy();
    const girl = screen.getByRole("checkbox", { name: "13 · Girls" }) as HTMLInputElement;
    expect(girl.checked).toBe(false);

    fireEvent.click(girl);
    await waitFor(() =>
      expect(vi.mocked(generateDivisions)).toHaveBeenCalledWith("c-1", {
        eventDefinitionId: "def-free",
        divisions: [
          { ageGroup: "13", category: "F", tier: "Competitive" },
        ],
      }),
    );
  });

  it("points an unconfigured carnival at the divisions profile", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue({ ...settings, divisions: null });
    vi.mocked(listCarnivalEvents).mockResolvedValue([]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    await screen.findByText(/no division profile yet/);
    expect(
      screen.getByRole("link", { name: "the divisions profile" }).getAttribute("href"),
    ).toBe("#/carnivals/c-1/setup/divisions");
    expect(screen.queryByRole("checkbox")).toBeNull();
  });

  // The catalogue is global and the divisions are the carnival's: a Closed
  // carnival refuses generate/unschedule behind `RequireEditable`, so the grid
  // must not offer a tick that becomes a refusal under the row.
  it("locks the grid on a closed carnival while the event itself stays editable", async () => {
    vi.mocked(getCarnival).mockResolvedValue({ ...carnival, status: "Closed" });
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const boy = (await screen.findByRole("checkbox", { name: "13 · Boys" })) as HTMLInputElement;
    expect(boy.disabled).toBe(true);
    expect(
      (screen.getByRole("checkbox", { name: "13 · Girls" }) as HTMLInputElement).disabled,
    ).toBe(true);
    expect(screen.getByText(/its divisions can no longer be added to or removed/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save event" }).hasAttribute("disabled")).toBe(
      false,
    );
  });

  it("locks the grid on a finalised carnival too", async () => {
    vi.mocked(getCarnival).mockResolvedValue({ ...carnival, status: "Finalised" });
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const boy = (await screen.findByRole("checkbox", { name: "13 · Boys" })) as HTMLInputElement;
    expect(boy.disabled).toBe(true);
  });

  it("retires an event in two presses, and never calls it a deletion", async () => {
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    fireEvent.click(await screen.findByRole("button", { name: "Retire event…" }));
    expect(vi.mocked(deactivateEventDefinition)).not.toHaveBeenCalled();

    const explanation = screen.getByText(/Retiring deactivates this event/);
    expect(explanation.textContent).toMatch(/nothing is deleted/i);
    fireEvent.click(screen.getByRole("button", { name: "Confirm retire" }));

    await waitFor(() =>
      expect(vi.mocked(deactivateEventDefinition)).toHaveBeenCalledWith("def-free"),
    );
    await waitFor(() =>
      expect(screen.queryByRole("button", { name: "Confirm retire" })).toBeNull(),
    );
  });

  it("refuses to promise a save it cannot make while the server is out of reach", async () => {
    setOnline(false);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    await screen.findByDisplayValue("Freestyle 50m");
    expect(screen.getByText(/read-only for now/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save event" }).hasAttribute("disabled")).toBe(true);
    expect(screen.queryByRole("button", { name: "Retire event…" })).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Retire event…" }).hasAttribute("disabled"),
    ).toBe(true);
  });

  it("leaves a retired event readable, with nothing to press", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([{ ...definition, isActive: false }]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    await screen.findByText(/This event is retired/);
    expect(screen.getByRole("button", { name: "Save event" }).hasAttribute("disabled")).toBe(true);
    expect(screen.queryByRole("button", { name: "Retire event…" })).toBeNull();
  });

  it("says so when the catalogue holds no such event", async () => {
    vi.mocked(listEventDefinitions).mockResolvedValue([]);
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-gone" />);

    await screen.findByRole("heading", { name: "Event not found" });
  });

  it("counts what a rename would reach, before the name field", async () => {
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const usage = await screen.findByText("Used by 3 carnivals · 84 divisions · 2 school records");
    const nameField = screen.getByDisplayValue("Freestyle 50m");
    const after = usage.compareDocumentPosition(nameField) & Node.DOCUMENT_POSITION_FOLLOWING;
    expect(after).toBeTruthy();
    // The count and the sentence are two halves of one warning, not alternatives.
    expect(screen.getByText(/Renaming this event renames every division/)).toBeTruthy();
  });

  it("does not read a failed catalogue read as a deleted event", async () => {
    vi.mocked(listEventDefinitions).mockRejectedValue(new TypeError("Failed to fetch"));
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    await screen.findByRole("heading", { name: /Couldn.t load this event/ });
    expect(screen.queryByRole("heading", { name: "Event not found" })).toBeNull();
    expect(screen.getByText(/the event catalogue couldn.t be read/)).toBeTruthy();
  });

  // The pool case ADR-0022 §7 is written for: the AP is up, the uplink is dead,
  // and `navigator.onLine` still says everything is fine.
  it("is read-only when the server is unreachable, whatever the platform claims", async () => {
    setOnline(true);
    reportUnreachable();
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    await screen.findByDisplayValue("Freestyle 50m");
    expect(screen.getByText(/read-only for now/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Save event" }).hasAttribute("disabled")).toBe(true);
  });

  it("keeps the typed values on screen when the save never reaches the server", async () => {
    vi.mocked(updateEventDefinition).mockRejectedValue(new TypeError("Failed to fetch"));
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    const name = await screen.findByDisplayValue("Freestyle 50m");
    fireEvent.change(name, { target: { value: "Freestyle 50 metres" } });
    fireEvent.click(screen.getByRole("button", { name: "Save event" }));

    await screen.findByText(/nothing was saved/);
    expect(screen.getByDisplayValue("Freestyle 50 metres")).toBeTruthy();
  });

  it("leaves the retire confirmation open when the retire fails", async () => {
    vi.mocked(deactivateEventDefinition).mockRejectedValue(new Error("The catalogue is locked."));
    renderWithQuery(<EventEditorScreen carnivalId="c-1" definitionId="def-free" />);

    fireEvent.click(await screen.findByRole("button", { name: "Retire event…" }));
    fireEvent.click(screen.getByRole("button", { name: "Confirm retire" }));

    await screen.findByText("The catalogue is locked.");
    expect(screen.getByRole("button", { name: "Confirm retire" })).toBeTruthy();
    expect(screen.getByText(/Retiring deactivates this event/)).toBeTruthy();
  });

  it("takes up someone else's rename while nothing has been typed", async () => {
    const { client } = renderWithQuery(
      <EventEditorScreen carnivalId="c-1" definitionId="def-free" />,
    );
    await screen.findByDisplayValue("Freestyle 50m");

    act(() => {
      client.setQueryData(keys.eventDefinitions(true), [{ ...definition, name: "50m Freestyle" }]);
    });

    await waitFor(() => expect(screen.getByDisplayValue("50m Freestyle")).toBeTruthy());
    expect(screen.queryByText(/Someone else changed this event/)).toBeNull();
  });

  it("keeps what is being typed when someone else renames the event, and says so", async () => {
    const { client } = renderWithQuery(
      <EventEditorScreen carnivalId="c-1" definitionId="def-free" />,
    );
    const name = await screen.findByDisplayValue("Freestyle 50m");
    fireEvent.change(name, { target: { value: "Freestyle 50 metres" } });

    act(() => {
      client.setQueryData(keys.eventDefinitions(true), [{ ...definition, name: "50m Freestyle" }]);
    });

    const notice = await screen.findByText(/Someone else changed this event/);
    expect(notice.textContent).toMatch(/50m Freestyle/);
    expect(screen.getByDisplayValue("Freestyle 50 metres")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Discard mine and show theirs" }));
    expect(screen.getByDisplayValue("50m Freestyle")).toBeTruthy();
    expect(screen.queryByText(/Someone else changed this event/)).toBeNull();
  });

  it("keeps what is being typed when someone else retires the event", async () => {
    const { client } = renderWithQuery(
      <EventEditorScreen carnivalId="c-1" definitionId="def-free" />,
    );
    const name = await screen.findByDisplayValue("Freestyle 50m");
    fireEvent.change(name, { target: { value: "Freestyle 50 metres" } });

    act(() => {
      client.setQueryData(keys.eventDefinitions(true), [{ ...definition, isActive: false }]);
    });

    await screen.findByText(/This event is retired/);
    expect(screen.getByDisplayValue("Freestyle 50 metres")).toBeTruthy();
  });
});

/** ADR-0055 §2: the same editor with no carnival behind it. */
describe("EventEditorScreen in the catalogue", () => {
  it("edits the definition with no divisions grid and no carnival reads", async () => {
    renderWithQuery(<EventEditorScreen carnivalId={null} definitionId="def-free" />);

    await screen.findByDisplayValue("Freestyle 50m");
    expect(screen.queryByRole("heading", { name: "Divisions" })).toBeNull();
    expect(vi.mocked(getCarnival)).not.toHaveBeenCalled();
    expect(vi.mocked(listCarnivalEvents)).not.toHaveBeenCalled();

    fireEvent.change(screen.getByDisplayValue("seconds"), { target: { value: "s" } });
    fireEvent.click(screen.getByRole("button", { name: "Save event" }));
    await waitFor(() => expect(vi.mocked(updateEventDefinition)).toHaveBeenCalled());
  });

  // ADR-0022 §6: the blast radius is the same edit wherever it is made from,
  // so the count belongs before the name field on the catalogue's editor too.
  it("counts what a rename would reach, before the name field", async () => {
    renderWithQuery(<EventEditorScreen carnivalId={null} definitionId="def-free" />);

    const usage = await screen.findByText("Used by 3 carnivals · 84 divisions · 2 school records");
    const nameField = screen.getByDisplayValue("Freestyle 50m");
    expect(usage.compareDocumentPosition(nameField) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  // The edit reaches every carnival that scheduled the event, and this screen
  // knows none of them by id — only the prefix over all of them does.
  it("invalidates the carnivals prefix, not just the catalogue, on a save", async () => {
    const client = createTestQueryClient();
    const invalidated = vi.spyOn(client, "invalidateQueries");
    renderWithQuery(<EventEditorScreen carnivalId={null} definitionId="def-free" />, client);

    fireEvent.change(await screen.findByDisplayValue("seconds"), { target: { value: "s" } });
    fireEvent.click(screen.getByRole("button", { name: "Save event" }));

    await waitFor(() => expect(vi.mocked(updateEventDefinition)).toHaveBeenCalled());
    await waitFor(() => {
      const keysInvalidated = invalidated.mock.calls.map((call) => call[0]?.queryKey);
      expect(keysInvalidated).toContainEqual(keys.carnivals());
      expect(keysInvalidated).toContainEqual(keys.eventDefinitions(true));
    });
  });

  it("goes back to the catalogue, from the editor and from a miss", async () => {
    renderWithQuery(<EventEditorScreen carnivalId={null} definitionId="def-free" />);
    await screen.findByDisplayValue("Freestyle 50m");
    expect(screen.getByRole("link", { name: "← Event catalogue" }).getAttribute("href")).toBe(
      "#/school/events",
    );

    cleanup();
    vi.mocked(listEventDefinitions).mockResolvedValue([]);
    renderWithQuery(<EventEditorScreen carnivalId={null} definitionId="def-gone" />);
    await screen.findByText("No event in the catalogue has that id.");
    expect(screen.getByRole("link", { name: "← Event catalogue" }).getAttribute("href")).toBe(
      "#/school/events",
    );
  });
});
