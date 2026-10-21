// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import type { CarnivalEvent } from "../../api/types";
import { EventRow } from "./EventRow";

/**
 * ADR-0014 §4: tier and points eligibility are data on the event instance, and
 * a Competitive event that scores house points is the norm — so the row badges
 * ONLY the departures. A badge that appeared on every row would carry no
 * information and would cost deck-day reading time.
 */

vi.mock("../../api/resources", () => ({
  assignOfficials: vi.fn(),
  changeEventStatus: vi.fn(),
}));

const base: CarnivalEvent = {
  id: "ev-1",
  carnivalId: "c-1",
  eventDefinitionId: "def-1",
  definitionCode: "SWIM-50-FS",
  definitionName: "Freestyle 50m",
  ageGroup: "15",
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

function renderRow(event: CarnivalEvent) {
  return render(
    <ul>
      <EventRow event={event} canManage={false} busy={false} onAction={vi.fn()} />
    </ul>,
  );
}

beforeEach(cleanup);

describe("EventRow", () => {
  it("badges a Social, points-excluded event and states its division", () => {
    renderRow({
      ...base,
      tier: "Social",
      scoringMode: "Social",
      effectiveScoringMode: "Social",
      scoresPoints: false,
    });

    expect(screen.getByText("Social")).toBeTruthy();
    expect(screen.getByText("no points")).toBeTruthy();
    expect(screen.getByText(/15 · Girls/)).toBeTruthy();
  });

  it("badges nothing on the norm — a Competitive event that scores points", () => {
    renderRow(base);

    expect(screen.queryByText("Social")).toBeNull();
    expect(screen.queryByText("no points")).toBeNull();
    // The status badge is the only one left.
    expect(screen.getByText("Open")).toBeTruthy();
  });

  it("badges a scoring mode that departs from its tier's default", () => {
    // A Competitive event scored flat-per-entry looks ordinary otherwise, and
    // it hands out points on a completely different basis — the one case where
    // ADR-0014 §4 wants the mode surfaced.
    renderRow({ ...base, scoringMode: "Social", effectiveScoringMode: "Social" });

    expect(screen.getByText("flat points")).toBeTruthy();
    expect(screen.queryByText("Social")).toBeNull(); // the tier is still Competitive
  });

  it("says nothing when an explicit mode merely restates the tier default", () => {
    renderRow({ ...base, scoringMode: "Places", effectiveScoringMode: "Places" });

    expect(screen.queryByText("flat points")).toBeNull();
    expect(screen.queryByText("placings")).toBeNull();
  });

  it("shows the division's own age beside its category", () => {
    renderRow({ ...base, ageGroup: "SNR", category: "M" });
    expect(screen.getByText(/SNR · Boys/)).toBeTruthy();
  });
});
