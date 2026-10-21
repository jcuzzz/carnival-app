// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
import { QueryWrapper, renderWithQuery } from "../../test/queryWrapper";
import type { CarnivalEvent, Entry, ResultDto } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import type { ResultJumpPayload } from "../../api/outboxTransport";
import { heightKey } from "../../results/jumpCard";
import { HighJumpGrid, type JumpGridRow } from "./HighJumpGrid";

/**
 * The card grid and the box it expands in place (ADR-0056 §4, amended 2026-09-21
 * and 2026-09-25 (b), (c)). What is pinned here is the part the screenshot pass
 * cannot assert: which boxes are reachable, where the expansion goes and where
 * focus lands, that a tap is ONE `result.jump`, that a queued mark shows beside
 * the card rather than replacing it, and that a marked box edits its own mark —
 * a queued one by cancelling, a stored one as a correction of the whole card.
 */

vi.mock("../../api/syncSetup", () => ({ syncWorker: { syncNow: vi.fn() } }));
vi.mock("../../sync/outbox", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../sync/outbox")>()),
  cancelIfPending: vi.fn(),
  requeue: vi.fn(),
}));
vi.mock("../../results/submitJump", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../results/submitJump")>()),
  submitJumpViaOutbox: vi.fn(),
}));
vi.mock("../../results/correctResult", () => ({ submitCorrection: vi.fn() }));
vi.mock("../../results/submitResult", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../results/submitResult")>()),
  submitResultViaOutbox: vi.fn(),
}));
vi.mock("../../api/resources", () => ({ raiseBar: vi.fn(), setAttemptsPerHeight: vi.fn() }));

import { cancelIfPending, requeue } from "../../sync/outbox";
import { submitJumpViaOutbox } from "../../results/submitJump";
import { submitCorrection } from "../../results/correctResult";
import { submitResultViaOutbox } from "../../results/submitResult";
import { raiseBar, setAttemptsPerHeight } from "../../api/resources";

