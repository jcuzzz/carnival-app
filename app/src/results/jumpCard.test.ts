import { describe, expect, it } from "vitest";
import type { Jump } from "../api/types";
import type { OutboxItem } from "../db/db";
import type { ResultJumpPayload } from "../api/outboxTransport";
import {
  attemptNumber,
  cellAt,
  deviceMarksAt,
  eliminated,
  formatHeight,
  heightKey,
  isLastHeight,
  markable,
  markedCells,
  nextBarHeight,
  nextFailEliminates,
  cardCleared,
  describeDropped,
  editCard,
  sameHeight,
} from "./jumpCard";

/**
 * The display predicates only (ADR-0056 §1). Nothing here decides a placing —
 * the server owns best, status and the countback; these decide what the grid
 * draws and which cells a finger can reach.
 */

const card = (...entries: [number, string][]): Jump[] =>
  entries.map(([height, marks]) => ({ height, marks }));

function queuedMark(height: number, mark: "O" | "X" | "-", status: OutboxItem["status"]) {
  const payload: ResultJumpPayload = {
    carnivalId: "c-1",
    carnivalEventId: "ev-1",
    entryId: "en-1",
    height,
    mark,
  };
  return {
    id: `item-${height}-${mark}-${status}`,
    kind: "result.jump",
    payload,
    actorSubject: "official-1",
    capturedAt: "2027-03-06T01:00:00Z",
    status,
    attempts: 0,
  } as OutboxItem;
}

describe("heights compare by value, never by spelling", () => {
  it("reads 1.2 and 1.20 as one height, and rounds float drift out", () => {
    expect(sameHeight(0.1 + 0.2, 0.3)).toBe(true);
    expect(heightKey(1.35)).toBe(heightKey(1.3500000001));
    expect(sameHeight(1.2, 1.25)).toBe(false);
    expect(formatHeight(1.2)).toBe("1.20");
  });
});

describe("the card as the cell reads it", () => {
  it("appends this device's un-drained marks after the stored ones", () => {
    const queued = new Map([[heightKey(1.3), [queuedMark(1.3, "X", "queued")]]]);
    expect(cellAt(card([1.25, "O"], [1.3, "X"]), queued, 1.3)).toEqual({
      height: 1.3,
      stored: "X",
      queued: "X",
      marks: "XX",
    });
  });

  it("hands the cell back to the card once the mark is ACCEPTED", () => {
    const queued = new Map([[heightKey(1.3), [queuedMark(1.3, "X", "accepted")]]]);
    expect(cellAt(card([1.3, "X"]), queued, 1.3).marks).toBe("X");
  });

  it("keeps every queued mark at one height — three taps are three marks", () => {
    const queued = new Map([
      [
        heightKey(1.35),
        [queuedMark(1.35, "X", "queued"), queuedMark(1.35, "X", "syncing")],
      ],
    ]);
    expect(cellAt(undefined, queued, 1.35).marks).toBe("XX");
  });

  it("orders the card by height, so the last height is the athlete's own", () => {
    const cells = markedCells(card([1.3, "XXO"], [1.25, "O"], [1.35, "XX"]), undefined);
    expect(cells.map((cell) => `${cell.height} ${cell.marks}`)).toEqual([
      "1.25 O",
      "1.3 XXO",
      "1.35 XX",
    ]);
    expect(isLastHeight(cells, 1.35)).toBe(true);
    expect(isLastHeight(cells, 1.3)).toBe(false);
  });
});

describe("elimination: three consecutive fails, a clearance breaking the run", () => {
  it("is out on XXX at one height", () => {
    expect(eliminated(markedCells(card([1.2, "O"], [1.25, "XXX"]), undefined))).toBe(true);
  });

  it("is out on a run that spans two heights", () => {
    expect(eliminated(markedCells(card([1.25, "XX"], [1.3, "X"]), undefined))).toBe(true);
  });

  it("is not out where a clearance sits inside the run", () => {
    expect(eliminated(markedCells(card([1.25, "XXO"], [1.3, "X"]), undefined))).toBe(false);
  });

  it("carries the run through a pass — a pass is not a fail and does not break it", () => {
    expect(eliminated(markedCells(card([1.25, "XX-"], [1.3, "X"]), undefined))).toBe(true);
  });
});

