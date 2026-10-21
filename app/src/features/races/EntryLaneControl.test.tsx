// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ARMING_LOCKOUT_MS } from "../ui/timing";
import { EntryLaneControl, type LaneControlHandle } from "./EntryLaneControl";

/**
 * The row's own lane picker (ADR-0020 §2, spanning every race of the division
 * since ADR-0038 §3). Presentational: the writes are the screen's, so what is
 * pinned here is the part a screen test cannot see cheaply — the two-step
 * remove, its arming lockout, and what the picker will and will not offer.
 *
 * Since ADR-0041 §2 the select is revealed by the row's actions menu, which
 * reaches this control through `handle` — so `select()` opens it first.
 */

const handle: { current: LaneControlHandle | null } = { current: null };

const props = {
  handle,
  entryId: "en-1",
  displayName: "Ann Anchor",
  lane: 1 as number | null,
  inRace: true,
  races: [{ raceId: "r-1", title: "Race 1", lanes: [1, 2, 4], home: true }],
  disabled: false,
  item: undefined,
  onPick: vi.fn(),
  onPickInRace: vi.fn(),
  onRemove: vi.fn(),
  onSwap: vi.fn(),
};

const marley = { lane: 3, entryId: "en-2", displayName: "Marley Singh" };

const select = () => {
  if (screen.queryByLabelText("Lane for Ann Anchor") === null)
    act(() => handle.current!.openPicker());
  return screen.getByLabelText("Lane for Ann Anchor") as HTMLSelectElement;
};

beforeEach(() => {
  cleanup();
  props.onPick.mockClear();
  props.onPickInRace.mockClear();
  props.onRemove.mockClear();
  props.onSwap.mockClear();
});