const event: CarnivalEvent = {
  id: "ev-1",
  carnivalId: "c-1",
  eventDefinitionId: "def-1",
  definitionCode: "AT-HIGH-JUMP",
  definitionName: "High Jump",
  ageGroup: "14",
  category: "M",
  measureType: "Height",
  attemptCount: null,
  ladder: { start: 1.2, step: 0.05 },
  heights: [1.25, 1.3, 1.35],
  attemptsPerHeight: 3,
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

function entry(id: string, displayName: string): Entry {
  return {
    id,
    carnivalEventId: "ev-1",
    studentId: `st-${id}`,
    displayName,
    houseCode: "WATTLE",
    yearLevel: 8,
    ageGroup: "14",
    heat: null,
    lane: null,
    raceId: null,
    isTeam: false,
    overrideApplied: false,
  } as Entry;
}

function result(over: Partial<ResultDto>): ResultDto {
  return {
    id: "res-1",
    entryId: "en-1",
    status: "Ok",
    measuredValue: 1.3,
    jumps: null,
    lifecycleState: "Verified",
    currentRevision: 2,
    recordedAtUtc: "2027-03-06T01:00:00Z",
    recordedBySubject: "official-1",
    place: null,
    ...over,
  };
}

function queuedItem(
  id: string,
  height: number,
  mark: "O" | "X" | "-",
  status: OutboxItem["status"] = "queued",
  serverResponse?: Record<string, unknown>,
): OutboxItem {
  const payload: ResultJumpPayload = {
    carnivalId: "c-1",
    carnivalEventId: "ev-1",
    entryId: "en-1",
    height,
    mark,
  };
  return {
    id,
    kind: "result.jump",
    payload,
    actorSubject: "official-1",
    capturedAt: "2027-03-06T01:00:00Z",
    status,
    attempts: 0,
    ...(serverResponse ? { serverResponse } : {}),
  } as OutboxItem;
}

function submitItem(
  status: OutboxItem["status"],
  over?: { kind?: string; statusCode?: string },
): OutboxItem {
  return {
    id: "sub-1",
    kind: over?.kind ?? "result.submit",
    payload: {
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      value: null,
      status: over?.statusCode ?? "Dns",
    },
    actorSubject: "official-1",
    capturedAt: "2027-03-06T01:05:00Z",
    status,
    attempts: 1,
  } as OutboxItem;
}

const rows: JumpGridRow[] = [
  { entry: entry("en-1", "Finley Larsen"), createItem: undefined, inRace: false },
  { entry: entry("en-2", "Blake Costa"), createItem: undefined, inRace: false },
];

function renderGrid(over?: {
  event?: Partial<CarnivalEvent>;
  results?: [string, ResultDto][];
  jumps?: [string, Map<string, OutboxItem[]>][];
  outbox?: [string, OutboxItem][];
  actorSubject?: string | null;
  resultsUnavailable?: boolean;
}) {
  return renderWithQuery(
    <HighJumpGrid
      carnivalId="c-1"
      event={{ ...event, ...over?.event }}
      rows={rows}
      resultsByEntry={new Map(over?.results ?? [])}
      jumpsByEntry={new Map(over?.jumps ?? [])}
      outboxByEntry={new Map(over?.outbox ?? [])}
      resultsUnavailable={over?.resultsUnavailable ?? false}
      reachable
      actorSubject={over?.actorSubject === undefined ? "official-1" : over.actorSubject}
      onChanged={() => undefined}
    />,
  );
}

beforeEach(() => {
  vi.clearAllMocks();
});

/** The `.jump-extra` row under an athlete: what will not fit in a grid cell. */
function rowOverflow(row: Element): HTMLElement {
  let next = row.nextElementSibling;
  while (next && !next.classList.contains("jump-extra")) next = next.nextElementSibling;
  return next as HTMLElement;
}

describe("the grid", () => {
  it("draws one column per ladder height, plus Best, and tints the bar that is up", () => {
    const { container } = renderGrid();
    const [top, attempts] = [...container.querySelectorAll("thead tr")].map((tr) =>
      [...tr.querySelectorAll("th")].map((th) => th.textContent),
    );
    expect(top).toEqual(["Athlete", "1.25", "1.30", "1.35", "Best", "Actions"]);
    // Amendment 2026-09-25 (b): the height heads a group of one box per attempt.
    expect(attempts).toEqual(["1", "2", "3", "1", "2", "3", "1", "2", "3"]);
    // The LAST height is the bar (§4) — its whole group tinted, nothing else.
    expect(container.querySelectorAll(".jump-height.bar")).toHaveLength(1);
    expect(container.querySelector(".jump-height.bar")!.textContent).toBe("1.35");
    expect(container.querySelectorAll(".jump-attempt-head.bar")).toHaveLength(3);
    const row = container.querySelector(".jump-row")!;
    expect(row.querySelectorAll(".jump-box")).toHaveLength(9);
    expect(row.querySelectorAll(".jump-box.bar")).toHaveLength(3);
  });

  it("puts the athletes still in above the ones who are out, and mutes the out row", () => {
    const { container } = renderGrid({
      results: [
        [
          "en-1",
          result({ entryId: "en-1", status: "Nm", measuredValue: null, jumps: [{ height: 1.25, marks: "XXX" }] }),
        ],
        ["en-2", result({ id: "res-2", entryId: "en-2", jumps: [{ height: 1.25, marks: "O" }] })],
      ],
    });
    const names = [...container.querySelectorAll(".jump-name")].map((n) => n.textContent);
    expect(names).toEqual(["Blake Costa", "Finley Larsen"]);
    const out = container.querySelector(".jump-row.out")!;
    expect(out.textContent).toContain("Finley Larsen");
    expect(out.textContent).toContain("· out");
    // Best is the SERVER's — NM here, never derived from the card.
    expect(out.querySelector(".jump-best")!.textContent).toBe("NM");
  });

  it("makes a markable cell a 44px target and leaves a cleared one plain", () => {
    renderGrid({
      results: [["en-1", result({ entryId: "en-1", jumps: [{ height: 1.25, marks: "O" }] })]],
    });
    expect(
      screen.getByRole("button", { name: "Mark Finley Larsen at 1.35 m, attempt 1 of 3" }),
    ).toBeTruthy();
    // 1.25 is cleared: no mark can land there, so it is a correction target only.
    expect(screen.queryByRole("button", { name: /^Mark Finley Larsen at 1.25 m/ })).toBeNull();
    expect(
      screen.getByRole("button", { name: "Correct Finley Larsen at 1.25 m, attempt 1" }),
    ).toBeTruthy();
  });

  it("outlines only the cells where a mark can still land", () => {
    renderGrid({
      results: [["en-1", result({ entryId: "en-1", jumps: [{ height: 1.25, marks: "O" }] })]],
    });
    const named = (pattern: RegExp) =>
      screen.getAllByRole("button", { name: pattern }).map((b) => b.getAttribute("aria-label"));
    // Cleared at 1.25, so that one cell is a correction target and everything
    // above it — and every cell of the athlete with no card — still takes a mark.
    // One box per height takes the next attempt; the rest of the group waits blank.
    expect(named(/^Mark /)).toEqual([
      "Mark Finley Larsen at 1.30 m, attempt 1 of 3",
      "Mark Finley Larsen at 1.35 m, attempt 1 of 3",
      "Mark Blake Costa at 1.25 m, attempt 1 of 3",
      "Mark Blake Costa at 1.30 m, attempt 1 of 3",
      "Mark Blake Costa at 1.35 m, attempt 1 of 3",
    ]);
    expect(named(/^Correct /)).toEqual(["Correct Finley Larsen at 1.25 m, attempt 1"]);
    // The outline IS the empty cell's affordance now — there is no `_` glyph.
    const frame = (name: string) => screen.getByRole("button", { name }).className;
    expect(frame("Mark Finley Larsen at 1.30 m, attempt 1 of 3")).toContain("markable");
    expect(frame("Correct Finley Larsen at 1.25 m, attempt 1")).not.toContain("markable");
    expect(document.querySelector(".jump-next")).toBeNull();
  });

  it("leaves an eliminated athlete's empty cells un-expandable", () => {
    renderGrid({
      results: [
        [
          "en-1",
          result({
            entryId: "en-1",
            status: "Nm",
            measuredValue: null,
            jumps: [{ height: 1.25, marks: "XXX" }],
          }),
        ],
      ],
    });
    for (const height of ["1.30 m", "1.35 m"]) {
      expect(
        screen.queryByRole("button", { name: new RegExp(`^Mark Finley Larsen at ${height}`) }),
      ).toBeNull();
      expect(
        screen.queryByRole("button", { name: new RegExp(`^Correct Finley Larsen at ${height}`) }),
      ).toBeNull();
    }
  });

  /** ADR-0053 §6 amendment trap: a typed status closes every cell to a MARK. */
  it("leaves every height on a DNS row correctable, so the card can be given back", () => {
    renderGrid({
      results: [
        ["en-1", result({ entryId: "en-1", status: "Dns", measuredValue: null, jumps: null })],
      ],
    });
    for (const height of ["1.25 m", "1.30 m", "1.35 m"]) {
      const cell = `Finley Larsen at ${height}`;
      expect(screen.getByRole("button", { name: `Correct ${cell}, attempt 1` })).toBeTruthy();
      expect(screen.queryByRole("button", { name: new RegExp(`^Mark ${cell}`) })).toBeNull();
    }
    // The row stays muted and says what it is until the correction lands.
    const out = screen.getByText("Finley Larsen").closest("tr")!;
    expect(out.className).toContain("out");
    expect(out.textContent).toContain("· DNS");
  });

  it("closes every cell when the results read has failed, and says so", () => {
    renderGrid({
      results: [["en-1", result({ entryId: "en-1", jumps: [{ height: 1.25, marks: "O" }] })]],
      resultsUnavailable: true,
    });
    expect(screen.queryByRole("button", { name: /^Mark / })).toBeNull();
    expect(screen.queryByRole("button", { name: /^Correct / })).toBeNull();
    expect(screen.getByText(/results could not be read/)).toBeTruthy();
  });

  it("shows a parked mark, badged, without letting it move the card", () => {
    const { container } = renderGrid({
      results: [["en-1", result({ entryId: "en-1", jumps: [{ height: 1.25, marks: "XX" }] })]],
      jumps: [["en-1", new Map([[heightKey(1.25), [queuedItem("i-1", 1.25, "X", "rejected")]]])]],
    });
    const boxes = [...container.querySelector(".jump-row")!.querySelectorAll(".jump-box")];
    const marks = boxes
      .slice(0, 3)
      .map((box) => box.querySelector(".jump-marks-text")?.textContent);
    expect(marks).toEqual(["X", "X", "X"]);
    expect(boxes[2]!.querySelector(".sync-badge")!.textContent).toContain("rejected");
    // Counted, the third X would eliminate: the server refused it, so it does not.
    expect(container.querySelector(".jump-row.out")).toBeNull();
    expect(
      screen.getByRole("button", { name: "Mark Finley Larsen at 1.25 m, attempt 3 of 3" }),
    ).toBeTruthy();
  });

  it("opens scrolled to the bar, which a 390px phone would otherwise start left of", () => {
    const proto = HTMLDivElement.prototype;
    const scrolled: number[] = [];
    Object.defineProperty(proto, "scrollWidth", { configurable: true, get: () => 900 });
    Object.defineProperty(proto, "scrollLeft", {
      configurable: true,
      get: () => 0,
      set: (value: number) => scrolled.push(value),
    });
    try {
      renderGrid();
      expect(scrolled).toContain(900);
    } finally {
      Reflect.deleteProperty(proto, "scrollWidth");
      Reflect.deleteProperty(proto, "scrollLeft");
    }
  });

  it("appends a queued mark to the cell with its sync badge, card untouched", () => {
    const { container } = renderGrid({
      results: [["en-1", result({ entryId: "en-1", jumps: [{ height: 1.35, marks: "X" }] })]],
      jumps: [["en-1", new Map([[heightKey(1.35), [queuedItem("i-1", 1.35, "X")]]])]],
    });
    const [stored, pending, next] = [
      ...container.querySelector(".jump-row")!.querySelectorAll(".jump-box.bar"),
    ];
    expect(stored!.textContent).toBe("X");
    expect(stored!.querySelector(".jump-queued")).toBeNull();
    expect(pending!.querySelector(".jump-queued")!.textContent).toBe("X");
    expect(pending!.querySelector(".sync-badge")!.textContent).toContain("pending");
    expect(next!.querySelector("button")!.getAttribute("aria-label")).toBe(
      "Mark Finley Larsen at 1.35 m, attempt 3 of 3",
    );
  });

  it("says what to do when no bar has been set yet", () => {
    const { container } = renderGrid({ event: { heights: [] } });
    expect(screen.getByText("No opening height yet")).toBeTruthy();
    expect(container.querySelectorAll(".jump-name")).toHaveLength(2);
    expect(screen.getByRole("button", { name: "Set opening height 1.20 m" })).toBeTruthy();
  });

  it("names the bar in the card header, with no legend and no heat surface", () => {
    const { container } = renderGrid();
    expect(container.querySelector(".jump-bar-name")!.textContent).toBe("Bar at 1.35 m");
    expect(container.querySelector(".jump-key")).toBeNull();
    expect(container.textContent).not.toContain("Heat");
  });
});

describe("one box per attempt (amendment 2026-09-25 (b))", () => {
  const boxesAt = (container: HTMLElement, name: string, from: number, to: number) => {
    const row = [...container.querySelectorAll(".jump-row")].find((r) =>
      r.textContent!.includes(name),
    )!;
    return [...row.querySelectorAll(".jump-box")].slice(from, to);
  };

  it("draws two boxes a height at two attempts", () => {
    const { container } = renderGrid({ event: { attemptsPerHeight: 2 } });
    const attempts = container.querySelectorAll("thead tr")[1]!.querySelectorAll("th");
    expect([...attempts].map((th) => th.textContent)).toEqual(["1", "2", "1", "2", "1", "2"]);
    expect(container.querySelector(".jump-row")!.querySelectorAll(".jump-box")).toHaveLength(6);
  });

  it("makes only the next box at a height a button, and leaves the rest blank", () => {
    const { container } = renderGrid({
      results: [["en-1", result({ entryId: "en-1", jumps: [{ height: 1.25, marks: "X" }] })]],
    });
    const [first, second, third] = boxesAt(container, "Finley Larsen", 0, 3);
    expect(first!.querySelector("button")!.getAttribute("aria-label")).toBe(
      "Correct Finley Larsen at 1.25 m, attempt 1",
    );
    expect(second!.querySelector("button.markable")!.getAttribute("aria-label")).toBe(
      "Mark Finley Larsen at 1.25 m, attempt 2 of 3",
    );
    expect(third!.querySelector("button")).toBeNull();
    expect(third!.textContent).toBe("");
  });

  it("leaves the boxes after a clearance or a pass blank and inert", () => {
    const { container } = renderGrid({
      results: [
        [
          "en-1",
          result({
            entryId: "en-1",
            jumps: [
              { height: 1.25, marks: "XO" },
              { height: 1.3, marks: "-" },
            ],
          }),
        ],
      ],
    });
    const boxes = boxesAt(container, "Finley Larsen", 0, 9);
    expect(boxes.map((box) => box.textContent)).toEqual(["X", "O", "", "–", "", "", "", "", ""]);
    for (const i of [2, 4, 5]) expect(boxes[i]!.querySelector("button")).toBeNull();
    // Only the next contested height still takes a mark.
    expect(boxes[6]!.querySelector("button.markable")).toBeTruthy();
  });

  it("moves focus to the next box once a fail reaches the card", async () => {
    const stored = [
      ["en-1", result({ entryId: "en-1", jumps: [{ height: 1.25, marks: "O" }] })],
    ] as [string, ResultDto][];
    const grid = (jumps?: [string, Map<string, OutboxItem[]>][]) => (
      <HighJumpGrid
        carnivalId="c-1"
        event={event}
        rows={rows}
        resultsByEntry={new Map(stored)}
        jumpsByEntry={new Map(jumps ?? [])}
        outboxByEntry={new Map()}
        resultsUnavailable={false}
        reachable
        actorSubject="official-1"
        onChanged={() => undefined}
      />
    );
    const { rerender, client } = renderWithQuery(grid());
    const first = "Mark Finley Larsen at 1.30 m, attempt 1 of 3";
    fireEvent.click(screen.getByRole("button", { name: first }));
    fireEvent.click(screen.getByRole("button", { name: "X fail for Finley Larsen at 1.30 m" }));
    await waitFor(() => expect(document.querySelector(".jump-panel")).toBeNull());
    expect(document.activeElement!.getAttribute("aria-label")).toBe(first);

    rerender(
      <QueryWrapper client={client}>
        {grid([["en-1", new Map([[heightKey(1.3), [queuedItem("i-1", 1.3, "X")]]])]])}
      </QueryWrapper>,
    );
    expect(document.activeElement!.getAttribute("aria-label")).toBe(
      "Mark Finley Larsen at 1.30 m, attempt 2 of 3",
    );
  });

  it("keeps focus on the marked box when the mark ends the height", async () => {
    renderGrid({
      results: [["en-1", result({ entryId: "en-1", jumps: [{ height: 1.25, marks: "O" }] })]],
    });
    const first = "Mark Finley Larsen at 1.30 m, attempt 1 of 3";
    fireEvent.click(screen.getByRole("button", { name: first }));
    fireEvent.click(screen.getByRole("button", { name: "O clear for Finley Larsen at 1.30 m" }));
    await waitFor(() => expect(document.querySelector(".jump-panel")).toBeNull());
    expect(document.activeElement!.getAttribute("aria-label")).toBe(first);
  });
});

describe("a marked box edits its own mark (amendment 2026-09-25 (c))", () => {
  const card = (...jumps: [number, string][]): [string, ResultDto][] => [
    [
      "en-1",
      result({ entryId: "en-1", jumps: jumps.map(([height, marks]) => ({ height, marks })) }),
    ],
  ];
  const tap = (name: string) => fireEvent.click(screen.getByRole("button", { name }));
  const corrected = () => vi.mocked(submitCorrection).mock.calls[0]![0];

  beforeEach(() => {
    vi.mocked(submitCorrection).mockResolvedValue({ kind: "queued", item: { id: "x" } } as never);
  });

  it("names the box, shows its mark as current and saves a change at once", async () => {
    renderGrid({ results: card([1.25, "O"], [1.3, "XX"]) });
    tap("Correct Finley Larsen at 1.30 m, attempt 2");
    const panel = within(document.querySelector(".jump-panel") as HTMLElement);
    expect(panel.getByText("attempt 2 — now X")).toBeTruthy();
    const current = panel.getByRole("button", {
      name: "X fail for Finley Larsen at 1.30 m (current mark)",
    }) as HTMLButtonElement;
    expect(current.disabled).toBe(true);
    expect(screen.queryByRole("button", { name: "Undo last mark" })).toBeNull();
    expect(screen.queryByRole("button", { name: /Correct this height/ })).toBeNull();

    tap("O clear for Finley Larsen at 1.30 m");
    await waitFor(() => expect(submitCorrection).toHaveBeenCalledTimes(1));
    expect(corrected()).toMatchObject({
      resultId: "res-1",
      baseVersion: 2,
      value: null,
      status: null,
      jumps: [
        { height: 1.25, marks: "O" },
        { height: 1.3, marks: "XO" },
      ],
      reason: "Attempt 2 at 1.30 m changed X → O",
    });
  });

  it("names what else comes off and waits for Confirm, the typed reason appended", async () => {
    renderGrid({ results: card([1.25, "XXO"], [1.3, "X"]) });
    tap("Correct Finley Larsen at 1.25 m, attempt 1");
    fireEvent.change(screen.getByLabelText("Reason (optional)"), {
      target: { value: "Read back" },
    });
    tap("O clear for Finley Larsen at 1.25 m");
    expect(screen.getByText("This also removes attempts 2 and 3 at 1.25 m.")).toBeTruthy();
    expect(submitCorrection).not.toHaveBeenCalled();

    tap("Confirm");
    await waitFor(() => expect(submitCorrection).toHaveBeenCalledTimes(1));
    expect(corrected()).toMatchObject({
      jumps: [
        { height: 1.25, marks: "O" },
        { height: 1.3, marks: "X" },
      ],
      reason: "Attempt 1 at 1.25 m changed X → O — Read back",
    });
  });

  it("cancels a knock-on with nothing sent, and hands focus back to the box", () => {
    renderGrid({ results: card([1.25, "XXO"], [1.3, "X"]) });
    const box = screen.getByRole("button", { name: "Correct Finley Larsen at 1.25 m, attempt 1" });
    fireEvent.click(box);
    tap("– pass for Finley Larsen at 1.25 m");
    tap("Cancel");
    expect(document.querySelector(".jump-panel")).toBeNull();
    expect(submitCorrection).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(box);
  });

  it("removes a mark, closing the gap at its height", async () => {
    renderGrid({ results: card([1.25, "O"], [1.3, "XXO"]) });
    tap("Correct Finley Larsen at 1.30 m, attempt 2");
    tap("Remove this mark");
    await waitFor(() => expect(submitCorrection).toHaveBeenCalledTimes(1));
    expect(corrected()).toMatchObject({
      jumps: [
        { height: 1.25, marks: "O" },
        { height: 1.3, marks: "XO" },
      ],
      reason: "Attempt 2 at 1.30 m removed",
    });
  });

  it("takes the only mark off as an empty card, which the server now accepts", async () => {
    renderGrid({ results: card([1.25, "O"]) });
    tap("Correct Finley Larsen at 1.25 m, attempt 1");
    tap("Remove this mark");
    await waitFor(() => expect(submitCorrection).toHaveBeenCalledTimes(1));
    expect(corrected()).toMatchObject({ jumps: [], reason: "Attempt 1 at 1.25 m removed" });
  });

  it("reads a cleared card as not yet recorded, every contested height open again", () => {
    const { container } = renderGrid({
      results: [
        ["en-1", result({ entryId: "en-1", status: "Nm", measuredValue: null, jumps: null })],
      ],
    });
    const row = [...container.querySelectorAll(".jump-row")].find((r) =>
      r.textContent!.includes("Finley Larsen"),
    )!;
    expect(row.querySelector(".jump-best")!.textContent).toBe("—");
    expect(row.className).not.toContain("out");
    expect(
      [...row.querySelectorAll("button.markable")].map((b) => b.getAttribute("aria-label")),
    ).toEqual([
      "Mark Finley Larsen at 1.25 m, attempt 1 of 3",
      "Mark Finley Larsen at 1.30 m, attempt 1 of 3",
      "Mark Finley Larsen at 1.35 m, attempt 1 of 3",
    ]);
  });

  it("at two, names a height the change now puts out of reach", async () => {
    renderGrid({ event: { attemptsPerHeight: 2 }, results: card([1.25, "XO"], [1.3, "O"]) });
    tap("Correct Finley Larsen at 1.25 m, attempt 2");
    tap("X fail for Finley Larsen at 1.25 m");
    expect(screen.getByText("This also removes 1.30 m.")).toBeTruthy();
    tap("Confirm");
    await waitFor(() => expect(submitCorrection).toHaveBeenCalledTimes(1));
    expect(corrected().jumps).toEqual([{ height: 1.25, marks: "XX" }]);
  });

  it("changes a queued mark by cancelling it and its later ones, then requeuing", async () => {
    vi.mocked(cancelIfPending).mockResolvedValue({ id: "i" } as never);
    renderGrid({
      results: card([1.25, "O"]),
      jumps: [
        [
          "en-1",
          new Map([[heightKey(1.3), [queuedItem("i-1", 1.3, "X"), queuedItem("i-2", 1.3, "X")]]]),
        ],
      ],
    });
    tap("Correct Finley Larsen at 1.30 m, attempt 1");
    expect(screen.queryByLabelText("Reason (optional)")).toBeNull();
    tap("O clear for Finley Larsen at 1.30 m");
    expect(screen.getByText("This also removes attempt 2 at 1.30 m.")).toBeTruthy();
    tap("Confirm");
    await waitFor(() => expect(submitJumpViaOutbox).toHaveBeenCalledTimes(1));
    expect(vi.mocked(cancelIfPending).mock.calls.map(([id]) => id)).toEqual(["i-1", "i-2"]);
    expect(vi.mocked(submitJumpViaOutbox).mock.calls[0]![0]).toMatchObject({
      height: 1.3,
      mark: "O",
    });
    expect(submitCorrection).not.toHaveBeenCalled();
  });

  it("removes a queued mark by cancelling it alone", async () => {
    vi.mocked(cancelIfPending).mockResolvedValue({ id: "i-1" } as never);
    renderGrid({ jumps: [["en-1", new Map([[heightKey(1.25), [queuedItem("i-1", 1.25, "X")]]])]] });
    tap("Correct Finley Larsen at 1.25 m, attempt 1");
    tap("Remove this mark");
    await waitFor(() => expect(cancelIfPending).toHaveBeenCalledWith("i-1"));
    expect(submitJumpViaOutbox).not.toHaveBeenCalled();
  });

  it("says so when the worker sent the queued mark first", async () => {
    vi.mocked(cancelIfPending).mockResolvedValue(null);
    renderGrid({ jumps: [["en-1", new Map([[heightKey(1.25), [queuedItem("i-1", 1.25, "X")]]])]] });
    tap("Correct Finley Larsen at 1.25 m, attempt 1");
    tap("O clear for Finley Larsen at 1.25 m");
    expect(await screen.findByText(/already reached the server/)).toBeTruthy();
    expect(submitJumpViaOutbox).not.toHaveBeenCalled();
  });

  it("waits for this device's queued marks before a server mark can change", () => {
    renderGrid({
      results: card([1.25, "O"]),
      jumps: [["en-1", new Map([[heightKey(1.3), [queuedItem("i-1", 1.3, "X")]]])]],
    });
    tap("Correct Finley Larsen at 1.25 m, attempt 1");
    expect(
      screen.getByText("Waiting for 1 mark to sync before this one can change."),
    ).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^X fail for/ })).toBeNull();
    expect(screen.queryByRole("button", { name: "Remove this mark" })).toBeNull();
  });

  it("offers an empty box its marks and nothing else", () => {
    renderGrid({ results: card([1.25, "O"]) });
    tap("Mark Finley Larsen at 1.30 m, attempt 1 of 3");
    expect(
      screen.getByRole("button", { name: "O clear for Finley Larsen at 1.30 m" }),
    ).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Remove this mark" })).toBeNull();
    expect(screen.queryByLabelText("Reason (optional)")).toBeNull();
    expect(screen.queryByRole("button", { name: "Undo last mark" })).toBeNull();
  });

  it("gives a DNS row a card back with the one mark tapped, the status going with it", async () => {
    renderGrid({
      results: [
        ["en-1", result({ entryId: "en-1", status: "Dns", measuredValue: null, jumps: null })],
      ],
    });
    tap("Correct Finley Larsen at 1.35 m, attempt 1");
    expect(screen.getByText(/the typed status goes with it/)).toBeTruthy();
    tap("X fail for Finley Larsen at 1.35 m");
    await waitFor(() => expect(submitCorrection).toHaveBeenCalledTimes(1));
    expect(corrected()).toMatchObject({
      status: null,
      jumps: [{ height: 1.35, marks: "X" }],
      reason: "Card restored with X at 1.35 m",
    });
  });
});

