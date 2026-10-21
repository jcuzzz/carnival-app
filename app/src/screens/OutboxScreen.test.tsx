// @vitest-environment jsdom
import { beforeEach, afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { renderWithQuery } from "../test/queryWrapper";
import { db, type OutboxStatus } from "../db/db";
import { signIn, signOut } from "../auth/session";
import {
  ENTRY_ASSIGN_KIND,
  ENTRY_CREATE_KIND,
  RACE_MATERIALISE_KIND,
  RESULT_ATTEMPT_KIND,
  RESULT_CORRECT_KIND,
  RESULT_JUMP_KIND,
  RESULT_SUBMIT_KIND,
  TEAM_CREATE_KIND,
  type EntryAssignPayload,
  type RaceMaterialisePayload,
  type ResultAttemptPayload,
  type ResultCapturePayload,
  type ResultJumpPayload,
  type TeamCreatePayload,
} from "../api/outboxTransport";

/**
 * The per-item outbox view (ADR-0002/0013). What is pinned here is the ONE
 * sentence each row gets: a parked item is read by a person deciding what to do
 * about it, so "entry.assign" — or a placement that will not say which race —
 * is not a decidable sentence at a carnival with thirty races on the program.
 */

vi.mock("../api/syncSetup", () => ({
  syncWorker: { start: vi.fn(), stop: vi.fn(), syncNow: vi.fn() },
  resumeAfterSignIn: vi.fn(),
}));

import { syncWorker } from "../api/syncSetup";
import { ARMING_LOCKOUT_MS } from "../features/ui/timing";
import OutboxScreen from "./OutboxScreen";

async function queuePlacement(id: string, payload: Partial<EntryAssignPayload>): Promise<void> {
  await db.outbox.put({
    id,
    kind: ENTRY_ASSIGN_KIND,
    payload: {
      carnivalId: "c-1",
      carnivalEventId: "ev-17",
      entryId: "en-1",
      raceId: "r-1",
      lane: 4,
      heat: null,
      display: { entryDisplayName: "Nina Novak" },
      ...payload,
    } satisfies EntryAssignPayload,
    actorSubject: "official-1",
    capturedAt: "2026-08-13T09:48:00.000Z",
    status: "conflict",
    attempts: 1,
  });
}

beforeEach(async () => {
  cleanup();
  await db.outbox.clear();
  await db.syncMeta.clear();
});

afterEach(() => cleanup());

describe("OutboxScreen", () => {
  it("names the race a parked placement belongs to", async () => {
    await queuePlacement("i-1", {
      display: { entryDisplayName: "Nina Novak", raceTitle: "Race 2" },
    });
    renderWithQuery(<OutboxScreen />);
    expect(await screen.findByText("Place Nina Novak in lane 4 — Race 2")).toBeTruthy();
  });

  it("still describes a placement queued before the race title was recorded", async () => {
    // Additive, so items already in the queue on an existing device must keep
    // rendering — just without the race.
    await queuePlacement("i-2", { display: { entryDisplayName: "Nina Novak" } });
    renderWithQuery(<OutboxScreen />);
    expect(await screen.findByText("Place Nina Novak in lane 4")).toBeTruthy();
  });

  it("calls a cleared placement what it is, never a lane-less one", async () => {
    await queuePlacement("i-3", { raceId: null, lane: null });
    renderWithQuery(<OutboxScreen />);
    expect(await screen.findByText("Clear placement for Nina Novak")).toBeTruthy();
  });

  it("says which division a parked materialisation would race (ADR-0019)", async () => {
    // One human act, one row. "race.materialise" tells the person reading it
    // nothing about which pass of the pool it describes.
    await db.outbox.put({
      id: "i-materialise",
      kind: RACE_MATERIALISE_KIND,
      payload: {
        carnivalId: "c-1",
        eventId: "ev-13f",
        scheduleSlotId: null,
        label: null,
        display: { title: "13 · F" },
      } satisfies RaceMaterialisePayload,
      actorSubject: "official-1",
      capturedAt: "2026-08-14T09:48:00.000Z",
      status: "conflict",
      attempts: 1,
    });
    renderWithQuery(<OutboxScreen />);
    expect(await screen.findByText("Create race for 13 · F")).toBeTruthy();
  });

  it("names the HOUSE of a parked relay team (ADR-0039 A4)", async () => {
    // "team.create" does not say which house is missing from the water.
    await db.outbox.put({
      id: "i-team",
      kind: TEAM_CREATE_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-relay",
        houseCode: "BANKSIA",
        studentIds: ["st-1", "st-2", "st-3", "st-4"],
        raceId: "r-2",
      } satisfies TeamCreatePayload,
      actorSubject: "official-1",
      capturedAt: "2026-09-04T09:48:00.000Z",
      status: "conflict",
      attempts: 1,
      lastError: "TEAM_EXISTS",
    });
    renderWithQuery(<OutboxScreen />);
    expect(await screen.findByText("Relay team — BANKSIA")).toBeTruthy();
  });

  it("renders the server's warnings on a race it ACCEPTED (ADR-0017 grammar)", async () => {
    // Incoherent is refused, implausible is warned: a division overflowing the
    // lane count is a legitimate mass start, so the server never refuses it —
    // which makes this row the only place the warning is ever read.
    await db.outbox.put({
      id: "i-warned",
      kind: RACE_MATERIALISE_KIND,
      payload: {
        carnivalId: "c-1",
        eventId: "ev-13f",
        scheduleSlotId: null,
        label: null,
        display: { title: "13 · F" },
      } satisfies RaceMaterialisePayload,
      actorSubject: "official-1",
      capturedAt: "2026-08-14T09:50:00.000Z",
      status: "accepted",
      attempts: 1,
      syncedAt: "2026-08-14T09:50:01.000Z",
      serverResponse: {
        warnings: ["This race holds 14 entries but this venue has 8 lanes (carnival settings)."],
      },
    });
    renderWithQuery(<OutboxScreen />);
    expect(
      await screen.findByText(/This race holds 14 entries but this venue has 8 lanes/),
    ).toBeTruthy();
  });

  it("gives the reason as the server's sentence, never the HTTP status", async () => {
    // This is the surface someone decides requeue-or-discard from, and
    // "conflict: HTTP 409" is not a reason to decide from — the deck row for the
    // same item has always printed the ADR-0011 `detail`.
    await db.outbox.put({
      id: "i-409",
      kind: RESULT_SUBMIT_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-17",
        entryId: "en-1",
        value: "35.67",
        status: null,
        display: { entryDisplayName: "Nina Novak" },
      } satisfies ResultCapturePayload,
      actorSubject: "official-1",
      capturedAt: "2026-08-13T09:48:00.000Z",
      status: "conflict",
      attempts: 1,
      lastError: "conflict: HTTP 409",
      serverResponse: {
        error: "RESULT_EXISTS",
        detail: "This entry already has a result. Correct it rather than capturing it again.",
      },
    });
    renderWithQuery(<OutboxScreen />);
    expect(await screen.findByText(/This entry already has a result/)).toBeTruthy();
    expect(screen.queryByText(/HTTP 409/)).toBeNull();
  });
});

