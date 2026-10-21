// @vitest-environment jsdom
import { createRef } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { renderWithQuery } from "../../test/queryWrapper";
import type { CarnivalEvent, Entry, ResultDto, SchoolRecord } from "../../api/types";
import type { ResultSubmitPayload } from "../../api/outboxTransport";
import type { OutboxItem } from "../../db/db";
import { EntryRow } from "./EntryRow";

/**
 * In-place recovery on the deck row (ADR-0025 §5). What is pinned here is the
 * part a screen test cannot see cheaply: that the conflict badge is the only
 * new affordance, that both recoveries run off the row's existing controls,
 * and that nothing on the row can abandon a capture.
 */

vi.mock("../../api/syncSetup", () => ({ syncWorker: { syncNow: vi.fn() } }));
// Partial: the status predicates (`needsRebase`) are logic under test here,
// not collaborators to stub.
vi.mock("../../sync/outbox", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../../sync/outbox")>()),
  cancelIfPending: vi.fn(),
  requeue: vi.fn(),
  editAndRequeue: vi.fn(),
}));
vi.mock("../../results/submitResult", () => ({ submitResultViaOutbox: vi.fn() }));
vi.mock("../../results/correctResult", () => ({ submitCorrection: vi.fn() }));
vi.mock("../../results/submitAttempt", () => ({ submitAttemptViaOutbox: vi.fn() }));

import { cancelIfPending, editAndRequeue, InvalidTransitionError, requeue } from "../../sync/outbox";
import { submitCorrection } from "../../results/correctResult";
import { submitAttemptViaOutbox } from "../../results/submitAttempt";

const event: CarnivalEvent = {
  id: "ev-1",
  carnivalId: "c-1",
  eventDefinitionId: "def-1",
  definitionCode: "SWIM-50-FS",
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
  entryCount: 1,
  complete: false,
  officials: [],
};

const entry: Entry = {
  id: "en-1",
  carnivalEventId: "ev-1",
  studentId: "st-1",
  raceId: null,
  heat: 1,
  lane: 3,
  displayName: "Sam Swimmer",
  yearLevel: 7,
  houseCode: "BANK",
  ageGroup: "U13",
  overrideApplied: false,
  isTeam: false,
  teamHouseCode: null,
  legs: [],
};

const payload: ResultSubmitPayload = {
  carnivalId: "c-1",
  carnivalEventId: "ev-1",
  entryId: "en-1",
  value: "34.56",
  status: null,
  display: { entryDisplayName: "Sam Swimmer" },
};

/** The parked capture the whole ADR is about: refused, payload intact. */
const conflicted: OutboxItem = {
  id: "01930000-0000-7000-8000-000000000001",
  kind: "result.submit",
  payload,
  actorSubject: "official-1",
  capturedAt: "2026-08-20T01:00:00Z",
  status: "conflict",
  attempts: 2,
  lastError: "409",
  serverResponse: { error: "REQUEST_INVALID", detail: "The measured value is not a time." },
};

/** The same capture parked as a CORRECTION that lost the concurrency race. */
const staleCorrection: OutboxItem = {
  ...conflicted,
  id: "01930000-0000-7000-8000-000000000002",
  kind: "result.correct",
  payload: { ...payload, resultId: "res-1", reason: "Corrected at the deck" },
  baseVersion: 2,
  serverResponse: { error: "CONCURRENCY_CONFLICT", detail: "Revision moved on." },
};

/** The offline retype of a ranked time as a non-finish, still on this device. */
const queuedDns: OutboxItem = {
  ...staleCorrection,
  id: "01930000-0000-7000-8000-000000000003",
  status: "queued",
  serverResponse: undefined,
  payload: { ...payload, value: null, status: "Dns", resultId: "res-1", reason: "Retyped at the deck" },
};

/** The recorded result the correction is amending, now on revision 4. */
const recorded: ResultDto = {
  id: "res-1",
  entryId: "en-1",
  status: "Ok",
  measuredValue: 34.56,
  jumps: null,
  lifecycleState: "Verified",
  currentRevision: 4,
  recordedAtUtc: "2026-08-20T01:00:00Z",
  recordedBySubject: "official-1",
  place: null,
};