describe("which cells a finger can reach", () => {
  const cells = (...entries: [number, string][]) => markedCells(card(...entries), undefined);

  it("takes a mark at an untouched height on the ladder", () => {
    expect(markable(cells([1.25, "O"]), 1.3)).toBe(true);
  });

  it("refuses a height BELOW one already jumped — the bar never comes down", () => {
    expect(markable(cells([1.25, "O"], [1.3, "X"]), 1.25)).toBe(false);
  });

  it("refuses a cleared, a passed and an exhausted height", () => {
    expect(markable(cells([1.25, "O"]), 1.25)).toBe(false);
    expect(markable(cells([1.25, "X-"]), 1.25)).toBe(false);
    expect(markable(cells([1.25, "XXX"]), 1.25)).toBe(false);
  });

  it("refuses every cell of an eliminated athlete", () => {
    expect(markable(cells([1.25, "XXX"]), 1.3)).toBe(false);
  });

  it("refuses every cell when the row is closed (a typed status, a final result)", () => {
    expect(markable(cells([1.25, "O"]), 1.3, true)).toBe(false);
  });

  it("counts the attempt about to be taken", () => {
    expect(attemptNumber(cells([1.35, "XX"]), 1.35)).toBe(3);
    expect(attemptNumber(cells([1.25, "O"]), 1.3)).toBe(1);
  });
});

describe("the warning the sheet shows before the last fail", () => {
  it("fires on two consecutive fails standing", () => {
    expect(nextFailEliminates(markedCells(card([1.35, "XX"]), undefined), 1.35)).toBe(true);
    expect(nextFailEliminates(markedCells(card([1.3, "X"], [1.35, "X"]), undefined), 1.35)).toBe(
      true,
    );
  });

  it("does not fire where a clearance broke the run", () => {
    expect(nextFailEliminates(markedCells(card([1.3, "XXO"], [1.35, "X"]), undefined), 1.35)).toBe(
      false,
    );
  });
});

describe("the next bar", () => {
  it("is the opening height where nothing has been set", () => {
    expect(nextBarHeight([], { start: 1.2, step: 0.05 })).toBe(1.2);
  });

  it("is one step above the last height", () => {
    expect(nextBarHeight([1.2, 1.25, 1.35], { start: 1.2, step: 0.05 })).toBe(1.4);
  });

  it("is nothing at all without a ladder", () => {
    expect(nextBarHeight([1.2], null)).toBeNull();
  });
});

/**
 * A refused capture is one the server ANSWERED (ADR-0025 §1): it stays on
 * screen so the official sees the work, but the card behind it is the server's.
 */
describe("a parked mark", () => {
  const parked = queuedMark(1.3, "X", "rejected");

  it("is kept out of every predicate", () => {
    const stored = card([1.3, "XX"]);
    const queued = new Map([[heightKey(1.3), [parked]]]);
    expect(cellAt(stored, queued, 1.3).marks).toBe("XX");
    // Counted, the third X would end the athlete's card.
    expect(eliminated(markedCells(stored, queued))).toBe(false);
    expect(markable(markedCells(stored, queued), 1.3)).toBe(true);
    expect(attemptNumber(markedCells(stored, queued), 1.3)).toBe(3);
  });

  it("is still readable in the cell it was captured in", () => {
    expect(deviceMarksAt([queuedMark(1.3, "O", "queued"), parked])).toBe("OX");
    expect(deviceMarksAt([queuedMark(1.3, "O", "accepted"), parked])).toBe("X");
  });
});

describe("two attempts per height (amendment 2026-09-25)", () => {
  const cells = (...entries: [number, string][]) => markedCells(card(...entries), undefined);

  it("is out when both attempts at one height fail, and nothing carries past a pass", () => {
    expect(eliminated(cells([1.25, "XX"]), 2)).toBe(true);
    expect(eliminated(cells([1.25, "X-"], [1.3, "X-"], [1.35, "X"]), 2)).toBe(false);
    expect(eliminated(cells([1.25, "X-"], [1.3, "X-"], [1.35, "X"]), 3)).toBe(true);
  });

  it("stops a height taking marks after XX, and after X then O", () => {
    expect(markable(cells([1.25, "X"]), 1.25, false, 2)).toBe(true);
    expect(markable(cells([1.25, "XX"]), 1.25, false, 2)).toBe(false);
    expect(markable(cells([1.25, "XO"]), 1.25, false, 2)).toBe(false);
    expect(markable(cells([1.25, "XX"]), 1.3, false, 2)).toBe(false);
    expect(markable(cells([1.25, "XX"]), 1.25)).toBe(true);
  });

  it("warns only when this height already reads X", () => {
    expect(nextFailEliminates(cells([1.25, "X"]), 1.25, 2)).toBe(true);
    expect(nextFailEliminates(cells([1.25, "X-"], [1.3, "X"]), 1.3, 2)).toBe(true);
    expect(nextFailEliminates(cells([1.25, "X-"]), 1.3, 2)).toBe(false);
    expect(nextFailEliminates(cells([1.25, "O"]), 1.3, 2)).toBe(false);
    expect(attemptNumber(cells([1.25, "X"]), 1.25)).toBe(2);
  });
});

