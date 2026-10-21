// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { CarnivalEvent } from "../../api/types";
import { EventList } from "./EventList";

/**
 * The filter bar offers what the carnival actually contests. A hardcoded M/F
 * list cannot narrow to an Open division (ADR-0022 §1) or to one that carries
 * no category at all, so the option list is derived like the age one beside it.
 */

vi.mock("../../api/resources", () => ({
  assignOfficials: vi.fn(),
  changeEventStatus: vi.fn(),
}));

const base: CarnivalEvent = {
  id: "ev-1",
  carnivalId: "c-1",
  eventDefinitionId: "def-1",
  definitionCode: "SW-50-FREE",
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
  entryCount: 3,
  complete: false,
  officials: [],
};

function event(id: string, category: string | null): CarnivalEvent {
  return { ...base, id, category } as CarnivalEvent;
}

function renderList(events: CarnivalEvent[]) {
  return render(
    <EventList events={events} canManage={false} busy={false} onAction={vi.fn()} />,
  );
}

function categoryOptions(): string[] {
  const select = screen.getByLabelText("Category") as HTMLSelectElement;
  return [...select.options].map((o) => o.text);
}

beforeEach(cleanup);

describe("EventList category filter", () => {
  it("offers every category the events carry, Open included", () => {
    renderList([event("m", "M"), event("f", "F"), event("s", "S")]);

    expect(categoryOptions()).toEqual(["Any category", "Boys", "Girls", "Open"]);
  });

  it("offers only what is there, so a filter that narrows to nothing is not offered", () => {
    renderList([event("f", "F")]);

    expect(categoryOptions()).toEqual(["Any category", "Girls"]);
  });

  it("narrows to an Open division", () => {
    renderList([event("f", "F"), event("s", "S")]);

    fireEvent.change(screen.getByLabelText("Category"), { target: { value: "S" } });
    expect(screen.getByText("Showing 1 of 2 events")).toBeTruthy();
  });

  // A null category is a value of its own (ADR-0003), and "" is already taken by
  // "any", so it cannot be filtered to without an option of its own.
  it("names the uncategorised case and narrows to it", () => {
    renderList([event("f", "F"), event("x", null)]);

    expect(categoryOptions()).toEqual(["Any category", "Girls", "No category"]);
    const select = screen.getByLabelText("Category") as HTMLSelectElement;
    fireEvent.change(select, { target: { value: select.options[2]!.value } });
    expect(screen.getByText("Showing 1 of 2 events")).toBeTruthy();
  });
});