/**
 * Recovery (ADR-0025): the screen stops being read-only. What is pinned here is
 * the shape of the three acts — that requeue is one tap and discard is two,
 * that `rejected → queued` is never offered, and that a discarded capture is
 * filed as finished with rather than left looking like outstanding work.
 */

async function queueResult(
  id: string,
  status: OutboxStatus,
  payload: Partial<ResultCapturePayload> = {},
): Promise<void> {
  await db.outbox.put({
    id,
    kind: RESULT_SUBMIT_KIND,
    payload: {
      carnivalId: "c-1",
      carnivalEventId: "ev-17",
      entryId: "en-1",
      value: "1:23.45",
      status: null,
      display: { entryDisplayName: "Nina Novak" },
      ...payload,
    } satisfies ResultCapturePayload,
    actorSubject: "official-1",
    capturedAt: "2026-08-13T09:48:00.000Z",
    status,
    attempts: 2,
    lastError: "LANE_TAKEN",
  });
}

describe("OutboxScreen recovery (ADR-0025)", () => {
  beforeEach(() => signIn({ subject: "official-2", displayName: "Sam Marshal", roles: [] }));
  afterEach(() => signOut());

  it("requeues a parked capture, clearing the refusal it carried", async () => {
    await queuePlacement("r-1", {});
    renderWithQuery(<OutboxScreen />);
    fireEvent.click(await screen.findByRole("button", { name: "Requeue" }));
    await waitFor(async () => {
      const item = await db.outbox.get("r-1");
      expect(item?.status).toBe("queued");
      expect(item?.lastError).toBeUndefined();
      // The attempt count survives: this is the same capture, not a new one.
      expect(item?.attempts).toBe(1);
    });
  });

  it("takes two steps to discard, and ignores the second half of a double-tap", async () => {
    await queuePlacement("r-2", {});
    renderWithQuery(<OutboxScreen />);
    const discard = await screen.findByRole("button", { name: "Discard…" });

    // Only the clock is faked: Dexie and waitFor keep their real timers.
    vi.useFakeTimers({ toFake: ["Date"] });
    try {
      fireEvent.click(discard);
      expect((await db.outbox.get("r-2"))?.status).toBe("conflict");

      const armed = screen.getByRole("button", { name: /^Confirm discarding/ });
      fireEvent.click(armed);
      expect(armed.isConnected).toBe(true);
      expect((await db.outbox.get("r-2"))?.status).toBe("conflict");

      vi.advanceTimersByTime(ARMING_LOCKOUT_MS);
      fireEvent.click(armed);
    } finally {
      vi.useRealTimers();
    }
    await waitFor(async () => {
      const item = await db.outbox.get("r-2");
      expect(item?.status).toBe("discarded");
      // The resolver is the CURRENT session, not the capturer (§4).
      expect(item?.resolvedBy).toBe("official-2");
      expect(item?.actorSubject).toBe("official-1");
    });
  });

  it("says what the discard costs: the adds AND the results queued behind it", async () => {
    // The count read `entry.create` only, so a relay and a swum time queued
    // behind a parked mint were both missing from the sentence a person
    // confirms against.
    await db.outbox.put({
      id: "en-9",
      kind: ENTRY_CREATE_KIND,
      payload: { carnivalId: "c-1", carnivalEventId: "ev-17", studentId: "st-1" },
      actorSubject: "official-1",
      capturedAt: "2026-08-13T09:48:00.000Z",
      status: "conflict",
      attempts: 1,
    });
    await queueResult("res-9", "queued", { entryId: "en-9" });
    renderWithQuery(<OutboxScreen />);

    fireEvent.click(await screen.findByRole("button", { name: "Discard…" }));

    expect(await screen.findByText(/1 captured result behind it/)).toBeTruthy();
  });

  it("offers a rejected capture dismissal only, never a requeue (§3)", async () => {
    await queueResult("r-3", "rejected");
    renderWithQuery(<OutboxScreen />);
    expect(await screen.findByRole("button", { name: "Discard…" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /requeue/i })).toBeNull();
  });

  it("offers edit-and-requeue on a result capture and not on a placement", async () => {
    await queueResult("r-4", "conflict");
    await queuePlacement("r-5", {});
    const { unmount } = renderWithQuery(<OutboxScreen />);
    const rows = await screen.findAllByRole("row");
    const resultRow = rows.find((r) => within(r).queryByText(/^Result 1:23.45/))!;
    const placementRow = rows.find((r) => within(r).queryByText(/^Place Nina Novak/))!;
    expect(within(resultRow).getByRole("button", { name: "Edit and requeue" })).toBeTruthy();
    expect(within(placementRow).queryByRole("button", { name: "Edit and requeue" })).toBeNull();
    unmount();
  });

  it("corrects a mistyped time through the deck's own field, keeping the item's id", async () => {
    // The id IS the idempotency key: a retype under a fresh key would park
    // again as a 409 if the original POST had in fact landed (§1).
    await queueResult("r-7", "conflict");
    renderWithQuery(<OutboxScreen />);
    fireEvent.click(await screen.findByRole("button", { name: "Edit and requeue" }));
    const field = await screen.findByRole("textbox", { name: /^Corrected time for Nina Novak/ });
    expect((field as HTMLInputElement).value).toBe("1:23.45");
    fireEvent.change(field, { target: { value: "1:23.99" } });
    fireEvent.click(screen.getByRole("button", { name: "Save and requeue" }));
    await waitFor(async () => {
      const item = await db.outbox.get("r-7");
      expect(item?.status).toBe("queued");
      expect((item?.payload as ResultCapturePayload).value).toBe("1:23.99");
      expect(item?.lastError).toBeUndefined();
    });
  });

  it("drains as soon as a corrected capture is requeued, as the deck does", async () => {
    await queueResult("r-8", "conflict");
    vi.mocked(syncWorker.syncNow).mockClear();
    renderWithQuery(<OutboxScreen />);
    fireEvent.click(await screen.findByRole("button", { name: "Edit and requeue" }));
    const field = await screen.findByRole("textbox", { name: /^Corrected time for Nina Novak/ });
    fireEvent.change(field, { target: { value: "1:23.99" } });
    fireEvent.click(screen.getByRole("button", { name: "Save and requeue" }));
    await waitFor(() => expect(syncWorker.syncNow).toHaveBeenCalled());
  });

  it("orders the resolved table by when each item finished, not when it was captured", async () => {
    // The caption promises newest first, and an item captured at 09:00 and
    // accepted at 15:00 finished after one captured at 14:00.
    await db.outbox.put({
      id: "a-early-capture",
      kind: RESULT_SUBMIT_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-17",
        entryId: "en-1",
        value: "1:01.01",
        status: null,
        display: { entryDisplayName: "Nina Novak" },
      } satisfies ResultCapturePayload,
      actorSubject: "official-1",
      capturedAt: "2026-08-13T09:00:00.000Z",
      status: "accepted",
      attempts: 1,
      syncedAt: "2026-08-13T15:00:00.000Z",
    });
    await db.outbox.put({
      id: "z-later-capture",
      kind: RESULT_SUBMIT_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-17",
        entryId: "en-2",
        value: "2:02.02",
        status: null,
        display: { entryDisplayName: "Ari Blake" },
      } satisfies ResultCapturePayload,
      actorSubject: "official-1",
      capturedAt: "2026-08-13T14:00:00.000Z",
      status: "discarded",
      attempts: 1,
      resolvedBy: "official-2",
      resolvedAt: "2026-08-13T14:05:00.000Z",
    });
    renderWithQuery(<OutboxScreen />);

    const resolved = await screen.findByRole("table", {
      name: "Synced and discarded items, newest first",
    });
    await waitFor(() => expect(within(resolved).getAllByRole("row")).toHaveLength(3));
    const [, first, second] = within(resolved).getAllByRole("row");
    expect(within(first!).getByText(/Nina Novak/)).toBeTruthy();
    expect(within(second!).getByText(/Ari Blake/)).toBeTruthy();
  });

  it("files a discarded capture as finished with, not as work still needing attention", async () => {
    await queueResult("r-6", "discarded");
    const parked = (await db.outbox.get("r-6"))!;
    await db.outbox.put({
      ...parked,
      resolvedBy: "official-2",
      resolvedAt: "2026-08-13T10:02:00.000Z",
    });
    renderWithQuery(<OutboxScreen />);

    const resolved = await screen.findByRole("table", {
      name: "Synced and discarded items, newest first",
    });
    expect(within(resolved).getByText(/^Result 1:23.45/)).toBeTruthy();
    expect(within(resolved).getByText(/Discarded by official-2/)).toBeTruthy();
    expect(
      screen.getByText("Nothing waiting — every capture on this device has reached the server."),
    ).toBeTruthy();
    expect(screen.queryByRole("table", { name: "Outbox items in capture order" })).toBeNull();
  });
});