describe("a DNS taken from the grid's own row menu", () => {
  it("closes the row while the capture is still queued, and badges it", () => {
    const { container } = renderGrid({ outbox: [["en-1", submitItem("queued")]] });
    const row = screen.getByText("Finley Larsen").closest("tr")!;
    expect(row.className).toContain("out");
    expect(row.textContent).toContain("· DNS");
    expect(row.querySelector(".sync-badge")!.textContent).toContain("pending");
    // No cell may take a mark under a status the server is about to be told.
    expect(screen.queryByRole("button", { name: /^Mark Finley Larsen/ })).toBeNull();
    // The still-in athlete sorts above the one on their way out.
    expect([...container.querySelectorAll(".jump-name")].map((n) => n.textContent)).toEqual([
      "Blake Costa",
      "Finley Larsen",
    ]);
  });

  it("withdraws the row menu's statuses while one is unanswered, so no second key is minted", () => {
    renderGrid({ outbox: [["en-1", submitItem("queued")]] });
    expect(screen.queryByRole("button", { name: "Actions for Finley Larsen" })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Actions for Blake Costa" }));
    expect(screen.getByRole("menuitem", { name: "DNS" })).toBeTruthy();
  });

  it("offers the parked capture back to the queue from the row it was taken on", async () => {
    vi.mocked(requeue).mockResolvedValue({ id: "sub-1" } as never);
    renderGrid({ outbox: [["en-1", submitItem("conflict")]] });
    const row = screen.getByText("Finley Larsen").closest("tr")!;
    expect(row.querySelector(".sync-badge")!.textContent).toContain("conflict");
    // Anything wider than a cell is the row's own full-width row beneath it.
    const extra = rowOverflow(row);
    expect(extra.textContent).toContain("Sync conflict for Finley Larsen");
    fireEvent.click(within(extra).getByRole("button", { name: "Send again" }));
    await waitFor(() => expect(requeue).toHaveBeenCalledWith("sub-1"));
  });

  it("offers no send for a refusal this row cannot clear, or with nobody signed in", () => {
    const deadEnd = {
      ...submitItem("conflict"),
      serverResponse: { error: "RESULT_FINALISED" },
    } as OutboxItem;
    const { unmount } = renderGrid({ outbox: [["en-1", deadEnd]] });
    expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
    unmount();
    renderGrid({ outbox: [["en-1", submitItem("conflict")]], actorSubject: null });
    expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
  });

  it("names the outbox for a rejected one, which can never go back in the queue", () => {
    renderGrid({ outbox: [["en-1", submitItem("rejected")]] });
    const extra = rowOverflow(screen.getByText("Finley Larsen").closest("tr")!);
    expect(extra.textContent).toContain("let go from the outbox screen");
    expect(within(extra).queryByRole("button", { name: "Send again" })).toBeNull();
  });

  it("leaves a queued CARD correction alone — it is not a typed status", () => {
    const card = {
      ...submitItem("queued", { kind: "result.correct" }),
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-1",
        entryId: "en-1",
        value: null,
        status: null,
        jumps: [{ height: 1.25, marks: "XO" }],
        reason: "Read back",
      },
    } as OutboxItem;
    renderGrid({ outbox: [["en-1", card]] });
    const row = screen.getByText("Finley Larsen").closest("tr")!;
    expect(row.className).not.toContain("out");
    expect(
      screen.getByRole("button", { name: "Mark Finley Larsen at 1.35 m, attempt 1 of 3" }),
    ).toBeTruthy();
  });
});

