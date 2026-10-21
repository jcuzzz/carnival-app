import { describe, expect, it } from "vitest";
import type { CarnivalEvent } from "../../api/types";
import type { OutboxItem, OutboxStatus } from "../../db/db";
import { progressPercent, progressText, queuedProgressByEvent, sumProgress } from "./progress";

/**
 * Progress with the uplink down (ADR-0014 §1). The server aggregates on the
 * event list stop moving the moment the network does, so everything captured
 * after that is only in the outbox — and an organiser hunting stragglers at
 * 14:30 is reading these numbers, not the clock.
 */

function event(over: Partial<CarnivalEvent> & Pick<CarnivalEvent, "id">): CarnivalEvent {
  return {
    carnivalId: "c-1",
    eventDefinitionId: "def-free",
    definitionName: "Freestyle 50m",
    ageGroup: "13",
    category: "F",
    tier: "Competitive",
    scoringMode: null,
    effectiveScoringMode: "Places",
    scoresPoints: true,
    scheduleSlotId: null,
    slotOrder: null,
    status: "Open",
    version: 1,
    entryCount: 0,
    resultCount: 0,
    officials: [],
    ...over,
  } as CarnivalEvent;
}

let seq = 0;
function item(
  kind: string,
  payload: Record<string, unknown>,
  status: OutboxStatus = "queued",
): OutboxItem {
  return {
    id: `item-${++seq}`,
    kind,
    payload,
    actorSubject: "official-1",
    capturedAt: "2026-08-07T04:00:00Z",
    status,
    attempts: 0,
  };
}

const result = (eventId: string, entryId: string, status?: OutboxStatus) =>
  item("result.submit", { carnivalEventId: eventId, entryId }, status);

describe("queuedProgressByEvent", () => {
  it("counts undelivered captures per event", () => {
    const queued = queuedProgressByEvent([
      result("ev-1", "en-1"),
      result("ev-1", "en-2", "syncing"),
      result("ev-2", "en-9"),
      item("entry.create", { carnivalEventId: "ev-1", studentId: "st-late" }),
    ]);
    expect(queued.get("ev-1")).toEqual({ entries: 1, results: 2 });
    expect(queued.get("ev-2")).toEqual({ entries: 0, results: 1 });
  });

  it("never counts an item the server has already answered", () => {
    // `accepted` is in the server's own count (and invalidates the list key, so
    // the aggregate is on its way); `rejected`/`conflict` are refusals, and
    // reporting a refused capture as progress says a swim is done that isn't.
    // `cancelled` was withdrawn inside the undo window and never left the device.
    const queued = queuedProgressByEvent([
      result("ev-1", "en-1", "accepted"),
      result("ev-1", "en-2", "rejected"),
      result("ev-1", "en-3", "conflict"),
      result("ev-1", "en-4", "cancelled"),
    ]);
    expect(queued.size).toBe(0);
  });

  it("counts one swim per entry however many times it was re-saved", () => {
    const queued = queuedProgressByEvent([result("ev-1", "en-1"), result("ev-1", "en-1")]);
    expect(queued.get("ev-1")?.results).toBe(1);
  });

  /** ADR-0053 §3 / ADR-0056 §2: many writes, one result — so one swim. */
  it("counts a queued attempt or jump as the entry's one undelivered result", () => {
    const queued = queuedProgressByEvent([
      item("result.attempt", { carnivalEventId: "ev-1", entryId: "en-1", ordinal: 1 }),
      item("result.attempt", { carnivalEventId: "ev-1", entryId: "en-1", ordinal: 2 }),
      item("result.jump", { carnivalEventId: "ev-1", entryId: "en-2", height: 1.3 }),
      item("result.jump", { carnivalEventId: "ev-1", entryId: "en-2", height: 1.35 }),
    ]);
    expect(queued.get("ev-1")).toEqual({ entries: 0, results: 2 });
  });

  it("never counts an entry twice when a submit and a mark are both queued", () => {
    const queued = queuedProgressByEvent([
      result("ev-1", "en-1"),
      item("result.jump", { carnivalEventId: "ev-1", entryId: "en-1", height: 1.3 }),
    ]);
    expect(queued.get("ev-1")?.results).toBe(1);
  });

  it("counts a queued relay, once per house (M7)", () => {
    // `deckRows` already draws the queued team, so leaving it out of the
    // overlay is the ADR-0039 B3 contradiction: a heat chip reading 4/4
    // complete above five rows. The house is the server's own team identity
    // (TEAM_EXISTS), so a re-captured relay is still one entry.
    const queued = queuedProgressByEvent([
      item("team.create", { carnivalEventId: "ev-1", houseCode: "BANK", studentIds: ["st-1"] }),
      item("team.create", { carnivalEventId: "ev-1", houseCode: "BANK", studentIds: ["st-2"] }),
      item("team.create", { carnivalEventId: "ev-1", houseCode: "WATTLE", studentIds: ["st-3"] }),
      item("team.create", { carnivalEventId: "ev-1", houseCode: "YARRA" }, "rejected"),
    ]);
    expect(queued.get("ev-1")).toEqual({ entries: 2, results: 0 });
  });

  it("ignores corrections — the result they replace is already counted", () => {
    const queued = queuedProgressByEvent([
      item("result.correct", { carnivalEventId: "ev-1", entryId: "en-1", resultId: "res-1" }),
    ]);
    expect(queued.size).toBe(0);
  });
});