describe("editing one mark (amendment 2026-09-25 (c))", () => {
  it("changes a mark with nothing else to drop", () => {
    const before = card([1.2, "O"], [1.25, "XX"]);
    expect(editCard(before, 1.25, 1, "O", 3)).toEqual({
      card: card([1.2, "O"], [1.25, "XO"]),
      dropped: [],
    });
    expect(editCard(card([1.2, "X"]), 1.2, 0, "O", 2)).toEqual({
      card: card([1.2, "O"]),
      dropped: [],
    });
  });

  it("drops the marks after a clearance made mid-height", () => {
    expect(editCard(card([1.25, "XXO"], [1.3, "X"]), 1.25, 0, "O", 3)).toEqual({
      card: card([1.25, "O"], [1.3, "X"]),
      dropped: [
        { height: 1.25, attempt: 2, mark: "X" },
        { height: 1.25, attempt: 3, mark: "O" },
      ],
    });
    expect(editCard(card([1.25, "XO"]), 1.25, 0, "-", 2)).toEqual({
      card: card([1.25, "-"]),
      dropped: [{ height: 1.25, attempt: 2, mark: "O" }],
    });
  });

  it("drops everything after the jump that now eliminates the athlete", () => {
    expect(editCard(card([1.2, "XXO"], [1.25, "X"]), 1.2, 2, "X", 3)).toEqual({
      card: card([1.2, "XXX"]),
      dropped: [{ height: 1.25, attempt: 1, mark: "X" }],
    });
    // At three the run crosses heights; at two it does not.
    expect(editCard(card([1.2, "XO"], [1.25, "XXO"]), 1.2, 1, "-", 3).dropped).toEqual([
      { height: 1.25, attempt: 3, mark: "O" },
    ]);
    expect(editCard(card([1.2, "XO"], [1.25, "O"]), 1.2, 1, "X", 2)).toEqual({
      card: card([1.2, "XX"]),
      dropped: [{ height: 1.25, attempt: 1, mark: "O" }],
    });
  });

  it("closes the gap where a middle mark is removed", () => {
    expect(editCard(card([1.25, "XXO"]), 1.25, 1, null, 3)).toEqual({
      card: card([1.25, "XO"]),
      dropped: [],
    });
    expect(editCard(card([1.25, "XO"], [1.3, "X"]), 1.25, 0, null, 2)).toEqual({
      card: card([1.25, "O"], [1.3, "X"]),
      dropped: [],
    });
  });

  it("takes a height off the card when its only mark is removed", () => {
    expect(editCard(card([1.2, "O"], [1.25, "X"]), 1.25, 0, null, 3)).toEqual({
      card: card([1.2, "O"]),
      dropped: [],
    });
    expect(editCard(card([1.2, "O"]), 1.2, 0, null, 2)).toEqual({ card: [], dropped: [] });
  });

  it("names the drops, a whole height by the height alone", () => {
    const out = card([1.2, "XXO"], [1.25, "O"], [1.3, "XO"]);
    expect(describeDropped(editCard(out, 1.2, 2, "X", 3).dropped, out)).toBe(
      "1.25 m and 1.30 m",
    );
    const cleared = card([1.25, "XXO"], [1.3, "XO"]);
    expect(describeDropped(editCard(cleared, 1.25, 0, "O", 3).dropped, cleared)).toBe(
      "attempts 2 and 3 at 1.25 m",
    );
    expect(
      describeDropped(
        [
          { height: 1.25, attempt: 3, mark: "X" },
          { height: 1.3, attempt: 1, mark: "O" },
        ],
        card([1.25, "XXX"], [1.3, "O"]),
      ),
    ).toBe("attempt 3 at 1.25 m and 1.30 m");
  });
});

describe("a cleared card (amendment 2026-09-25 (c))", () => {
  it("is a Height result at NM holding no card, and nothing else", () => {
    expect(cardCleared({ status: "Nm", jumps: null }, "Height")).toBe(true);
    expect(cardCleared({ status: "Nm", jumps: [] }, "Height")).toBe(true);
    // Still jumping: a card with no clearance yet is recorded.
    expect(cardCleared({ status: "Nm", jumps: card([1.2, "X"]) }, "Height")).toBe(false);
    // A typed NM on a single-measure field event is a result.
    expect(cardCleared({ status: "Nm", jumps: null }, "Distance")).toBe(false);
    expect(cardCleared({ status: "Dns", jumps: null }, "Height")).toBe(false);
    expect(cardCleared(undefined, "Height")).toBe(false);
  });
});