describe("the cell, expanded in place", () => {
  const BAR_BOX = "Mark Finley Larsen at 1.35 m, attempt 3 of 3";

  function openBar() {
    renderGrid({
      results: [
        [
          "en-1",
          result({
            entryId: "en-1",
            jumps: [
              { height: 1.25, marks: "O" },
              { height: 1.3, marks: "O" },
              { height: 1.35, marks: "XX" },
            ],
          }),
        ],
      ],
    });
    fireEvent.click(screen.getByRole("button", { name: BAR_BOX }));
  }

  it("expands under the athlete's row, naming the bar and the attempt", () => {
    openBar();
    // No dialog at any width — the panel is a row of the grid (amendment 2026-09-21).
    expect(screen.queryByRole("dialog")).toBeNull();
    const cell = screen.getByRole("button", { name: BAR_BOX });
    expect(cell.getAttribute("aria-expanded")).toBe("true");
    const panelRow = document.querySelector(".jump-panel-row")!;
    expect(panelRow.previousElementSibling).toBe(cell.closest("tr"));
    const panel = within(panelRow as HTMLElement);
    expect(panel.getByText("Finley Larsen")).toBeTruthy();
    expect(panel.getByText("1.35 m")).toBeTruthy();
    expect(panel.getByText("attempt 3 of 3")).toBeTruthy();
  });

  it("moves the expansion to the next cell tapped, never opening two", () => {
    openBar();
    fireEvent.click(
      screen.getByRole("button", { name: "Mark Blake Costa at 1.30 m, attempt 1 of 3" }),
    );
    expect(document.querySelectorAll(".jump-panel")).toHaveLength(1);
    expect(document.querySelector(".jump-panel")!.textContent).toContain("Blake Costa");
    expect(
      screen.getByRole("button", { name: BAR_BOX })
        .getAttribute("aria-expanded"),
    ).toBe("false");
  });

  it("collapses on a second tap of the same cell, and on Escape", () => {
    openBar();
    const cell = screen.getByRole("button", { name: BAR_BOX });
    fireEvent.click(cell);
    expect(document.querySelector(".jump-panel")).toBeNull();
    fireEvent.click(cell);
    expect(document.querySelector(".jump-panel")).toBeTruthy();
    fireEvent.keyDown(document.querySelector(".jump-panel")!, { key: "Escape" });
    expect(document.querySelector(".jump-panel")).toBeNull();
    // Escape hands the grid back to the cell it was taken from.
    expect(document.activeElement).toBe(cell);
  });

  it("warns that the next fail ends the athlete's jumping", () => {
    openBar();
    expect(screen.getByText(/A third fail here puts Finley out/)).toBeTruthy();
  });

  it.each([
    ["O clear for Finley Larsen at 1.35 m", "O"],
    ["X fail for Finley Larsen at 1.35 m", "X"],
    ["– pass for Finley Larsen at 1.35 m", "-"],
  ])("%s is exactly one result.jump", async (name, mark) => {
    openBar();
    fireEvent.click(screen.getByRole("button", { name }));
    await waitFor(() => expect(submitJumpViaOutbox).toHaveBeenCalledTimes(1));
    expect(vi.mocked(submitJumpViaOutbox).mock.calls[0]![0]).toMatchObject({
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      height: 1.35,
      mark,
      measureType: "Height",
      actorSubject: "official-1",
    });
    // The panel collapses on the tap and the cell keeps the focus, so the next
    // mark is one keystroke away — the gap the sheet left when a marked cell
    // stopped being a button.
    await waitFor(() => expect(document.querySelector(".jump-panel")).toBeNull());
    expect(document.activeElement).toBe(
      screen.getByRole("button", { name: BAR_BOX }),
    );
  });

});