/**
 * ADR-0031 (B4/C6): the queue prints other officials' captured student names,
 * so acting on it takes a named session — and an accepted row stops printing
 * the name at all, because the server holds the record by then.
 */
describe("OutboxScreen on a shared device (ADR-0031)", () => {
  afterEach(() => signOut());

  it("offers no recovery at all to a device with nobody signed in", async () => {
    signOut();
    await queuePlacement("s-1", {});
    renderWithQuery(<OutboxScreen />);

    expect(await screen.findByText("Sign in to recover this capture")).toBeTruthy();
    // Requeue drains under the ORIGINAL capturer's X-Captured-Actor, so an
    // anonymous holder of the device must not be able to fire it.
    expect(screen.queryByRole("button", { name: "Requeue" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Edit and requeue" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Discard…" })).toBeNull();
  });

  it("names the second official on a capture they edited, keeping the first", async () => {
    signIn({ subject: "official-2", displayName: "Sam Marshal", roles: [] });
    await queueResult("s-2", "conflict");
    renderWithQuery(<OutboxScreen />);

    fireEvent.click(await screen.findByRole("button", { name: "Edit and requeue" }));
    const field = await screen.findByRole("textbox", { name: /^Corrected time for Nina Novak/ });
    fireEvent.change(field, { target: { value: "1:23.99" } });
    fireEvent.click(screen.getByRole("button", { name: "Save and requeue" }));

    await waitFor(async () => {
      const item = await db.outbox.get("s-2");
      expect(item?.actorSubject).toBe("official-1");
      expect(item?.editedBySubject).toBe("official-2");
    });
    expect(await screen.findByText("Edited by Sam Marshal")).toBeTruthy();
  });

  it("keeps the reassurance row readable once the name is gone", async () => {
    // C6 strips `display` on acceptance; the row still has to say what landed
    // and when, which is all it was ever for.
    signIn({ subject: "official-1", displayName: "Ali Recorder", roles: [] });
    await db.outbox.put({
      id: "s-3",
      kind: RESULT_SUBMIT_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-17",
        entryId: "en-1",
        value: "1:23.45",
        status: null,
      } as unknown as ResultCapturePayload,
      actorSubject: "official-1",
      capturedAt: "2026-08-22T09:48:00.000Z",
      status: "accepted",
      attempts: 1,
      syncedAt: "2026-08-22T09:48:02.000Z",
    });
    renderWithQuery(<OutboxScreen />);

    expect(await screen.findByText("Result 1:23.45")).toBeTruthy();
    // No raw entry id standing in for the person who was dropped.
    expect(screen.queryByText(/en-1/)).toBeNull();
  });
});