describe("EntryLaneControl", () => {
  it("offers the lanes it was given, no lane, and the removal", () => {
    render(<EntryLaneControl {...props} />);
    expect([...select().querySelectorAll("option")].map((o) => o.textContent)).toEqual([
      "No lane",
      "Lane 1",
      "Lane 2",
      "Lane 4",
      "Remove from race…",
    ]);
  });

  it("picks a lane, and parks a swimmer on 'No lane' without leaving the race", () => {
    render(<EntryLaneControl {...props} />);
    fireEvent.change(select(), { target: { value: "4" } });
    expect(props.onPick).toHaveBeenCalledWith(4);

    fireEvent.change(select(), { target: { value: "" } });
    expect(props.onPick).toHaveBeenLastCalledWith(null);
    expect(props.onRemove).not.toHaveBeenCalled();
  });

  it("offers no removal, and no parking, for a swimmer in no race at all", () => {
    // "Not in a race" is where they already are, so re-picking it is not a write
    // — and the first real pick is what MINTS the division's race.
    render(<EntryLaneControl {...props} inRace={false} lane={null} />);
    expect([...select().querySelectorAll("option")].map((o) => o.textContent)).toEqual([
      "Unplaced",
      "Lane 1",
      "Lane 2",
      "Lane 4",
    ]);
    fireEvent.change(select(), { target: { value: "" } });
    expect(props.onPick).not.toHaveBeenCalled();
  });

  it("arms rather than removes, and ignores the second half of a double-tap", () => {
    // Removal clears the heat, so it is never a casual dropdown pick — and on a
    // wet deck a double-tap is one gesture, which must not BE the confirmation.
    vi.useFakeTimers();
    try {
      render(<EntryLaneControl {...props} />);
      fireEvent.change(select(), { target: { value: "__remove" } });
      expect(props.onRemove).not.toHaveBeenCalled();

      const armed = screen.getByRole("button", {
        name: "Confirm removing Ann Anchor from this race",
      });
      fireEvent.click(armed);
      expect(props.onRemove).not.toHaveBeenCalled();

      vi.advanceTimersByTime(ARMING_LOCKOUT_MS);
      fireEvent.click(armed);
      expect(props.onRemove).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it("disarms itself once nobody is standing in it, so a bench-armed strip is not a removal waiting", () => {
    vi.useFakeTimers();
    try {
      render(<EntryLaneControl {...props} />);
      fireEvent.change(select(), { target: { value: "__remove" } });
      // Focus has LEFT the strip: the window is what it was written for.
      fireEvent.focusOut(screen.getByRole("button", { name: /^Confirm removing/ }));
      act(() => vi.advanceTimersByTime(4_100));
      expect(screen.queryByRole("button", { name: /^Confirm removing/ })).toBeNull();
    } finally {
      vi.useRealTimers();
    }
  });

  it("arms the same removal from the row's actions menu (ADR-0041 §2)", () => {
    render(<EntryLaneControl {...props} />);
    act(() => handle.current!.armRemove());
    expect(props.onRemove).not.toHaveBeenCalled();
    expect(
      screen.getByRole("button", { name: "Confirm removing Ann Anchor from this race" }),
    ).toBeTruthy();
  });

  it("moves focus to Confirm and announces the strip", () => {
    // The interaction is now in the strip, and a keyboard user was left on a
    // select whose value had not changed with the confirmation unannounced.
    render(<EntryLaneControl {...props} />);
    fireEvent.change(select(), { target: { value: "__remove" } });
    const armed = screen.getByRole("button", { name: "Confirm removing Ann Anchor from this race" });
    expect(document.activeElement).toBe(armed);
    expect(armed.closest('[role="alert"]')).toBeTruthy();
  });

  it("does not withdraw the confirmation from under the finger holding it", () => {
    vi.useFakeTimers();
    try {
      render(<EntryLaneControl {...props} />);
      fireEvent.change(select(), { target: { value: "__remove" } });
      act(() => vi.advanceTimersByTime(9_000));
      // Still armed: the auto-disarm is for an abandoned strip, not a read one.
      expect(screen.getByRole("button", { name: /^Confirm removing/ })).toBeTruthy();
    } finally {
      vi.useRealTimers();
    }
  });

  it("groups the division's OTHER races behind their own labels (ADR-0038 §3)", () => {
    render(
      <EntryLaneControl
        {...props}
        races={[
          { raceId: "r-1", title: "Race 1", lanes: [1, 2], home: true },
          { raceId: "r-2", title: "Race 2", lanes: [3], home: false },
        ]}
      />,
    );
    expect([...select().querySelectorAll("optgroup")].map((g) => g.label)).toEqual([
      "Race 1",
      "Race 2",
    ]);
    // The lane text is the lane, wherever it sits: the group says which race.
    expect([...select().querySelectorAll("option")].map((o) => o.textContent)).toEqual([
      "No lane",
      "Lane 1",
      "Lane 2",
      "Lane 3",
      "Remove from race…",
    ]);
  });

  it("arms rather than moves, and names the target race once confirmed", () => {
    // A pick in another race is a change of PLACING POOL, not of lane, so it is
    // two-step like Remove — and the write carries where to, because a plain
    // lane number would have gone to the race the row is leaving.
    vi.useFakeTimers();
    try {
      render(
        <EntryLaneControl
          {...props}
          races={[
            { raceId: "r-1", title: "Race 1", lanes: [1, 2], home: true },
            { raceId: "r-2", title: "Race 2", lanes: [3], home: false },
          ]}
        />,
      );
      fireEvent.change(select(), { target: { value: "__move:r-2:3" } });
      expect(props.onPickInRace).not.toHaveBeenCalled();
      expect(screen.getByText("Move to Race 2, lane 3")).toBeTruthy();

      const armed = screen.getByRole("button", {
        name: "Confirm moving Ann Anchor — Move to Race 2, lane 3",
      });
      // The same arming lockout the other two-step acts have.
      fireEvent.click(armed);
      expect(props.onPickInRace).not.toHaveBeenCalled();
      vi.advanceTimersByTime(ARMING_LOCKOUT_MS);
      fireEvent.click(armed);
      expect(props.onPickInRace).toHaveBeenCalledWith("r-2", 3);
      expect(props.onPick).not.toHaveBeenCalled();

      // A lane in the row's OWN race is still one tap: nothing is re-heated.
      fireEvent.change(select(), { target: { value: "2" } });
      expect(props.onPick).toHaveBeenCalledWith(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it("queues nothing while a keyboard walks ACROSS the groups", () => {
    // Arrow-stepping a select fires a change per step: unarmed, walking past
    // Race 2 on the way to Race 3 would have moved the swimmer twice.
    render(
      <EntryLaneControl
        {...props}
        races={[
          { raceId: "r-1", title: "Race 1", lanes: [1, 2], home: true },
          { raceId: "r-2", title: "Race 2", lanes: [3], home: false },
          { raceId: "r-3", title: "Race 3", lanes: [4], home: false },
        ]}
      />,
    );
    fireEvent.change(select(), { target: { value: "__move:r-2:3" } });
    fireEvent.change(select(), { target: { value: "__move:r-3:4" } });
    expect(props.onPickInRace).not.toHaveBeenCalled();
    // The strip follows the walk: it describes where the cursor is now.
    expect(screen.getByText("Move to Race 3, lane 4")).toBeTruthy();
    expect(screen.queryByText("Move to Race 2, lane 3")).toBeNull();
  });

  it("disarms a move whose lane filled up underneath the strip", () => {
    const { rerender } = render(
      <EntryLaneControl
        {...props}
        races={[
          { raceId: "r-1", title: "Race 1", lanes: [1, 2], home: true },
          { raceId: "r-2", title: "Race 2", lanes: [3], home: false },
        ]}
      />,
    );
    fireEvent.change(select(), { target: { value: "__move:r-2:3" } });
    rerender(
      <EntryLaneControl
        {...props}
        races={[
          { raceId: "r-1", title: "Race 1", lanes: [1, 2], home: true },
          { raceId: "r-2", title: "Race 2", lanes: [5], home: false },
        ]}
      />,
    );
    expect(screen.queryByRole("button", { name: /^Confirm moving/ })).toBeNull();
  });

  it("still offers every race to a row in NONE of them — no dead end (ADR-0038 §3)", () => {
    vi.useFakeTimers();
    try {
      render(
        <EntryLaneControl
          {...props}
          inRace={false}
          lane={null}
          races={[
            { raceId: "r-1", title: "Race 1", lanes: [2], home: false },
            { raceId: "r-2", title: "Race 2", lanes: [1], home: false },
          ]}
        />,
      );
      expect([...select().querySelectorAll("option")].map((o) => o.textContent)).toEqual([
        "Unplaced",
        "Lane 2",
        "Lane 1",
      ]);
      fireEvent.change(select(), { target: { value: "__move:r-1:2" } });
      vi.advanceTimersByTime(ARMING_LOCKOUT_MS);
      fireEvent.click(screen.getByRole("button", { name: /^Confirm moving/ }));
      // Unraced: the pick is a placement, and the confirm is what fires it.
      expect(props.onPickInRace).toHaveBeenCalledWith("r-1", 2);
    } finally {
      vi.useRealTimers();
    }
  });

  it("renders no group that has nothing under it", () => {
    // An <optgroup> with no options is a label over nothing; Remove and the
    // swaps live outside the groups, so a home group can empty out too.
    render(
      <EntryLaneControl
        {...props}
        lane={null}
        races={[
          { raceId: "r-1", title: "Race 1", lanes: [], home: true },
          { raceId: "r-2", title: "Race 2", lanes: [3], home: false },
        ]}
      />,
    );
    expect([...select().querySelectorAll("optgroup")].map((g) => g.label)).toEqual(["Race 2"]);
    expect([...select().querySelectorAll("option")].map((o) => o.textContent)).toEqual([
      "No lane",
      "Lane 3",
      "Remove from race…",
    ]);
  });

  it("disarms when the row moves underneath it", () => {
    // Another device placed this swimmer while the strip sat armed: what the
    // confirm would remove is no longer what was armed.
    const { rerender } = render(<EntryLaneControl {...props} />);
    fireEvent.change(select(), { target: { value: "__remove" } });
    expect(screen.getByRole("button", { name: /^Confirm removing/ })).toBeTruthy();

    rerender(<EntryLaneControl {...props} lane={2} />);
    expect(screen.queryByRole("button", { name: /^Confirm removing/ })).toBeNull();
  });
});

/**
 * ADR-0030. An occupied lane is offered as a named swap, and confirming it is
 * the only act on this control that moves a swimmer who is not on this row —
 * so it is two-step, like Remove.
 */
describe("EntryLaneControl swaps", () => {
  it("names the occupant rather than offering a bare taken lane", () => {
    render(<EntryLaneControl {...props} swaps={[marley]} />);
    expect([...select().querySelectorAll("option")].map((o) => o.textContent)).toEqual([
      "No lane",
      "Lane 1",
      "Lane 2",
      "Lane 4",
      "Lane 3 — swap with Marley Singh",
      "Remove from race…",
    ]);
  });

  it("arms rather than swaps, and ignores the second half of a double-tap", () => {
    vi.useFakeTimers();
    try {
      render(<EntryLaneControl {...props} swaps={[marley]} />);
      fireEvent.change(select(), { target: { value: "__swap:en-2" } });
      expect(props.onSwap).not.toHaveBeenCalled();

      const armed = screen.getByRole("button", {
        name: "Confirm swapping Ann Anchor with Marley Singh",
      });
      fireEvent.click(armed);
      expect(props.onSwap).not.toHaveBeenCalled();

      vi.advanceTimersByTime(ARMING_LOCKOUT_MS);
      fireEvent.click(armed);
      expect(props.onSwap).toHaveBeenCalledWith(marley);
      // The swap is not the removal, whatever the strip looks like.
      expect(props.onRemove).not.toHaveBeenCalled();
      expect(props.onPick).not.toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  });

  it("disarms when the other half of the swap moves away", () => {
    // Another device emptied lane 3 while the strip sat armed: confirming now
    // would exchange lanes with nobody.
    const { rerender } = render(<EntryLaneControl {...props} swaps={[marley]} />);
    fireEvent.change(select(), { target: { value: "__swap:en-2" } });
    expect(screen.getByRole("button", { name: /^Confirm swapping/ })).toBeTruthy();

    rerender(<EntryLaneControl {...props} swaps={[]} />);
    expect(screen.queryByRole("button", { name: /^Confirm swapping/ })).toBeNull();
  });

  it("disarms when somebody ELSE is in the armed lane", () => {
    const { rerender } = render(<EntryLaneControl {...props} swaps={[marley]} />);
    fireEvent.change(select(), { target: { value: "__swap:en-2" } });
    rerender(
      <EntryLaneControl
        {...props}
        swaps={[{ lane: 3, entryId: "en-9", displayName: "Nina Novak" }]}
      />,
    );
    expect(screen.queryByRole("button", { name: /^Confirm swapping/ })).toBeNull();
  });

  it("keeps two occupants of ONE lane apart, and arms the one that was picked", () => {
    // A queued placement over a stale read can show two rows in one lane for a
    // moment; a lane-keyed option would collide and arm the wrong swimmer.
    const nina = { lane: 3, entryId: "en-9", displayName: "Nina Novak" };
    render(<EntryLaneControl {...props} swaps={[marley, nina]} />);
    const values = [...select().querySelectorAll("option")].map((o) => o.value);
    expect(new Set(values).size).toBe(values.length);

    fireEvent.change(select(), { target: { value: "__swap:en-9" } });
    expect(
      screen.getByRole("button", { name: "Confirm swapping Ann Anchor with Nina Novak" }),
    ).toBeTruthy();
  });

  it("offers no swap to a swimmer holding no lane — they have nothing to give", () => {
    // The screen withholds the offers; the control must not invent them from
    // the lane list either.
    render(<EntryLaneControl {...props} lane={null} swaps={[]} />);
    expect(
      [...select().querySelectorAll("option")].some((o) => o.textContent?.includes("swap")),
    ).toBe(false);
  });

  it("cancels back to the picker without writing anything", () => {
    render(<EntryLaneControl {...props} swaps={[marley]} />);
    fireEvent.change(select(), { target: { value: "__swap:en-2" } });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));
    expect(screen.queryByRole("button", { name: /^Confirm swapping/ })).toBeNull();
    expect(props.onSwap).not.toHaveBeenCalled();
    // The select never held the swap value: it still shows where the row IS.
    expect(select().value).toBe("1");
  });
});

/**
 * ADR-0020's deferred item: on Windows Chrome/Firefox an arrow key through an
 * open select fires `change` per option, so walking from lane 1 to lane 4 used
 * to queue an assign for every lane on the way. The pick is now KEYBOARD-
 * COMMITTED — Enter or blur — while a mouse or touch pick is untouched.
 */
describe("EntryLaneControl keyboard traversal", () => {
  /** One arrow step: the highlight moves, and the browser fires `change`. */
  function arrowTo(value: string) {
    fireEvent.keyDown(select(), { key: "ArrowDown" });
    fireEvent.change(select(), { target: { value } });
  }

  it("writes nothing for the lanes an arrow key passes through", () => {
    render(<EntryLaneControl {...props} />);
    arrowTo("2");
    arrowTo("4");
    expect(props.onPick).not.toHaveBeenCalled();
    // The marshal can still SEE where the highlight is.
    expect(select().value).toBe("4");
  });

  it("writes once, for where the traversal stopped", () => {
    render(<EntryLaneControl {...props} />);
    arrowTo("2");
    arrowTo("4");
    fireEvent.keyDown(select(), { key: "Enter" });
    expect(props.onPick).toHaveBeenCalledTimes(1);
    expect(props.onPick).toHaveBeenCalledWith(4);
  });

  it("commits a traversal the marshal tabbed away from", () => {
    render(<EntryLaneControl {...props} />);
    arrowTo("2");
    fireEvent.blur(select());
    expect(props.onPick).toHaveBeenCalledTimes(1);
    expect(props.onPick).toHaveBeenCalledWith(2);
  });

  it("writes nothing at all when the traversal is abandoned with Escape", () => {
    render(<EntryLaneControl {...props} />);
    arrowTo("2");
    fireEvent.keyDown(select(), { key: "Escape" });
    fireEvent.blur(select());
    expect(props.onPick).not.toHaveBeenCalled();
    expect(select().value).toBe("1"); // back to where the row IS
  });

  it("arms rather than fires when a traversal stops on a two-step option", () => {
    // The arming options are the reason this matters most: Remove clears the
    // heat, and a swap moves somebody else's swimmer.
    render(<EntryLaneControl {...props} swaps={[marley]} />);
    arrowTo("__swap:en-2");
    expect(screen.queryByRole("button", { name: /^Confirm swapping/ })).toBeNull();
    fireEvent.keyDown(select(), { key: "Enter" });
    expect(
      screen.getByRole("button", { name: "Confirm swapping Ann Anchor with Marley Singh" }),
    ).toBeTruthy();
    expect(props.onSwap).not.toHaveBeenCalled();
  });

  it("leaves a mouse or touch pick writing on its one change event", () => {
    render(<EntryLaneControl {...props} />);
    fireEvent.mouseDown(select());
    fireEvent.change(select(), { target: { value: "4" } });
    expect(props.onPick).toHaveBeenCalledWith(4);
  });

  it("does not write twice when Enter is followed by the select's own blur", () => {
    render(<EntryLaneControl {...props} />);
    arrowTo("2");
    fireEvent.keyDown(select(), { key: "Enter" });
    fireEvent.blur(screen.queryByLabelText("Lane for Ann Anchor") ?? document.body);
    expect(props.onPick).toHaveBeenCalledTimes(1);
  });
});