describe("attempts per height (amendment 2026-09-25)", () => {
  const perHeight = () =>
    screen.queryByLabelText("Attempts per height") as HTMLSelectElement | null;

  it("switches between 3 and 2 before the first mark lands, and calls the endpoint", async () => {
    vi.mocked(setAttemptsPerHeight).mockResolvedValue({ ...event, attemptsPerHeight: 2 });
    renderGrid();
    expect(perHeight()!.value).toBe("3");
    fireEvent.change(perHeight()!, { target: { value: "2" } });
    await waitFor(() => expect(setAttemptsPerHeight).toHaveBeenCalledWith("c-1", "ev-1", 2));
  });

  it("reads as text once a mark is on the server, or queued on this device", () => {
    const { unmount } = renderGrid({
      results: [["en-1", result({ entryId: "en-1", jumps: [{ height: 1.25, marks: "X" }] })]],
    });
    expect(perHeight()).toBeNull();
    expect(screen.getByText("3 attempts per height")).toBeTruthy();
    unmount();

    renderGrid({
      event: { attemptsPerHeight: 2 },
      jumps: [["en-2", new Map([[heightKey(1.25), [queuedItem("i-1", 1.25, "X")]]])]],
    });
    expect(perHeight()).toBeNull();
    expect(screen.getByText("2 attempts per height")).toBeTruthy();
  });

  it("is text with nobody signed in, who could not raise the bar either", () => {
    renderGrid({ actorSubject: null });
    expect(perHeight()).toBeNull();
    expect(screen.getByText("3 attempts per height")).toBeTruthy();
  });

  it("counts attempts out of two and warns at the first fail of a height", () => {
    renderGrid({
      event: { attemptsPerHeight: 2 },
      results: [
        [
          "en-1",
          result({
            entryId: "en-1",
            jumps: [
              { height: 1.25, marks: "O" },
              { height: 1.3, marks: "X" },
            ],
          }),
        ],
      ],
    });
    fireEvent.click(
      screen.getByRole("button", { name: "Mark Finley Larsen at 1.30 m, attempt 2 of 2" }),
    );
    const panel = within(document.querySelector(".jump-panel") as HTMLElement);
    expect(panel.getByText("attempt 2 of 2")).toBeTruthy();
    expect(panel.getByText("A second fail at this height puts Finley out.")).toBeTruthy();
  });

  it("puts an athlete out on XX at one height, where three would leave them jumping", () => {
    const card = [{ height: 1.25, marks: "O" }, { height: 1.3, marks: "XX" }];
    const out = [["en-1", result({ entryId: "en-1", jumps: card })]] as [string, ResultDto][];
    const { container, unmount } = renderGrid({ event: { attemptsPerHeight: 2 }, results: out });
    expect(container.querySelector(".jump-row.out")!.textContent).toContain("Finley Larsen");
    expect(screen.queryByRole("button", { name: /^Mark Finley Larsen/ })).toBeNull();
    unmount();

    renderGrid({ results: out });
    expect(
      screen.getByRole("button", { name: "Mark Finley Larsen at 1.30 m, attempt 3 of 3" }),
    ).toBeTruthy();
  });

});

