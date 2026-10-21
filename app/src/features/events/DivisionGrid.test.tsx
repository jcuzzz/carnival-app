// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../../api/client";
import type { CarnivalEvent, DivisionProfile } from "../../api/types";

/**
 * The grid an administrator ticks (ADR-0022 §8). What they must be able to rely
 * on: a tick creates exactly the division on that box, an un-tick that the
 * server refuses is refused AGAINST THAT BOX and changes nothing, and a
 * division the profile no longer names is still on screen.
 */

vi.mock("../../api/resources", () => ({
  generateDivisions: vi.fn(),
  unscheduleDivision: vi.fn(),
}));

import { generateDivisions, unscheduleDivision } from "../../api/resources";
import { DivisionGrid } from "./DivisionGrid";
import { allCellKeys, buildDivisionGrid } from "./divisionGridModel";

const profile: DivisionProfile = {
  ageGroups: ["12", "13"],
  categories: ["M", "F"],
  tiers: ["Competitive"],
};

const thirteenF: CarnivalEvent = {
  id: "ev-13F",
  carnivalId: "c-1",
  eventDefinitionId: "def-free",
  ageGroup: "13",
  category: "F",
  tier: "Competitive",
  status: "Scheduled",
  entryCount: 4,
} as CarnivalEvent;

const onChanged = vi.fn();

function live(divisions: CarnivalEvent[] = [], writable = true) {
  return render(
    <DivisionGrid
      profile={profile}
      divisions={divisions}
      writable={writable}
      profilePath="/carnivals/c-1/setup/divisions"
      mode={{
        kind: "live",
        carnivalId: "c-1",
        eventDefinitionId: "def-free",
        onChanged,
      }}
    />,
  );
}

beforeEach(() => {
  cleanup();
  onChanged.mockReset();
  vi.mocked(generateDivisions).mockReset().mockResolvedValue({
    eventDefinitionId: "def-free",
    divisions: [],
    created: 1,
    skipped: 0,
  });
  vi.mocked(unscheduleDivision).mockReset().mockResolvedValue(undefined);
});