/**
 * ADR-0053 §6: a parked cell is read by someone deciding whether that round
 * went off, so the sentence names the ordinal and the mark — and a foul has
 * nothing to retype.
 */
describe("OutboxScreen attempt captures", () => {
  beforeEach(() => signIn({ subject: "official-2", displayName: "Sam Marshal", roles: [] }));
  afterEach(() => signOut());

  async function queueAttempt(id: string, payload: Partial<ResultAttemptPayload>): Promise<void> {
    await db.outbox.put({
      id,
      kind: RESULT_ATTEMPT_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-17",
        entryId: "en-1",
        ordinal: 2,
        value: "4.32",
        foul: false,
        display: {
          entryDisplayName: "Nina Novak",
          measureType: "Distance",
          attemptCount: 3,
        },
        ...payload,
      } satisfies ResultAttemptPayload,
      actorSubject: "official-1",
      capturedAt: "2026-08-13T09:48:00.000Z",
      status: "conflict",
      attempts: 1,
    });
  }

  it("names the ordinal and the mark, in the event's own unit", async () => {
    await queueAttempt("a-1", {});
    renderWithQuery(<OutboxScreen />);
    // Of how many rounds: attempt 2 of six reads nothing like attempt 2 of two,
    // and a parked cell is read cold, hours later.
    expect(await screen.findByText("Attempt 2 of 3 · 4.32 m — Nina Novak")).toBeTruthy();
  });

  it("names the ordinal alone where the capture recorded no count", async () => {
    await queueAttempt("a-4", {
      display: { entryDisplayName: "Nina Novak", measureType: "Distance" },
    });
    renderWithQuery(<OutboxScreen />);
    expect(await screen.findByText("Attempt 2 · 4.32 m — Nina Novak")).toBeTruthy();
  });

  it("retypes a parked mark through the mark mask", async () => {
    await queueAttempt("a-2", {});
    renderWithQuery(<OutboxScreen />);
    fireEvent.click(await screen.findByRole("button", { name: "Edit and requeue" }));

    const field = screen.getByLabelText("Corrected mark for Nina Novak") as HTMLInputElement;
    expect(field.value).toBe("4.32");
    fireEvent.change(field, { target: { value: "415" } });
    fireEvent.click(screen.getByRole("button", { name: "Save and requeue" }));

    await waitFor(async () => {
      const item = await db.outbox.get("a-2");
      expect(item?.status).toBe("queued");
      expect(item?.payload).toMatchObject({ ordinal: 2, value: "4.15", foul: false });
    });
  });

  it("offers a parked FOUL retry and discard only — there is no value to retype", async () => {
    await queueAttempt("a-3", { value: null, foul: true });
    renderWithQuery(<OutboxScreen />);
    expect(await screen.findByText("Attempt 2 of 3 · X — Nina Novak")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Requeue" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Edit and requeue" })).toBeNull();
  });
});