describe("raising the bar", () => {
  it("pre-fills the ladder's next step and calls the endpoint", async () => {
    vi.mocked(raiseBar).mockResolvedValue(event as never);
    renderGrid();
    // The trigger becomes the box and its Raise button, in the card header.
    fireEvent.click(screen.getByRole("button", { name: "Raise bar to 1.40 m" }));
    expect(screen.getByLabelText("New height")).toBeTruthy();
    expect(document.querySelector(".jump-card-head .jump-raise-open")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Raise bar to 1.40 m" }));
    await waitFor(() => expect(raiseBar).toHaveBeenCalledWith("c-1", "ev-1", 1.4));
  });

  /** The amendment: the suggestions are optional, the raise control is not. */
  it("still raises with no suggestions — an empty box and an example height", () => {
    vi.mocked(raiseBar).mockResolvedValue(event as never);
    renderGrid({ event: { ladder: null } });
    const trigger = screen.getByRole("button", { name: "Raise the bar" }) as HTMLButtonElement;
    expect(trigger.disabled).toBe(false);
    fireEvent.click(trigger);

    const field = screen.getByLabelText("New height") as HTMLInputElement;
    expect(field.value).toBe("");
    expect(field.placeholder).toBe("e.g. 1.20");
    expect(screen.queryByText(/Leave it blank/)).toBeNull();
    const save = document.querySelector(".jump-raise .primary") as HTMLButtonElement;
    expect(save.disabled).toBe(true);

    fireEvent.change(field, { target: { value: "140" } });
    fireEvent.click(document.querySelector(".jump-raise .primary") as HTMLButtonElement);
    return waitFor(() => expect(raiseBar).toHaveBeenCalledWith("c-1", "ev-1", 1.4));
  });

  it("names the opening height as one to set, with no suggestion to offer", () => {
    renderGrid({ event: { ladder: null, heights: [] } });
    expect(
      (screen.getByRole("button", { name: "Set opening height" }) as HTMLButtonElement).disabled,
    ).toBe(false);
  });

  it("asks for a height, with an example a bar could actually be", () => {
    renderGrid();
    fireEvent.click(screen.getByRole("button", { name: "Raise bar to 1.40 m" }));
    const field = screen.getByLabelText("New height") as HTMLInputElement;
    expect(field.placeholder).toBe("height");
    // Not the long-jump deck's "432 reads 4.32 m": nobody clears 4.32 m.
    expect(screen.getByText("135 reads 1.35 m")).toBeTruthy();
  });

  it("refuses a height that is not above the bar, in the form", () => {
    renderGrid();
    fireEvent.click(screen.getByRole("button", { name: "Raise bar to 1.40 m" }));
    fireEvent.change(screen.getByLabelText("New height"), { target: { value: "130" } });
    expect(screen.getByText(/1.30 m is not above 1.35 m/)).toBeTruthy();
    expect(raiseBar).not.toHaveBeenCalled();
  });

  it("takes the opening height on a Scheduled division, which the server allows", () => {
    // ADR-0056 §2: the bar goes up before the division opens; RequireEventCapturable
    // refuses only a Closed or Finalised one.
    renderGrid({ event: { status: "Scheduled", heights: [] } });
    expect(
      (screen.getByRole("button", { name: "Set opening height 1.20 m" }) as HTMLButtonElement)
        .disabled,
    ).toBe(false);
  });

  it("is dead when the results read has failed — the ladder on screen is not the ladder", () => {
    renderGrid({ resultsUnavailable: true });
    expect(
      (screen.getByRole("button", { name: "Raise bar to 1.40 m" }) as HTMLButtonElement).disabled,
    ).toBe(true);
  });

  it("holds the refusal back until the height is complete, but never offers a live dead end", () => {
    // The metre mask right-fills: "1" reads 0.01 and "14" reads 0.14 on the way
    // to 1.40. Neither is finished typing, so no alert fires — and neither is
    // above the bar, so the button stays dead rather than doing nothing on a tap.
    renderGrid();
    fireEvent.click(screen.getByRole("button", { name: "Raise bar to 1.40 m" }));
    const field = screen.getByLabelText("New height");
    const save = () => document.querySelector(".jump-raise .primary") as HTMLButtonElement;
    for (const keys of ["1", "14"]) {
      fireEvent.change(field, { target: { value: keys } });
      expect(screen.queryByText(/is not above/)).toBeNull();
      expect(save().disabled).toBe(true);
    }
    fireEvent.change(field, { target: { value: "140" } });
    expect(screen.queryByText(/is not above/)).toBeNull();
    expect(save().disabled).toBe(false);
    // A complete height below the bar says so as well as refusing it.
    fireEvent.change(field, { target: { value: "130" } });
    expect(screen.getByText(/1.30 m is not above 1.35 m/)).toBeTruthy();
    expect(save().disabled).toBe(true);
  });

  it("is dead with the server out of reach — the bar is shared state (K7)", () => {
    const { container } = renderWithQuery(
      <HighJumpGrid
        carnivalId="c-1"
        event={event}
        rows={rows}
        resultsByEntry={new Map()}
        jumpsByEntry={new Map()}
        outboxByEntry={new Map()}
        resultsUnavailable={false}
        reachable={false}
        actorSubject="official-1"
        onChanged={() => undefined}
      />,
    );
    expect(
      (screen.getByRole("button", { name: "Raise bar to 1.40 m" }) as HTMLButtonElement).disabled,
    ).toBe(true);
    expect(container.textContent).toContain("the bar can't be raised from here yet");
  });
});

describe("a typed status on a card the server has only just minted", () => {
  /** The first mark MINTS the result (ADR-0056 §2), so its ack is the base. */
  it("corrects the result the accepted mark named, rather than submitting a second", async () => {
    vi.mocked(submitCorrection).mockResolvedValue({ kind: "queued", item: { id: "x" } } as never);
    renderGrid({
      jumps: [
        [
          "en-1",
          new Map([
            [
              heightKey(1.25),
              [
                queuedItem("i-1", 1.25, "O", "accepted", { id: "res-9", currentRevision: 4 }),
              ],
            ],
          ]),
        ],
      ],
    });
    fireEvent.click(screen.getByRole("button", { name: "Actions for Finley Larsen" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "DNS" }));
    await waitFor(() => expect(submitCorrection).toHaveBeenCalledTimes(1));
    expect(vi.mocked(submitCorrection).mock.calls[0]![0]).toMatchObject({
      resultId: "res-9",
      baseVersion: 4,
      status: "Dns",
    });
    expect(submitResultViaOutbox).not.toHaveBeenCalled();
  });
});