describe("DivisionGrid — live", () => {
  it("generates exactly the division the ticked box names", async () => {
    live();

    fireEvent.click(screen.getByRole("checkbox", { name: "13 · Boys" }));

    await waitFor(() => expect(vi.mocked(generateDivisions)).toHaveBeenCalled());
    expect(vi.mocked(generateDivisions).mock.calls[0]).toEqual([
      "c-1",
      {
        eventDefinitionId: "def-free",
        divisions: [
          { ageGroup: "13", category: "M", tier: "Competitive" },
        ],
      },
    ]);
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  it("shows a contested division's entry count beside its tick, and unschedules it", async () => {
    live([thirteenF]);

    const box = screen.getByRole("checkbox", { name: "13 · Girls" }) as HTMLInputElement;
    expect(box.checked).toBe(true);
    expect(screen.getByText("4")).toBeTruthy();

    fireEvent.click(box);
    await waitFor(() =>
      expect(vi.mocked(unscheduleDivision)).toHaveBeenCalledWith("c-1", "ev-13F"),
    );
    expect(vi.mocked(generateDivisions)).not.toHaveBeenCalled();
  });

  it("sends one DELETE when the box is clicked again before the refetch lands", async () => {
    // The parent still renders the removed division: `useApiQuery` keeps stale
    // data through a revalidation, and a second DELETE would 404.
    live([thirteenF]);

    const box = screen.getByRole("checkbox", { name: "13 · Girls" }) as HTMLInputElement;
    fireEvent.click(box);

    // The box goes un-ticked and stays disabled until the refetch drops the
    // division; jsdom dispatches a click at a disabled input anyway, so what is
    // pinned is that no second DELETE leaves for an id that is already gone.
    await waitFor(() => expect(box.checked).toBe(false));
    expect(box.hasAttribute("disabled")).toBe(true);
    fireEvent.click(box);

    expect(vi.mocked(unscheduleDivision)).toHaveBeenCalledTimes(1);
  });

  // ADR-0054 §6: `checked` used to come off the server row alone, so a tick read
  // as nothing happening until the parent's refetch landed.
  it("holds a ticked box ticked from the press, not from the refetch", async () => {
    let settle!: () => void;
    vi.mocked(generateDivisions).mockReturnValue(
      new Promise((resolve) => {
        settle = () => resolve({ eventDefinitionId: "def-free", divisions: [], created: 1, skipped: 0 });
      }),
    );
    live();

    const box = screen.getByRole("checkbox", { name: "13 · Boys" }) as HTMLInputElement;
    fireEvent.click(box);

    expect(box.checked).toBe(true);
    expect(box.hasAttribute("disabled")).toBe(true);
    settle();
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  // The pending key has to be dropped once the write lands: held any longer it
  // would still claim a tick after the division is unscheduled again.
  it("follows the server row again once the division has been created and removed", async () => {
    const { rerender } = live();
    const grid = (divisions: CarnivalEvent[]) => (
      <DivisionGrid
        profile={profile}
        divisions={divisions}
        writable
        profilePath="/carnivals/c-1/setup/divisions"
        mode={{ kind: "live", carnivalId: "c-1", eventDefinitionId: "def-free", onChanged }}
      />
    );

    fireEvent.click(screen.getByRole("checkbox", { name: "13 · Girls" }));
    await waitFor(() => expect(onChanged).toHaveBeenCalled());

    // The refetch lands, then the division is unscheduled elsewhere.
    rerender(grid([thirteenF]));
    expect((screen.getByRole("checkbox", { name: "13 · Girls" }) as HTMLInputElement).checked).toBe(
      true,
    );

    rerender(grid([]));
    const box = screen.getByRole("checkbox", { name: "13 · Girls" }) as HTMLInputElement;
    expect(box.checked).toBe(false);
    expect(box.hasAttribute("disabled")).toBe(false);
  });

  it("reverts a ticked box when the generate is refused, with the reason beside it", async () => {
    vi.mocked(generateDivisions).mockRejectedValue(
      new ApiError(409, { error: "EVENT_DEFINITION_INACTIVE", detail: "That event is retired." }, "/x"),
    );
    live();

    const box = screen.getByRole("checkbox", { name: "13 · Boys" }) as HTMLInputElement;
    fireEvent.click(box);
    expect(box.checked).toBe(true);

    const refusal = await screen.findByRole("alert");
    expect(refusal.textContent).toContain("13 · Boys: That event is retired.");
    expect(box.checked).toBe(false);
    expect(box.hasAttribute("disabled")).toBe(false);
  });

  it("refuses an un-tick against the box that was clicked, and points at closing", async () => {
    vi.mocked(unscheduleDivision).mockRejectedValue(
      new ApiError(
        409,
        { error: "EVENT_HAS_ENTRIES", detail: "…", entryCount: 4, resultCount: 2 },
        "/carnivals/c-1/events/ev-13F",
      ),
    );
    live([thirteenF]);

    const box = screen.getByRole("checkbox", { name: "13 · Girls" }) as HTMLInputElement;
    fireEvent.click(box);

    const refusal = await screen.findByRole("alert");
    expect(refusal.textContent).toContain("13 · Girls has 4 entries and 2 results");
    expect(refusal.textContent).toMatch(/Close the division instead/);
    expect(refusal.textContent).toMatch(/created in error/);
    // Tied to the box that was clicked, not to the row above or the page.
    expect(box.getAttribute("aria-describedby")).toBe(refusal.getAttribute("id"));
    expect(
      screen
        .getByRole("checkbox", { name: "13 · Boys" })
        .getAttribute("aria-describedby"),
    ).toBeNull();
    // The division still exists, so the box is still ticked.
    expect(box.checked).toBe(true);
  });

  it("says nothing was saved when the un-tick never reaches the server", async () => {
    vi.mocked(unscheduleDivision).mockRejectedValue(new TypeError("Failed to fetch"));
    live([thirteenF]);

    fireEvent.click(screen.getByRole("checkbox", { name: "13 · Girls" }));

    const refusal = await screen.findByRole("alert");
    expect(refusal.textContent).toMatch(/nothing was saved/);
  });

  it("shows a division the profile no longer names, marked off profile", () => {
    live([{ ...thirteenF, id: "ev-11M", ageGroup: "11", category: "M" }]);

    expect(
      (screen.getByRole("checkbox", { name: "11 · Boys" }) as HTMLInputElement).checked,
    ).toBe(true);
    expect(screen.getByText(/One division is/).textContent).toMatch(
      /after it was created\. It is still run, and is shown below\./,
    );
    expect(screen.getAllByText(/off profile/).length).toBeGreaterThan(0);
  });

  it("writes the off-profile sentence as a plural only when there is more than one", () => {
    live([
      { ...thirteenF, id: "ev-11M", ageGroup: "11", category: "M" },
      { ...thirteenF, id: "ev-11F", ageGroup: "11", category: "F" },
    ]);

    expect(screen.getByText(/2 divisions are/).textContent).toMatch(
      /after they were created\. They are still run, and are shown below\./,
    );
  });

  it("ticks nothing while the server is out of reach", () => {
    live([thirteenF], false);

    for (const box of screen.getAllByRole("checkbox"))
      expect(box.hasAttribute("disabled")).toBe(true);
  });

  it("points at the divisions profile when the carnival has none", () => {
    render(
      <DivisionGrid
        profile={null}
        divisions={[]}
        writable
        profilePath="/carnivals/c-1/setup/divisions"
        mode={{
          kind: "live",
          carnivalId: "c-1",
          eventDefinitionId: "def-free",
          onChanged,
        }}
      />,
    );

    expect(screen.getByText(/no division profile yet/)).toBeTruthy();
    expect(screen.getByRole("link", { name: "the divisions profile" }).getAttribute("href")).toBe(
      "#/carnivals/c-1/setup/divisions",
    );
    expect(screen.queryByRole("checkbox")).toBeNull();
  });
});

/** The add-an-event screen's mode: nothing exists yet, so nothing is called. */
function Draft() {
  const model = buildDivisionGrid(profile, []);
  const [ticked, setTicked] = useState<ReadonlySet<string>>(() => allCellKeys(model));
  return (
    <>
      <p>{ticked.size} ticked</p>
      <DivisionGrid
        profile={profile}
        divisions={[]}
        writable
        profilePath="/carnivals/c-1/setup/divisions"
        mode={{ kind: "draft", ticked, onTickedChange: setTicked }}
      />
    </>
  );
}

describe("DivisionGrid — draft", () => {
  it("collects ticks for the parent to submit, and calls no endpoint", () => {
    render(<Draft />);

    expect(screen.getByText("4 ticked")).toBeTruthy();
    expect(
      (screen.getByRole("checkbox", { name: "12 · Boys" }) as HTMLInputElement).checked,
    ).toBe(true);

    fireEvent.click(screen.getByRole("checkbox", { name: "12 · Boys" }));

    expect(screen.getByText("3 ticked")).toBeTruthy();
    expect(
      (screen.getByRole("checkbox", { name: "12 · Boys" }) as HTMLInputElement).checked,
    ).toBe(false);
    expect(vi.mocked(generateDivisions)).not.toHaveBeenCalled();
    expect(vi.mocked(unscheduleDivision)).not.toHaveBeenCalled();
  });
});
