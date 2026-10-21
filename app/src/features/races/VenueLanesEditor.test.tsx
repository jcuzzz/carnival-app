// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { VenueLanesEditor } from "./VenueLanesEditor";
import Toaster from "../../shell/Toaster";
import { _resetToastsForTests } from "../../shell/toast";
import { ApiError } from "../../api/client";
import type { CarnivalSettings } from "../../api/types";

/**
 * ADR-0038 §1: the venue's two lane facts, edited together. Auto-fill without a
 * lane count is refused twice — here and on the server — in one sentence.
 */

const updateCarnivalSettings = vi.fn();

vi.mock("../../api/resources", () => ({
  updateCarnivalSettings: (...args: unknown[]) => updateCarnivalSettings(...args),
}));

const settings: CarnivalSettings = {
  divisionsDefault: null,
  rosterAgeGroups: [],
  rosterYear: null,
  scoring: {} as CarnivalSettings["scoring"],
  adjustmentCategories: ["Novelty"],
  laneCount: null,
  autoFillLanes: false,
  divisions: null,
};

beforeEach(() => {
  cleanup();
  _resetToastsForTests();
  updateCarnivalSettings.mockReset();
});

describe("VenueLanesEditor", () => {
  it("round-trips the lane count and the checkbox in one settings write", async () => {
    updateCarnivalSettings.mockResolvedValue(undefined);
    const onSaved = vi.fn();
    render(
      <>
        <VenueLanesEditor carnivalId="c-1" settings={settings} onSaved={onSaved} />
        <Toaster />
      </>,
    );

    fireEvent.change(screen.getByLabelText("Lanes per venue"), { target: { value: "8" } });
    fireEvent.click(screen.getByLabelText("Auto-fill lanes"));
    fireEvent.click(screen.getByText("Save lane settings"));

    await waitFor(() => expect(updateCarnivalSettings).toHaveBeenCalledTimes(1));
    // Only this slice: the scoring rules and the division profile are overlaid
    // by the server from what is stored, and restating them here would be a
    // claim this editor has no business making.
    expect(updateCarnivalSettings.mock.calls[0]![1]).toEqual({ laneCount: 8, autoFillLanes: true });
    expect(onSaved).toHaveBeenCalledTimes(1);
    expect(screen.getByText("Lane settings saved.")).toBeTruthy();

    // What the stored settings now say is what the fields show.
    cleanup();
    render(
      <VenueLanesEditor
        carnivalId="c-1"
        settings={{ ...settings, laneCount: 8, autoFillLanes: true }}
        onSaved={vi.fn()}
      />,
    );
    expect((screen.getByLabelText("Lanes per venue") as HTMLInputElement).value).toBe("8");
    expect((screen.getByLabelText("Auto-fill lanes") as HTMLInputElement).checked).toBe(true);
  });

  it("warns rather than writes when the box is ticked with no lane count", () => {
    render(<VenueLanesEditor carnivalId="c-1" settings={settings} onSaved={vi.fn()} />);

    // The checkbox is LIVE with no count — the two fields are set in one visit,
    // and a dead control cannot say why it is dead.
    const box = screen.getByLabelText("Auto-fill lanes") as HTMLInputElement;
    expect(box.disabled).toBe(false);
    fireEvent.click(box);
    fireEvent.click(screen.getByText("Save lane settings"));

    expect(updateCarnivalSettings).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toBe("Set the lane count first.");
  });

  it("accepts the box against a lane count already stored", async () => {
    updateCarnivalSettings.mockResolvedValue(undefined);
    render(
      <VenueLanesEditor
        carnivalId="c-1"
        settings={{ ...settings, laneCount: 6 }}
        onSaved={vi.fn()}
      />,
    );
    fireEvent.click(screen.getByLabelText("Auto-fill lanes"));
    fireEvent.click(screen.getByText("Save lane settings"));

    await waitFor(() => expect(updateCarnivalSettings).toHaveBeenCalledTimes(1));
    expect(updateCarnivalSettings.mock.calls[0]![1]).toEqual({ laneCount: 6, autoFillLanes: true });
  });

  it("shows the server's refusal as the same sentence, never the code", async () => {
    updateCarnivalSettings.mockRejectedValue(
      new ApiError(
        400,
        { error: "AUTO_FILL_LANES_REQUIRES_LANE_COUNT", detail: "autoFillLanes needs laneCount." },
        "/carnivals/c-1/settings",
      ),
    );
    render(
      <VenueLanesEditor
        carnivalId="c-1"
        settings={{ ...settings, laneCount: 6 }}
        onSaved={vi.fn()}
      />,
    );
    fireEvent.click(screen.getByLabelText("Auto-fill lanes"));
    fireEvent.click(screen.getByText("Save lane settings"));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toBe("Set the lane count first.");
    expect(alert.textContent).not.toContain("AUTO_FILL");
  });

  it("rejects a lane count outside the venue bounds before any write", () => {
    render(<VenueLanesEditor carnivalId="c-1" settings={settings} onSaved={vi.fn()} />);
    fireEvent.change(screen.getByLabelText("Lanes per venue"), { target: { value: "0" } });
    fireEvent.click(screen.getByText("Save lane settings"));

    expect(updateCarnivalSettings).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("between 1 and 64");
  });

  it("leaves a half-typed count alone when a poll re-reads unchanged settings", () => {
    const { rerender } = render(
      <VenueLanesEditor carnivalId="c-1" settings={settings} onSaved={vi.fn()} />,
    );
    const field = () => screen.getByLabelText("Lanes per venue") as HTMLInputElement;
    fireEvent.change(field(), { target: { value: "1" } });
    rerender(<VenueLanesEditor carnivalId="c-1" settings={{ ...settings }} onSaved={vi.fn()} />);
    expect(field().value).toBe("1");

    rerender(
      <VenueLanesEditor
        carnivalId="c-1"
        settings={{ ...settings, laneCount: 10 }}
        onSaved={vi.fn()}
      />,
    );
    expect(field().value).toBe("10");
  });
});
