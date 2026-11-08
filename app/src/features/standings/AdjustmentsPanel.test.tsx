// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { CarnivalSettings, HousePointsAdjustment } from "../../api/types";
import type { ApiState } from "../../api/useApiQuery";
import { _resetHouseMarksForTests, applyHouseMarks } from "../../branding/houseMarks";
import { renderWithQuery } from "../../test/queryWrapper";

/** Novelty points read in the school's house names; the code is still what is sent. */

vi.mock("../../api/resources", () => ({
  listHouses: vi.fn(),
  voidAdjustment: vi.fn(),
  getHouseMarks: vi.fn(),
}));
vi.mock("../../adjustments/submitAdjustment", () => ({
  submitAdjustmentViaOutbox: vi.fn(),
}));

import { listHouses } from "../../api/resources";
import { submitAdjustmentViaOutbox } from "../../adjustments/submitAdjustment";
import { AdjustmentsPanel } from "./AdjustmentsPanel";

const settings = { adjustmentCategories: ["Spirit"] } as CarnivalSettings;

function state(data: HousePointsAdjustment[]): ApiState<HousePointsAdjustment[]> {
  return { data, error: undefined, loading: false, isRefetching: false, updatedAt: 0 };
}

function render(adjustments: HousePointsAdjustment[] = []) {
  return renderWithQuery(
    <AdjustmentsPanel
      carnivalId="c-1"
      canManage
      settings={settings}
      adjustments={state(adjustments)}
      pending={[]}
      actorSubject="s1"
    />,
  );
}

beforeEach(() => {
  cleanup();
  vi.mocked(listHouses).mockReset().mockResolvedValue(["BANKSIA", "WATTLE"]);
  vi.mocked(submitAdjustmentViaOutbox).mockReset().mockResolvedValue(undefined as never);
  applyHouseMarks([
    { code: "BANKSIA", name: "Banksia", colour: null },
    { code: "WATTLE", name: "Wattle", colour: null },
  ]);
});

afterEach(() => {
  _resetHouseMarksForTests();
});

describe("AdjustmentsPanel", () => {
  it("lists houses by name, keeping the code as the value sent", async () => {
    render();

    const select = screen.getByLabelText("House") as HTMLSelectElement;
    await waitFor(() => expect(select.options).toHaveLength(3));
    expect([...select.options].slice(1).map((o) => [o.value, o.textContent])).toEqual([
      ["BANKSIA", "Banksia"],
      ["WATTLE", "Wattle"],
    ]);

    fireEvent.change(select, { target: { value: "WATTLE" } });
    fireEvent.change(screen.getByLabelText("Category"), { target: { value: "Spirit" } });
    fireEvent.change(screen.getByLabelText("Points (negative to deduct)"), {
      target: { value: "10" },
    });
    fireEvent.change(screen.getByLabelText("Reason"), { target: { value: "Best dressed" } });
    fireEvent.click(screen.getByRole("button", { name: "Award points" }));

    await waitFor(() =>
      expect(submitAdjustmentViaOutbox).toHaveBeenCalledWith(
        expect.objectContaining({ houseCode: "WATTLE", points: 10 }),
      ),
    );
  });

  it("names the house on a recorded award", () => {
    render([
      {
        id: "a-1",
        houseCode: "BANKSIA",
        points: 5,
        category: "Spirit",
        reason: "Loudest",
        voided: false,
      } as HousePointsAdjustment,
    ]);

    expect(screen.getByText("Banksia +5 · Spirit")).toBeTruthy();
  });

  it("is not the screen's primary action, and cites no ADR", () => {
    render();

    expect(screen.getByRole("button", { name: "Award points" }).className).toBe("");
    expect(document.body.textContent).not.toMatch(/ADR-/);
  });
});
