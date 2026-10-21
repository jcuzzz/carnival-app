// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { ScoringRulesEditor } from "./ScoringRulesEditor";
import Toaster from "../../shell/Toaster";
import { _resetToastsForTests } from "../../shell/toast";
import type { CarnivalSettings } from "../../api/types";

/**
 * U4 residue: a save that is in flight must disable its own button — on a
 * school iPad over a slow link, a second tap is the default behaviour, and this
 * one rewrites the carnival's scoring rules.
 */

const updateCarnivalSettings = vi.fn();

vi.mock("../../api/resources", () => ({
  updateCarnivalSettings: (...args: unknown[]) => updateCarnivalSettings(...args),
}));

const settings: CarnivalSettings = {
  divisionsDefault: null,
  rosterAgeGroups: [],
  rosterYear: null,
  scoring: {
    version: "ADR-0007",
    placePoints: [4, 3, 2],
    participationFloor: 1,
    dnfPoints: 0,
    dnsPoints: 0,
    dqPoints: 0,
    socialPerEntryPoints: 1,
  },
  adjustmentCategories: ["Novelty", "Spirit"],
  // ADR-0015 §6: the venue's lane count rides the same settings document. Null
  // is "not configured", which is where every carnival starts.
  laneCount: null,
  autoFillLanes: false,
  // Likewise the division profile (ADR-0022 §2) — absent until a template seeds it.
  divisions: null,
};

beforeEach(() => {
  cleanup();
  _resetToastsForTests();
  updateCarnivalSettings.mockReset();
});

describe("ScoringRulesEditor", () => {
  it("disables the save button while the write is in flight, then confirms by toast", async () => {
    let resolveSave: (() => void) | undefined;
    updateCarnivalSettings.mockReturnValue(
      new Promise<void>((resolve) => {
        resolveSave = resolve;
      }),
    );
    const onSaved = vi.fn();
    render(
      <>
        <ScoringRulesEditor carnivalId="c-1" settings={settings} onSaved={onSaved} />
        <Toaster />
      </>,
    );

    fireEvent.click(screen.getByText("Scoring rules…"));
    const save = screen.getByText("Save scoring rules");
    fireEvent.click(save);

    const busy = await screen.findByText("Saving…");
    expect((busy as HTMLButtonElement).disabled).toBe(true);
    // A second tap while the first is in flight must not reach the server.
    fireEvent.click(busy);
    expect(updateCarnivalSettings).toHaveBeenCalledTimes(1);

    resolveSave!();
    await waitFor(() => expect(onSaved).toHaveBeenCalledTimes(1));
    expect(screen.getByText("Scoring rules saved — standings recomputed.")).toBeTruthy();
    expect((screen.getByText("Save scoring rules") as HTMLButtonElement).disabled).toBe(false);
  });

  /**
   * ADR-0014 §4: a Social-scored event pays a flat per-entry point to the
   * entrant's house instead of ranking places, and that rate is per-carnival
   * configuration like every other scoring rule — never a code constant.
   */
  it("saves the Social per-entry rate alongside the place-points table", async () => {
    updateCarnivalSettings.mockResolvedValue(undefined);
    render(<ScoringRulesEditor carnivalId="c-1" settings={settings} onSaved={vi.fn()} />);
    fireEvent.click(screen.getByText("Scoring rules…"));

    const social = screen.getByLabelText("Social points per entry") as HTMLInputElement;
    expect(social.value).toBe("1");
    fireEvent.change(social, { target: { value: "2" } });
    fireEvent.click(screen.getByText("Save scoring rules"));

    await waitFor(() => expect(updateCarnivalSettings).toHaveBeenCalledTimes(1));
    expect(updateCarnivalSettings.mock.calls[0]![1].scoring.socialPerEntryPoints).toBe(2);
  });

  it("re-seeds from the stored rules without closing the panel", () => {
    const { rerender } = render(
      <ScoringRulesEditor carnivalId="c-1" settings={settings} onSaved={vi.fn()} />,
    );
    fireEvent.click(screen.getByText("Scoring rules…"));
    const social = () => screen.getByLabelText("Social points per entry") as HTMLInputElement;
    expect(social().value).toBe("1");

    // A refetch that changed nothing must leave a half-typed override alone.
    fireEvent.change(social(), { target: { value: "9" } });
    rerender(<ScoringRulesEditor carnivalId="c-1" settings={{ ...settings }} onSaved={vi.fn()} />);
    expect(social().value).toBe("9");

    // One that DID change them re-seeds — and the panel is still open, which the
    // remount this replaced could not manage: it vanished from under whoever had
    // just saved through it.
    rerender(
      <ScoringRulesEditor
        carnivalId="c-1"
        settings={{ ...settings, scoring: { ...settings.scoring, socialPerEntryPoints: 5 } }}
        onSaved={vi.fn()}
      />,
    );
    expect(social().value).toBe("5");
  });

  it("rejects a negative Social per-entry rate before any write", () => {
    render(<ScoringRulesEditor carnivalId="c-1" settings={settings} onSaved={vi.fn()} />);
    fireEvent.click(screen.getByText("Scoring rules…"));
    fireEvent.change(screen.getByLabelText("Social points per entry"), {
      target: { value: "-1" },
    });
    fireEvent.click(screen.getByText("Save scoring rules"));

    expect(updateCarnivalSettings).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("non-negative whole number");
  });

  it("rejects a malformed place-points list before any write", () => {
    render(<ScoringRulesEditor carnivalId="c-1" settings={settings} onSaved={vi.fn()} />);
    fireEvent.click(screen.getByText("Scoring rules…"));
    fireEvent.change(screen.getByDisplayValue("4, 3, 2"), { target: { value: "4, -1" } });
    fireEvent.click(screen.getByText("Save scoring rules"));

    expect(updateCarnivalSettings).not.toHaveBeenCalled();
    expect(screen.getByRole("alert").textContent).toContain("non-negative whole numbers");
  });
});