function renderRow(
  outboxItem: OutboxItem | undefined,
  serverResult?: ResultDto,
  standingRecord?: SchoolRecord,
  tier: CarnivalEvent["tier"] = "Competitive",
  /** The row's own field: its entry, and where it sits in the division's heats. */
  field: {
    entry?: Entry;
    unplaced?: boolean;
    raceless?: boolean;
    lanePending?: boolean;
    /** The event's measure — a field event, when it is not a time. */
    measureType?: CarnivalEvent["measureType"];
    /** ADR-0053: a series of this many attempts, rather than one typed mark. */
    attemptCount?: number | null;
    attemptItems?: ReadonlyMap<number, OutboxItem>;
    /** The division has races, so the row is offered the lane control. */
    laneControl?: boolean;
    inRace?: boolean;
    lane?: number | null;
  } = {},
) {
  return renderWithQuery(
    <ul>
      <EntryRow
        carnivalId="c-1"
        event={{
          ...event,
          tier,
          measureType: field.measureType ?? "Time",
          attemptCount: field.attemptCount ?? null,
        }}
        entry={field.entry ?? entry}
        unplaced={field.unplaced ?? false}
        raceless={field.raceless ?? false}
        lanePending={field.lanePending ?? false}
        {...(field.laneControl ? { laneControl: () => <span data-testid="lane-control" /> } : {})}
        inRace={field.inRace ?? false}
        {...(field.lane !== undefined ? { lane: field.lane } : {})}
        heatDivider={null}
        createItem={undefined}
        onDismissCreate={undefined}
        serverResult={serverResult}
        outboxItem={outboxItem}
        {...(field.attemptItems ? { attemptItems: field.attemptItems } : {})}
        resultsUnavailable={false}
        standingRecord={standingRecord}
        actorSubject="official-1"
        inputRef={createRef<HTMLInputElement>()}
        onSaved={vi.fn()}
      />
    </ul>,
  );
}

const badge = () => screen.getByRole("button", { name: /recover this capture/ });

beforeEach(() => {
  cleanup();
  vi.mocked(requeue).mockReset().mockResolvedValue(conflicted);
  vi.mocked(editAndRequeue).mockReset().mockResolvedValue(conflicted);
  vi.mocked(submitCorrection).mockReset();
  vi.mocked(submitAttemptViaOutbox).mockReset();
});