/**
 * ADR-0056 §2: a parked mark names the bar and the mark, because a person read
 * it cold hours later has to decide whether that jump happened. There is no
 * retype: a mark is one of three taps at a fixed height, not a typed measure.
 */
describe("OutboxScreen high-jump marks", () => {
  beforeEach(() => signIn({ subject: "official-2", displayName: "Sam Marshal", roles: [] }));
  afterEach(() => signOut());

  async function queueJump(id: string, payload: Partial<ResultJumpPayload>): Promise<void> {
    await db.outbox.put({
      id,
      kind: RESULT_JUMP_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-17",
        entryId: "en-1",
        height: 1.35,
        mark: "O",
        display: { entryDisplayName: "Finley Larsen", measureType: "Height" },
        ...payload,
      } satisfies ResultJumpPayload,
      actorSubject: "official-1",
      capturedAt: "2026-08-13T09:48:00.000Z",
      status: "conflict",
      attempts: 1,
    });
  }

  it("names the bar and the mark", async () => {
    await queueJump("j-1", {});
    renderWithQuery(<OutboxScreen />);
    expect(await screen.findByText("Jump 1.35 m · O — Finley Larsen")).toBeTruthy();
  });

  it("offers retry and discard, never a retype", async () => {
    await queueJump("j-2", { mark: "X" });
    renderWithQuery(<OutboxScreen />);
    expect(await screen.findByRole("button", { name: "Requeue" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Edit and requeue" })).toBeNull();
  });

  it("says a queued mark is waiting behind the parked one on the same card", async () => {
    // The drain steps over it (syncWorker's hold rule), so the row must not read
    // as work about to go out.
    await queueJump("j-3", { mark: "X" });
    await db.outbox.put({
      id: "j-4",
      kind: RESULT_JUMP_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-17",
        entryId: "en-1",
        height: 1.35,
        mark: "O",
        display: { entryDisplayName: "Finley Larsen", measureType: "Height" },
      } satisfies ResultJumpPayload,
      actorSubject: "official-1",
      capturedAt: "2026-08-13T09:49:00.000Z",
      status: "queued",
      attempts: 0,
    });
    renderWithQuery(<OutboxScreen />);
    const held = (await screen.findAllByText("Jump 1.35 m · O — Finley Larsen"))[0]!;
    expect(held.closest("td")!.textContent).toContain("Waiting behind a parked mark");
    // The parked mark itself is not waiting behind anything.
    expect(screen.getByText("Jump 1.35 m · X — Finley Larsen").closest("td")!.textContent).not
      .toContain("Waiting behind");
  });

  it("sends a parked CARD correction to the deck rather than the typed-value editor", async () => {
    // Its payload is the whole series; the editor would requeue `jumps` beside a
    // typed value and the server refuses both shapes (ADR-0056 §2).
    await db.outbox.put({
      id: "c-9",
      kind: RESULT_CORRECT_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-17",
        entryId: "en-1",
        value: null,
        status: null,
        jumps: [{ height: 1.35, marks: "XO" }],
        reason: "Read back from the card",
        display: { entryDisplayName: "Finley Larsen", measureType: "Height" },
      },
      actorSubject: "official-1",
      capturedAt: "2026-08-13T09:50:00.000Z",
      status: "conflict",
      attempts: 1,
      baseVersion: 3,
    });
    renderWithQuery(<OutboxScreen />);
    expect(await screen.findByRole("button", { name: "Requeue" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Edit and requeue" })).toBeNull();
    expect(screen.getByText(/Correct this height/)).toBeTruthy();
  });
});