describe("sumProgress", () => {
  it("overlays queued captures on the server aggregate", () => {
    // The 09:00 list said 2 of 6; four more have been captured since.
    const progress = sumProgress(
      [event({ id: "ev-1", entryCount: 6, resultCount: 2 })],
      queuedProgressByEvent(["en-3", "en-4", "en-5", "en-6"].map((e) => result("ev-1", e))),
    );
    expect(progressText(progress)).toBe("6/6");
  });

  it("raises the denominator for a swimmer added offline, so it cannot exceed 100%", () => {
    const progress = sumProgress(
      [event({ id: "ev-1", entryCount: 6, resultCount: 6 })],
      queuedProgressByEvent([
        item("entry.create", { carnivalEventId: "ev-1", studentId: "st-late" }),
        result("ev-1", "en-late"),
      ]),
    );
    expect(progressText(progress)).toBe("7/7");
  });

  it("raises the denominator for a relay added offline, so the division is not complete", () => {
    const progress = sumProgress(
      [event({ id: "ev-1", entryCount: 4, resultCount: 4 })],
      queuedProgressByEvent([
        item("team.create", { carnivalEventId: "ev-1", houseCode: "BANK", studentIds: ["st-1"] }),
      ]),
    );
    expect(progressText(progress)).toBe("4/5");
    expect(progressPercent(progress)).toBe(80);
  });

  it("clamps at the denominator: a card's queued marks are a result the server has", () => {
    // ADR-0056 §4: a jump per entry is counted as a queued result, but the
    // server already counts the athlete once a mark landed. Without the clamp a
    // ladder tile reads 11/8 and the bar fills past 100%.
    const progress = sumProgress(
      [event({ id: "ev-1", entryCount: 8, resultCount: 8 })],
      queuedProgressByEvent(
        ["en-1", "en-2", "en-3"].map((e) =>
          item("result.jump", { carnivalEventId: "ev-1", entryId: e, height: 1.4, mark: "X" }),
        ),
      ),
    );
    expect(progress.resultCount).toBe(8);
    expect(progressText(progress)).toBe("8/8");
    expect(progressPercent(progress)).toBe(100);
  });

  it("reads a Social-scored division by its entries, never as results owed", () => {
    // ADR-0014 §4: the points are the entries. "0/8" would send the straggler
    // hunt after eight swims that were never meant to exist.
    const progress = sumProgress([
      event({ id: "s1", tier: "Social", effectiveScoringMode: "Social", entryCount: 8 }),
    ]);
    expect(progress.expectedCount).toBe(0);
    expect(progressText(progress)).toBe("8 entered");
  });

  it("reads the numerator as unknown while the results read is down (M13)", () => {
    const progress = sumProgress([event({ id: "ev-1", entryCount: 6, resultCount: 2 })]);
    expect(progressText(progress, true)).toBe("?/6");
    // Nothing is owed on a Social division, so a failed results read says
    // nothing about it either.
    const social = sumProgress([
      event({ id: "s1", tier: "Social", effectiveScoringMode: "Social", entryCount: 8 }),
    ]);
    expect(progressText(social, true)).toBe("8 entered");
  });
});