describe("EntryRow recovery", () => {
  it("makes the conflict badge the recovery affordance, and adds no other chrome", () => {
    renderRow(conflicted);
    // Collapsed: the badge and the row's own Edit, nothing else.
    expect(badge().getAttribute("aria-expanded")).toBe("false");
    expect(screen.getByRole("button", { name: "Edit" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Send again/ })).toBeNull();

    fireEvent.click(badge());
    expect(badge().getAttribute("aria-expanded")).toBe("true");
    const send = screen.getByRole("button", { name: "Send again" });
    expect(document.activeElement).toBe(send);
    // The disclosure lands in the conflict alert the row already showed.
    expect(send.closest('[role="alert"]')?.textContent).toContain("Sync conflict for Sam Swimmer");
  });

  it("offers no recovery while the outbox read has not landed", () => {
    // An unresolved read yields no item, so the row cannot offer to act on one.
    renderRow(undefined);
    expect(screen.queryByRole("button", { name: /recover this capture/ })).toBeNull();
    expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
  });

  it("requeues the parked capture from the row, under its own id", async () => {
    renderRow(conflicted);
    fireEvent.click(badge());
    fireEvent.click(screen.getByRole("button", { name: "Send again" }));

    await waitFor(() => expect(requeue).toHaveBeenCalledWith(conflicted.id));
    // The disclosure closes itself: the capture is no longer parked.
    await waitFor(() =>
      expect(screen.queryByRole("button", { name: "Send again" })).toBeNull(),
    );
  });

  it("says so, rather than throwing, when the item was resolved somewhere else", async () => {
    vi.mocked(requeue).mockRejectedValue(new Error("illegal status transition"));
    renderRow(conflicted);
    fireEvent.click(badge());
    fireEvent.click(screen.getByRole("button", { name: "Send again" }));

    await waitFor(() => expect(screen.getByText(/already been dealt with/)).toBeTruthy());
  });

  it("retypes a parked capture through the row's own time editor and re-sends it", async () => {
    renderRow(conflicted);
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));

    // The SAME field a fresh capture uses, prefilled from what parked.
    const field = screen.getByLabelText(/^Time for Sam Swimmer/) as HTMLInputElement;
    expect(field.value).toBe("34.56");
    fireEvent.change(field, { target: { value: "35.12" } });
    expect(field.value).toBe("35.12");

    fireEvent.click(screen.getByRole("button", { name: "Save and send" }));
    await waitFor(() =>
      expect(editAndRequeue).toHaveBeenCalledWith(
        conflicted.id,
        { ...payload, value: "35.12", status: null },
        undefined,
      ),
    );
    // No undo window afterwards: undo would be a one-tap abandon (ADR-0025 §5).
    await waitFor(() => expect(screen.queryByRole("button", { name: /^Undo/ })).toBeNull());
  });

  it("retypes the PARKED item, not a fresh correction, when the server has a result too", async () => {
    // The U3 rough edge ADR-0025 §1 exists to fix: a retype under a new UUIDv7
    // leaves the parked item conflicted forever while the row goes green.
    renderRow(conflicted, recorded);
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    expect(screen.getByRole("button", { name: "Save and send" })).toBeTruthy();

    fireEvent.change(screen.getByLabelText(/^Time for Sam Swimmer/), {
      target: { value: "35.12" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save and send" }));

    await waitFor(() =>
      expect(editAndRequeue).toHaveBeenCalledWith(
        conflicted.id,
        { ...payload, value: "35.12", status: null },
        { baseVersion: 4 },
      ),
    );
    expect(submitCorrection).not.toHaveBeenCalled();
  });

  it("re-bases a correction that lost the concurrency race instead of re-sending it", async () => {
    // Requeueing unchanged would re-send revision 2 and park byte-identically,
    // for ever — so the row offers the retype, which carries revision 4.
    renderRow(staleCorrection, recorded);
    expect(screen.queryByRole("button", { name: /recover this capture/ })).toBeNull();
    expect(screen.getByText(/refused the same way/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    fireEvent.click(screen.getByRole("button", { name: "Save and send" }));
    await waitFor(() =>
      expect(editAndRequeue).toHaveBeenCalledWith(
        staleCorrection.id,
        expect.objectContaining({ resultId: "res-1" }),
        { baseVersion: 4 },
      ),
    );
  });

  it("offers nothing at all when the revision it needs is unknowable here", async () => {
    // The offline case this ADR exists for: no results read, so no revision to
    // re-base on. Both controls would be guaranteed to re-park.
    renderRow(staleCorrection);
    expect(screen.queryByRole("button", { name: /recover this capture/ })).toBeNull();
    expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
    expect(screen.getByText(/let go from the outbox screen/)).toBeTruthy();
  });

  it("offers no recovery for a refusal that can never be cleared from here", async () => {
    // RESULT_FINALISED is a conflict, so it parks and can loop unboundedly —
    // but nothing typed on this row will ever land, so the row says where it can.
    const finalised: OutboxItem = {
      ...conflicted,
      serverResponse: { error: "RESULT_FINALISED", detail: "Carnival finalised." },
    };
    renderRow(finalised, { ...recorded, lifecycleState: "Final" });
    expect(screen.queryByRole("button", { name: /recover this capture/ })).toBeNull();
    expect(screen.queryByRole("button", { name: "Edit" })).toBeNull();
    expect(screen.getByText(/let go from the outbox screen/)).toBeTruthy();
  });

  it("puts a concurrent resolution in words, not in state-machine terms", async () => {
    vi.mocked(editAndRequeue).mockRejectedValue(
      new InvalidTransitionError(conflicted.id, "discarded", "queued"),
    );
    renderRow(conflicted);
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    fireEvent.click(screen.getByRole("button", { name: "Save and send" }));

    await waitFor(() => expect(screen.getByText(/already been dealt with/)).toBeTruthy());
    expect(screen.queryByText(/illegal status transition/)).toBeNull();
  });

  it("says so when the recall loses the race with the worker", async () => {
    // The capture flipped to `syncing` under the tap, so there is nothing to
    // retype and no revision to correct against yet. Edit used to do nothing
    // at all; it now says what Undo says in the same place.
    const queuedCapture: OutboxItem = {
      ...conflicted,
      id: "01930000-0000-7000-8000-000000000004",
      status: "queued",
      serverResponse: undefined,
    };
    vi.mocked(cancelIfPending).mockResolvedValue(null);
    renderRow(queuedCapture);
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));

    await waitFor(() =>
      expect(screen.getByText(/Already syncing — it can be corrected/)).toBeTruthy(),
    );
  });

  it("offers no discard anywhere on the row, open or closed", () => {
    const { container } = renderRow(conflicted);
    expect(screen.queryByRole("button", { name: /discard|delete|dismiss/i })).toBeNull();
    fireEvent.click(badge());
    expect(screen.queryByRole("button", { name: /discard|delete|dismiss/i })).toBeNull();
    expect(container.textContent).not.toMatch(/discard/i);
  });
});

/**
 * The record badge (ADR-0028 §5). It rides the row's existing badge cluster and
 * costs one read, so the only thing to pin here is that it appears on exactly
 * the swims that beat the book — and never while the book is empty.
 */
describe("EntryRow record badge", () => {
  const standing: SchoolRecord = {
    id: "rec-1",
    eventDefinitionId: "def-1",
    eventCode: "SWIM-50-FS",
    eventName: "Freestyle 50m",
    ageGroup: "13",
    category: "F",
    division: "13 Girls",
    value: 34.56,
    formattedValue: "34.56",
    higherBetter: false,
    holderDisplayName: "Prior Holder",
    setOn: "2027-03-06",
    carnivalName: "Preston Swimming",
    voided: false,
    voidedAtUtc: null,
    voidedBySubject: null,
    voidReason: null,
  };

  it("badges a swim that beats the standing record, and names what it beat", () => {
    renderRow(undefined, { ...recorded, measuredValue: 34.0 }, standing);
    expect(screen.getByTitle("Beats the school record: 34.56, Prior Holder")).toBeTruthy();
  });

  it("does not badge one that merely equals it", () => {
    renderRow(undefined, recorded, standing);
    expect(screen.queryByTitle(/Beats the school record/)).toBeNull();
  });

  it("is inert with no record standing, which is the whole of the first carnival", () => {
    renderRow(undefined, { ...recorded, measuredValue: 1.0 });
    expect(screen.queryByTitle(/Beats the school record/)).toBeNull();
  });

  it("does not badge a Social swim: a standing record is a Competitive one", () => {
    renderRow(undefined, { ...recorded, measuredValue: 34.0 }, standing, "Social");
    expect(screen.queryByTitle(/Beats the school record/)).toBeNull();
  });
});

/**
 * The placing badge (ADR-0036). The gates that decide whether a row places at
 * all are server-side in the shared ranking, so what is pinned here is the
 * rendering and the one client-side rule: the badge reads the SERVER's result
 * and nothing else.
 */
describe("EntryRow place badge", () => {
  it("shows the ordinal the server ranked this row at", () => {
    renderRow(undefined, { ...recorded, place: 3 });
    expect(screen.getByText("3rd")).toBeTruthy();
  });

  it("marks 1st distinctly from the rest, and from the record badge beside it", () => {
    renderRow(undefined, { ...recorded, place: 1 });
    expect(screen.getByText("1st").className).toBe("badge first");
    cleanup();
    renderRow(undefined, { ...recorded, place: 2 });
    expect(screen.getByText("2nd").className).toBe("badge muted");
  });

  it("shows nothing where the server placed nothing — no dash, no 'unplaced'", () => {
    const { container } = renderRow(undefined, recorded);
    expect(container.querySelector(".badge.first")).toBeNull();
    expect(container.textContent).not.toMatch(/\d(st|nd|rd|th)\b/);
  });

  it("never badges a queued capture: a ribbon waits for the server to rank it", () => {
    // The record badge's discipline (ADR-0028 §5): a just-typed fastest time is
    // not a placing until it drains and the server agrees.
    const { container } = renderRow({ ...conflicted, status: "queued" });
    expect(container.textContent).not.toMatch(/\d(st|nd|rd|th)\b/);
  });

  it("clears the standing ordinal while a queued correction holds the row", () => {
    // The row reads DNS off this device's outbox; the "1st" beside it would be
    // the server's ranking of the time that DNS replaces.
    const { container } = renderRow(queuedDns, { ...recorded, place: 1 });
    expect(screen.getByLabelText("Result for Sam Swimmer").textContent).toBe("DNS");
    expect(container.textContent).not.toMatch(/\d(st|nd|rd|th)\b/);
  });

  it("reads 'lane pending' while the server owes a queued add its lane", () => {
    // ADR-0038 §2: under auto-fill the placement is the SERVER's, and the row
    // says pending rather than showing a lane two devices could both guess.
    const { container } = renderRow(undefined, undefined, undefined, "Competitive", {
      entry: { ...entry, heat: null, lane: null },
      lanePending: true,
    });
    expect(container.textContent).toContain("lane pending");
  });

  it("promises no lane on a field event — nothing there is laned (ADR-0052 §2)", () => {
    const { container } = renderRow(undefined, undefined, undefined, "Competitive", {
      entry: { ...entry, heat: null, lane: null },
      lanePending: true,
      measureType: "Distance",
    });
    expect(container.textContent).not.toContain("lane pending");
    // And says nothing about a heat either: a queued add is not "no heat".
    expect(container.textContent).not.toContain("no heat");
  });

  it("leads a field row with the name, not a LANE block (ADR-0038 §2)", () => {
    const { container } = renderRow(undefined, undefined, undefined, "Competitive", {
      measureType: "Distance",
      laneControl: true,
      inRace: true,
    });
    expect(container.querySelector(".lane-block")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Actions for Sam Swimmer" }));
    expect(screen.queryByRole("menuitem", { name: "Change lane" })).toBeNull();
    // ADR-0053 §6: the flight is still a heat as DATA, but the deck says nothing
    // about heats on a field event — so the action is simply "Remove entry".
    expect(screen.queryByRole("menuitem", { name: "Remove from heat" })).toBeNull();
    expect(screen.getByRole("menuitem", { name: "Remove entry" })).toBeTruthy();
  });

  it("keeps the lane block and the lane action on a time row", () => {
    const { container } = renderRow(undefined, undefined, undefined, "Competitive", {
      laneControl: true,
      inRace: true,
    });
    expect(container.querySelector(".lane-block")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Actions for Sam Swimmer" }));
    expect(screen.getByRole("menuitem", { name: "Change lane" })).toBeTruthy();
  });

  it("captures a mark in metres and offers NM, on a field event only", () => {
    renderRow(undefined, undefined, undefined, "Competitive", { measureType: "Distance" });
    const input = screen.getByLabelText("Mark for Sam Swimmer");
    expect(screen.queryByLabelText("Time for Sam Swimmer")).toBeNull();
    fireEvent.change(input, { target: { value: "432" } });
    // The field holds the keystrokes; the reading is the caption's (F1).
    expect((input as HTMLInputElement).value).toBe("432");
    expect(screen.getByText("reads 4.32 m")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Actions for Sam Swimmer" }));
    expect(screen.getByRole("menuitem", { name: "NM" })).toBeTruthy();
  });

  it("offers no NM on a time event", () => {
    renderRow(undefined);
    expect(screen.getByLabelText("Time for Sam Swimmer")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Actions for Sam Swimmer" }));
    expect(screen.getByRole("menuitem", { name: "DNF" })).toBeTruthy();
    expect(screen.queryByRole("menuitem", { name: "NM" })).toBeNull();
  });

  it("shows a recorded mark in metres, and a recorded NM as NM", () => {
    renderRow(undefined, { ...recorded, measuredValue: 4.32 }, undefined, "Competitive", {
      measureType: "Distance",
    });
    expect(screen.getByLabelText("Result for Sam Swimmer").textContent).toBe("4.32 m");
    cleanup();
    renderRow(undefined, { ...recorded, status: "Nm", measuredValue: null }, undefined,
      "Competitive", { measureType: "Distance" });
    expect(screen.getByLabelText("Result for Sam Swimmer").textContent).toBe("NM");
  });

  it("clears the ordinal while the row is being retyped", () => {
    renderRow(undefined, { ...recorded, place: 1 });
    expect(screen.getByText("1st")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    expect(screen.queryByText("1st")).toBeNull();
  });

  it("badges nothing on a row in NO heat of a division that has them", () => {
    // ADR-0039 §3: legacy data only, and it is not in the field being ranked.
    const { container } = renderRow(undefined, { ...recorded, place: 1 }, undefined, "Competitive", {
      entry: { ...entry, heat: null },
      unplaced: true,
    });
    expect(container.textContent).not.toMatch(/\d(st|nd|rd|th)\b/);
  });

  it("badges a heatless row where the division has no heats — that field IS the division", () => {
    // Field events and unraced divisions: the server ranks the heatless pool as
    // its own field, exactly as the results book prints it.
    renderRow(undefined, { ...recorded, place: 1 }, undefined, "Competitive", {
      entry: { ...entry, heat: null },
      unplaced: false,
    });
    expect(screen.getByText("1st")).toBeTruthy();
  });

  it("badges nothing on a RACED but heatless row — the axis is the heat (review A5)", () => {
    // The sweep's own product: folded into a race, no heat number yet. It is
    // not in any ranked field, whatever race it is a member of.
    const { container } = renderRow(undefined, { ...recorded, place: 1 }, undefined, "Competitive", {
      entry: { ...entry, heat: null, raceId: "r-1" },
      unplaced: true,
      raceless: false,
    });
    expect(container.textContent).not.toMatch(/\d(st|nd|rd|th)\b/);
  });

  it("marks a row that is in no race at all, so it does not read as a member (review B7)", () => {
    const { container } = renderRow(undefined, undefined, undefined, "Competitive", {
      entry: { ...entry, heat: null, lane: null, raceId: null },
      raceless: true,
    });
    expect(container.textContent).toContain("no heat");
  });

  it("shows the entry's own lane only where the deck has no answer of its own", () => {
    const laneText = (c: HTMLElement) => c.querySelector(".lane-number")!.textContent;
    const noAnswer = renderRow(undefined, undefined, undefined, "Competitive", {
      entry: { ...entry, lane: 4 },
    });
    expect(laneText(noAnswer.container)).toBe("4");
    noAnswer.unmount();
    // A park without a lane this device holds must not read as the old lane.
    const parked = renderRow(undefined, undefined, undefined, "Competitive", {
      entry: { ...entry, lane: 4 },
      lane: null,
      inRace: true,
    });
    expect(laneText(parked.container)).not.toBe("4");
  });
});

/**
 * The attempt strip (ADR-0053 §6). The row's value slot is the SERVER's derived
 * best mark; the cells are the capture, one write each.
 */
describe("EntryRow attempt strip", () => {
  const series = (measured: number | null, attempts: (ResultDto["attempts"] & object)[number][]) =>
    ({
      ...recorded,
      measuredValue: measured,
      status: measured === null ? ("Nm" as const) : ("Ok" as const),
      attempts,
    }) satisfies ResultDto;

  it("renders one cell per attempt and enqueues a mark against its own ordinal", async () => {
    renderRow(undefined, undefined, undefined, "Competitive", {
      measureType: "Distance",
      attemptCount: 3,
    });
    // Three cells, and no single best-mark field anywhere on the row.
    expect(screen.getAllByLabelText(/^Attempt \d mark for Sam Swimmer$/)).toHaveLength(3);
    expect(screen.queryByLabelText("Mark for Sam Swimmer")).toBeNull();

    const second = screen.getByLabelText("Attempt 2 mark for Sam Swimmer");
    fireEvent.change(second, { target: { value: "432" } });
    fireEvent.click(second.closest(".attempt-cell")!.querySelector("button.primary")!);

    await waitFor(() =>
      expect(submitAttemptViaOutbox).toHaveBeenCalledWith(
        expect.objectContaining({ ordinal: 2, value: "4.32", foul: false, attemptCount: 3 }),
      ),
    );
  });

  it("enqueues a foul as the cell with no mark", async () => {
    renderRow(undefined, undefined, undefined, "Competitive", {
      measureType: "Distance",
      attemptCount: 3,
    });
    fireEvent.click(screen.getByRole("button", { name: "Foul on attempt 1 for Sam Swimmer" }));

    await waitFor(() =>
      expect(submitAttemptViaOutbox).toHaveBeenCalledWith(
        expect.objectContaining({ ordinal: 1, value: null, foul: true }),
      ),
    );
  });

  it("shows the taken cells as marks and Xs, with the derived best in the value slot", () => {
    renderRow(undefined, series(4.32, [{ value: 4.1, foul: false }, { value: null, foul: true }, { value: 4.32, foul: false }]), undefined,
      "Competitive", { measureType: "Distance", attemptCount: 3 });

    expect(screen.getByLabelText("Attempt 1").textContent).toBe("4.10");
    expect(screen.getByLabelText("Attempt 2").textContent).toBe("X");
    expect(screen.getByLabelText("Attempt 3").textContent).toBe("4.32");
    expect(screen.getByLabelText("Result for Sam Swimmer").textContent).toBe("4.32 m");
    // Nothing is typeable while every ordinal is taken.
    expect(screen.queryByLabelText(/^Attempt \d mark for/)).toBeNull();
  });

  it("reads an all-foul series as NM in the value slot, and never offers NM in the menu", () => {
    renderRow(undefined, series(null, [{ value: null, foul: true }, { value: null, foul: true }, { value: null, foul: true }]), undefined,
      "Competitive", { measureType: "Distance", attemptCount: 3 });

    expect(screen.getByLabelText("Result for Sam Swimmer").textContent).toBe("NM");
    fireEvent.click(screen.getByRole("button", { name: "Actions for Sam Swimmer" }));
    // Derived, so never typed (§6) — the other three stay.
    expect(screen.queryByRole("menuitem", { name: "NM" })).toBeNull();
    for (const code of ["DNS", "DNF", "DQ"])
      expect(screen.getByRole("menuitem", { name: code })).toBeTruthy();
  });

  it("keeps NM on a single-measure field event", () => {
    renderRow(undefined, undefined, undefined, "Competitive", { measureType: "Distance" });
    fireEvent.click(screen.getByRole("button", { name: "Actions for Sam Swimmer" }));
    expect(screen.getByRole("menuitem", { name: "NM" })).toBeTruthy();
  });

  it("shows a queued cell with the row's own sync badge", () => {
    const queued: OutboxItem = {
      ...conflicted,
      kind: "result.attempt",
      status: "queued",
      serverResponse: undefined,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-1",
        entryId: "en-1",
        ordinal: 1,
        value: "3.95",
        foul: false,
      },
    };
    renderRow(undefined, undefined, undefined, "Competitive", {
      measureType: "Distance",
      attemptCount: 3,
      attemptItems: new Map([[1, queued]]),
    });

    expect(screen.getByLabelText("Attempt 1").textContent).toBe("3.95");
    // The row's own sync badge, in its own words.
    expect(screen.getByLabelText("Attempt 1").closest(".attempt-cell")!.textContent).toContain(
      "pending",
    );
    // The other two cells are still open for capture.
    expect(screen.getAllByLabelText(/^Attempt \d mark for Sam Swimmer$/)).toHaveLength(2);
  });

  it("keeps the server's series where a queued cell has been accepted", () => {
    const accepted: OutboxItem = {
      ...conflicted,
      kind: "result.attempt",
      status: "accepted",
      serverResponse: { id: "res-1", currentRevision: 2 },
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-1",
        entryId: "en-1",
        ordinal: 1,
        value: "4.10",
        foul: false,
      },
    };
    // The server re-measured it; the accepted capture is history, and would
    // otherwise mask the real mark for the seven days the item is kept.
    renderRow(undefined, series(4.4, [{ value: 4.4, foul: false }]), undefined, "Competitive", {
      measureType: "Distance",
      attemptCount: 3,
      attemptItems: new Map([[1, accepted]]),
    });

    expect(screen.getByLabelText("Attempt 1").textContent).toBe("4.40");
    expect(screen.getByRole("button", { name: "Edit attempt 1 for Sam Swimmer" })).toBeTruthy();
  });

  it("puts no Cancel on the row's silent status form", () => {
    // It is a write path, not an open sheet: there is nothing on screen to
    // abandon, and a Cancel on every settled row is chrome the deck never had.
    renderRow(undefined, series(4.32, [{ value: 4.32, foul: false }]), undefined, "Competitive", {
      measureType: "Distance",
      attemptCount: 3,
    });
    expect(screen.queryByRole("button", { name: "Cancel" })).toBeNull();
  });

  it("writes a menu DNF on an athlete with a series as a CORRECTION", async () => {
    vi.mocked(submitCorrection).mockResolvedValue({ kind: "corrected", result: recorded });
    renderRow(undefined, series(4.32, [{ value: 4.32, foul: false }]), undefined, "Competitive", {
      measureType: "Distance",
      attemptCount: 3,
    });

    fireEvent.click(screen.getByRole("button", { name: "Actions for Sam Swimmer" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "DNF" }));

    // A submit here draws RESULT_EXISTS; the correction is what clears the series.
    await waitFor(() => expect(submitCorrection).toHaveBeenCalled());
    const sent = vi.mocked(submitCorrection).mock.calls[0]![0];
    expect(sent.status).toBe("Dnf");
    expect(sent.resultId).toBe("res-1");
    expect(sent.attempts).toBeUndefined();
  });

  it("puts a mark back on a DNF row through the correction sheet, not the cell route", async () => {
    vi.mocked(submitCorrection).mockResolvedValue({ kind: "corrected", result: recorded });
    const dnf: ResultDto = { ...recorded, status: "Dnf", measuredValue: null };
    renderRow(undefined, dnf, undefined, "Competitive", {
      measureType: "Distance",
      attemptCount: 3,
    });

    // The strip's own Save would post result.attempt and draw
    // RESULT_STATUS_BLOCKS_ATTEMPTS, so the cell opens the sheet instead.
    const second = screen.getByLabelText("Attempt 2 mark for Sam Swimmer");
    fireEvent.change(second, { target: { value: "432" } });
    fireEvent.click(second.closest(".attempt-cell")!.querySelector("button.primary")!);
    expect(submitAttemptViaOutbox).not.toHaveBeenCalled();

    const field = screen.getByLabelText(/^Attempt 2 Mark for Sam Swimmer/) as HTMLInputElement;
    expect(field.value).toBe("4.32");
    fireEvent.click(screen.getByRole("button", { name: "Save correction" }));

    await waitFor(() => expect(submitCorrection).toHaveBeenCalled());
    const sent = vi.mocked(submitCorrection).mock.calls[0]![0];
    // The status goes back to derived because the series carries a valid mark.
    expect(sent.status).toBeNull();
    expect(sent.value).toBeNull();
    expect(sent.attempts).toEqual([null, { value: "4.32", foul: false }, null]);
  });

  it("takes a foul on a DNF row through the sheet as well", async () => {
    vi.mocked(submitCorrection).mockResolvedValue({ kind: "corrected", result: recorded });
    renderRow(undefined, { ...recorded, status: "Dns", measuredValue: null }, undefined,
      "Competitive", { measureType: "Distance", attemptCount: 3 });

    fireEvent.click(screen.getByRole("button", { name: "Foul on attempt 1 for Sam Swimmer" }));
    expect(submitAttemptViaOutbox).not.toHaveBeenCalled();
    // Pre-loaded as the foul it was: Save sends it with no mark to type.
    fireEvent.click(screen.getByRole("button", { name: "Save correction" }));

    await waitFor(() => expect(submitCorrection).toHaveBeenCalled());
    expect(vi.mocked(submitCorrection).mock.calls[0]![0].attempts).toEqual([
      { value: null, foul: true },
      null,
      null,
    ]);
  });

  it("opens cell 1 from the row's own Edit on a typed-status attempts row", () => {
    renderRow(undefined, { ...recorded, status: "Dq", measuredValue: null }, undefined,
      "Competitive", { measureType: "Distance", attemptCount: 3 });

    fireEvent.click(screen.getByRole("button", { name: "Edit" }));
    // Not "DQ" in a bare mark field, which the server refuses (ATTEMPTS_REQUIRED).
    const field = screen.getByLabelText(/^Attempt 1 Mark for Sam Swimmer/) as HTMLInputElement;
    expect(field.value).toBe("");
  });

  it("marks a taken cell as a foul from the correction sheet (§4)", async () => {
    vi.mocked(submitCorrection).mockResolvedValue({ kind: "corrected", result: recorded });
    renderRow(undefined, series(4.32, [{ value: 4.1, foul: false }, { value: 4.32, foul: false }]),
      undefined, "Competitive", { measureType: "Distance", attemptCount: 3 });

    fireEvent.click(screen.getByRole("button", { name: "Edit attempt 2 for Sam Swimmer" }));
    fireEvent.click(
      screen.getByRole("button", { name: "Mark attempt 2 for Sam Swimmer as a foul" }),
    );

    await waitFor(() => expect(submitCorrection).toHaveBeenCalled());
    expect(vi.mocked(submitCorrection).mock.calls[0]![0].attempts).toEqual([
      { value: "4.10", foul: false },
      { value: null, foul: true },
      null,
    ]);
  });

  it("clears a half-typed cell when the official taps Foul instead", async () => {
    renderRow(undefined, undefined, undefined, "Competitive", {
      measureType: "Distance",
      attemptCount: 3,
    });
    const first = screen.getByLabelText("Attempt 1 mark for Sam Swimmer") as HTMLInputElement;
    fireEvent.change(first, { target: { value: "41" } });
    fireEvent.click(screen.getByRole("button", { name: "Foul on attempt 1 for Sam Swimmer" }));

    await waitFor(() =>
      expect(submitAttemptViaOutbox).toHaveBeenCalledWith(
        expect.objectContaining({ ordinal: 1, value: null, foul: true }),
      ),
    );
    // What happened is the foul, so nothing is left in the field to contradict it.
    expect((screen.getByLabelText("Attempt 1 mark for Sam Swimmer") as HTMLInputElement).value)
      .toBe("");
  });

  it("draws a queued correction's series, and a second cell edit starts from it", async () => {
    vi.mocked(submitCorrection).mockResolvedValue({ kind: "corrected", result: recorded });
    const stored = series(4.32, [
      { value: 4.1, foul: false },
      { value: null, foul: true },
      { value: 4.32, foul: false },
    ]);
    const queuedFix: OutboxItem = {
      ...queuedDns,
      payload: {
        ...payload,
        value: null,
        status: null,
        resultId: "res-1",
        reason: "Corrected at the deck",
        attempts: [
          { value: "4.10", foul: false },
          { value: "4.55", foul: false },
          { value: "4.32", foul: false },
        ],
      },
    };
    renderRow(queuedFix, stored, undefined, "Competitive", {
      measureType: "Distance",
      attemptCount: 3,
    });
    expect(screen.getByLabelText("Attempt 2").textContent).toBe("4.55");
    expect(screen.getByLabelText("Result for Sam Swimmer").textContent).toBe("4.55 m");

    fireEvent.click(screen.getByRole("button", { name: "Edit attempt 3 for Sam Swimmer" }));
    fireEvent.change(screen.getByLabelText(/^Attempt 3 Mark for Sam Swimmer/), {
      target: { value: "440" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Save correction" }));
    await waitFor(() => expect(submitCorrection).toHaveBeenCalled());
    expect(vi.mocked(submitCorrection).mock.calls[0]![0].attempts).toEqual([
      { value: "4.10", foul: false },
      { value: "4.55", foul: false },
      { value: "4.40", foul: false },
    ]);
  });

  it("corrects ONE cell by restating the whole series with a reason (§4)", async () => {
    vi.mocked(submitCorrection).mockResolvedValue({ kind: "corrected", result: recorded });
    renderRow(undefined, series(4.32, [{ value: 4.1, foul: false }, { value: null, foul: true }, { value: 4.32, foul: false }]), undefined,
      "Competitive", { measureType: "Distance", attemptCount: 3 });

    fireEvent.click(screen.getByRole("button", { name: "Edit attempt 1 for Sam Swimmer" }));
    const field = screen.getByLabelText(/^Attempt 1 Mark for Sam Swimmer/) as HTMLInputElement;
    expect(field.value).toBe("4.10");
    fireEvent.change(field, { target: { value: "415" } });
    fireEvent.click(screen.getByRole("button", { name: "Save correction" }));

    await waitFor(() => expect(submitCorrection).toHaveBeenCalled());
    const sent = vi.mocked(submitCorrection).mock.calls[0]![0];
    // The whole series, the edited cell replaced — and no typed best mark, which
    // an attempts event refuses.
    expect(sent.attempts).toEqual([
      { value: "4.15", foul: false },
      { value: null, foul: true },
      { value: "4.32", foul: false },
    ]);
    expect(sent.value).toBeNull();
    expect(sent.reason).toBe("Corrected at the deck");
  });
});
