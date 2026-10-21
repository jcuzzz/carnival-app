import { beforeEach, describe, expect, it, vi } from "vitest";
import { db } from "../db/db";
import { enqueue, pendingInOrder, transition } from "../sync/outbox";
import { drainOnce } from "../sync/syncWorker";
import { _resetUuidv7StateForTests } from "../sync/uuidv7";
import { signIn, signOut } from "../auth/session";
import {
  ADJUSTMENT_CREATE_KIND,
  classifyResponse,
  createOutboxTransport,
  ENTRY_ASSIGN_KIND,
  ENTRY_CREATE_KIND,
  RACE_MATERIALISE_KIND,
  RACE_CREATE_KIND,
  RESULT_ATTEMPT_KIND,
  RESULT_CORRECT_KIND,
  RESULT_JUMP_KIND,
  RESULT_SUBMIT_KIND,
  TEAM_CREATE_KIND,
  type AdjustmentCreatePayload,
  type EntryAssignPayload,
  type EntryCreatePayload,
  type RaceMaterialisePayload,
  type RaceCreatePayload,
  type ResultAttemptPayload,
  type ResultCorrectPayload,
  type ResultJumpPayload,
  type ResultSubmitPayload,
  type TeamCreatePayload,
} from "./outboxTransport";
import * as outboxTransport from "./outboxTransport";
import { ACK_KINDS, narrowAck } from "../sync/ackShape";

/**
 * The transport bridges the EXISTING sync worker to the real API route:
 * POST /api/v1/carnivals/{c}/events/{e}/entries/{entry}/results with the
 * item's capture-time UUIDv7 as Idempotency-Key (ADR-0002).
 */

const payload: ResultSubmitPayload = {
  carnivalId: "c-1",
  carnivalEventId: "ev-1",
  entryId: "en-1",
  value: "1:23.45",
  status: null,
  display: { entryDisplayName: "Sam Swimmer" },
};

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

beforeEach(async () => {
  _resetUuidv7StateForTests();
  await db.outbox.clear();
  await db.syncMeta.clear();
  signOut();
});

describe("createOutboxTransport", () => {
  it("POSTs result.submit items to the real results route with the idempotency contract", async () => {
    signIn({ subject: "official-1", displayName: "O", roles: ["Recorder"] });
    const item = await enqueue({
      kind: RESULT_SUBMIT_KIND,
      payload,
      actorSubject: "official-1",
    });
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(201, { id: item.id, status: "Ok" }));

    const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
    expect(response.status).toBe(201);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    // Relative URL: production builds call the same origin they were served
    // from (single-Caddy routing); the dev loop's VITE_API_BASE prepends the
    // API origin.
    expect(url).toBe("/api/v1/carnivals/c-1/events/ev-1/entries/en-1/results");
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe(item.id); // UUIDv7 = result id
    expect(headers["X-Device-Id"]).toMatch(/^[0-9a-f-]{36}$/);
    expect(headers["X-Captured-Actor"]).toBe("official-1"); // capture-time actor
    expect(headers["X-Dev-Subject"]).toBe("official-1");
    expect(JSON.parse(init.body as string)).toEqual({
      value: "1:23.45",
      status: null,
      recordedAtUtc: item.capturedAt,
    });
  });

  it("re-spells the canonical client status for the wire (body contract unchanged)", async () => {
    // Client-side statuses carry the read casing ("Dns"); the API's request
    // body has always taken capitals and still does.
    const item = await enqueue({
      kind: RESULT_SUBMIT_KIND,
      payload: { ...payload, value: null, status: "Dns" },
      actorSubject: "official-1",
    });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, { id: item.id }));

    await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toEqual({
      value: null,
      status: "DNS",
      recordedAtUtc: item.capturedAt,
    });
  });

  it("sends the CAPTURE-TIME actor, not the sync-session user, after a device handover", async () => {
    // Item captured by the departing official…
    const item = await enqueue({
      kind: RESULT_SUBMIT_KIND,
      payload,
      actorSubject: "official-departed",
      actorDisplayName: "Dana Departed",
    });
    // …drained under the incoming official's session (ADR-0006 handover).
    signIn({ subject: "official-incoming", displayName: "Ines Incoming", roles: ["Recorder"] });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, { id: item.id }));

    await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const headers = init.headers as Record<string, string>;
    // Recorder = captured actor; session identity still authenticates the request.
    expect(headers["X-Captured-Actor"]).toBe("official-departed");
    expect(headers["X-Captured-Actor-Name"]).toBe(encodeURIComponent("Dana Departed"));
    expect(headers["X-Dev-Subject"]).toBe("official-incoming");
  });

  it("POSTs result.correct items to the corrections route with the captured baseVersion (U3)", async () => {
    const correctPayload: ResultCorrectPayload = {
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      resultId: "res-1",
      value: null,
      status: "Dq",
      reason: "Wrong swimmer",
      display: { entryDisplayName: "Sam Swimmer" },
    };
    const item = await enqueue({
      kind: RESULT_CORRECT_KIND,
      payload: correctPayload,
      actorSubject: "official-departed",
      actorDisplayName: "Dana Departed",
      baseVersion: 3,
    });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, { id: "res-1", currentRevision: 4 }));

    const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
    expect(response.status).toBe(200);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/results/res-1/correct");
    // A correction carries its capture-time actor exactly as a submission does.
    // Without this, a correction typed by Dana and drained after the next person
    // signs in is recorded against THEM (ADR-0002/0006: captured per item at
    // entry, never re-attributed).
    const correctHeaders = init.headers as Record<string, string>;
    expect(correctHeaders["X-Captured-Actor"]).toBe("official-departed");
    expect(correctHeaders["X-Captured-Actor-Name"]).toBe(encodeURIComponent("Dana Departed"));
    expect(JSON.parse(init.body as string)).toEqual({
      // The revision the official was looking at — NOT re-read at drain time.
      expectedRevision: 3,
      value: null,
      status: "DQ", // wire casing, same as a first capture
      reason: "Wrong swimmer",
    });
  });

  it("sends a queued correction's whole attempt series (ADR-0053 §4)", async () => {
    const item = await enqueue({
      kind: RESULT_CORRECT_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-1",
        entryId: "en-1",
        resultId: "res-1",
        value: null,
        status: null,
        attempts: [{ value: "4.15", foul: false }, { value: null, foul: true }, null],
        reason: "Tape misread at the pit",
        display: { entryDisplayName: "Sam Swimmer", measureType: "Distance" },
      } satisfies ResultCorrectPayload,
      actorSubject: "official-1",
      baseVersion: 2,
    });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, { id: "res-1", currentRevision: 3 }));

    await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    // Dropped, the drain would send a bare correction the server refuses with
    // ATTEMPTS_REQUIRED — and the mark typed offline would park for nothing.
    expect(JSON.parse(init.body as string)).toEqual({
      expectedRevision: 2,
      value: null,
      status: null,
      attempts: [{ value: "4.15", foul: false }, { value: null, foul: true }, null],
      reason: "Tape misread at the pit",
    });
  });

  it("PUTs result.attempt items to the ordinal route under the same idempotency contract", async () => {
    const attemptPayload: ResultAttemptPayload = {
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      ordinal: 2,
      value: "4.32",
      foul: false,
      display: { entryDisplayName: "Sam Swimmer", measureType: "Distance", attemptCount: 3 },
    };
    const item = await enqueue({
      kind: RESULT_ATTEMPT_KIND,
      payload: attemptPayload,
      actorSubject: "official-1",
      actorDisplayName: "Olive Official",
    });
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(200, { id: "res-1", currentRevision: 2 }));

    const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
    expect(response.status).toBe(200);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.method).toBe("PUT");
    expect(url).toBe("/api/v1/carnivals/c-1/events/ev-1/entries/en-1/attempts/2");
    const headers = init.headers as Record<string, string>;
    // The first attempt MINTS the result under this key, exactly as a submit does.
    expect(headers["Idempotency-Key"]).toBe(item.id);
    expect(headers["X-Captured-Actor"]).toBe("official-1");
    expect(headers["X-Captured-Actor-Name"]).toBe(encodeURIComponent("Olive Official"));
    expect(JSON.parse(init.body as string)).toEqual({
      value: "4.32",
      foul: false,
      recordedAtUtc: item.capturedAt,
    });
    // The ack is a result's, so a later correction can base itself on it.
    expect(response.body).toEqual({ id: "res-1", currentRevision: 2 });
  });

  it("PUTs result.jump to the height route, the mark and the capture time its body", async () => {
    const jumpPayload: ResultJumpPayload = {
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      height: 1.35,
      mark: "X",
      display: { entryDisplayName: "Finley Larsen", measureType: "Height" },
    };
    const item = await enqueue({
      kind: RESULT_JUMP_KIND,
      payload: jumpPayload,
      actorSubject: "official-1",
      actorDisplayName: "Olive Official",
    });
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(201, { id: "res-1", currentRevision: 1 }));

    const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(init.method).toBe("PUT");
    expect(url).toBe("/api/v1/carnivals/c-1/events/ev-1/entries/en-1/jumps/1.35");
    const headers = init.headers as Record<string, string>;
    // The FIRST mark mints the result under this key, exactly as an attempt does.
    expect(headers["Idempotency-Key"]).toBe(item.id);
    expect(headers["X-Captured-Actor"]).toBe("official-1");
    expect(headers["X-Captured-Actor-Name"]).toBe(encodeURIComponent("Olive Official"));
    expect(JSON.parse(init.body as string)).toEqual({
      mark: "X",
      recordedAtUtc: item.capturedAt,
    });
    expect(response.body).toEqual({ id: "res-1", currentRevision: 1 });
  });

  it("spells a whole-metre height as the payload holds it — the server compares by value", async () => {
    const item = await enqueue({
      kind: RESULT_JUMP_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-1",
        entryId: "en-1",
        height: 1.2,
        mark: "O",
      } satisfies ResultJumpPayload,
      actorSubject: "official-1",
    });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, { id: "res-1" }));

    await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);

    expect((fetchMock.mock.calls[0] as [string, RequestInit])[0]).toBe(
      "/api/v1/carnivals/c-1/events/ev-1/entries/en-1/jumps/1.2",
    );
  });

  it("parks a refused mark as a conflict — every ladder refusal is 409 (ADR-0056 §2)", async () => {
    for (const code of [
      "LADDER_REQUIRED",
      "HEIGHT_NOT_ON_LADDER",
      "BAR_NEVER_LOWERED",
      "ATHLETE_ELIMINATED",
      "HEIGHT_ALREADY_CLEARED",
      "HEIGHT_PASSED",
      "HEIGHT_ATTEMPTS_EXHAUSTED",
    ])
      expect([code, classifyResponse(409, { error: code })]).toEqual([code, "conflict"]);
  });

  it("sends the whole CARD on a correction of a laddered result", async () => {
    const item = await enqueue({
      kind: RESULT_CORRECT_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-1",
        entryId: "en-1",
        resultId: "res-1",
        value: null,
        status: null,
        jumps: [
          { height: 1.25, marks: "O" },
          { height: 1.3, marks: "XO" },
        ],
        reason: "Card read back from the official's sheet",
      } satisfies ResultCorrectPayload,
      actorSubject: "official-1",
      baseVersion: 3,
    });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, { id: "res-1" }));

    await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toEqual({
      expectedRevision: 3,
      value: null,
      status: null,
      jumps: [
        { height: 1.25, marks: "O" },
        { height: 1.3, marks: "XO" },
      ],
      reason: "Card read back from the official's sheet",
    });
  });

  it("sends a foul as the cell with no value", async () => {
    const item = await enqueue({
      kind: RESULT_ATTEMPT_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-1",
        entryId: "en-1",
        ordinal: 1,
        value: null,
        foul: true,
      } satisfies ResultAttemptPayload,
      actorSubject: "official-1",
    });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, { id: "res-1" }));

    await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toMatchObject({ value: null, foul: true });
  });

  it("parks a stale correction as a conflict, never a silent overwrite (409 CONCURRENCY_CONFLICT)", async () => {
    const item = await enqueue({
      kind: RESULT_CORRECT_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-1",
        entryId: "en-1",
        resultId: "res-1",
        value: "1:20.00",
        status: null,
        reason: "Mistyped",
        display: { entryDisplayName: "Sam Swimmer" },
      } satisfies ResultCorrectPayload,
      actorSubject: "official-1",
      baseVersion: 1,
    });
    const transport = createOutboxTransport(
      vi.fn().mockResolvedValue(
        jsonResponse(409, {
          error: "CONCURRENCY_CONFLICT",
          detail: "the result changed since it was read",
          currentRevision: 2,
        }),
      ) as unknown as typeof fetch,
    );

    const result = await drainOnce({ transport, isOnline: () => true });
    expect(result.conflicts).toBe(1);
    expect((await db.outbox.get(item.id))?.status).toBe("conflict");
  });

  it("POSTs entry.create items to the entries route with the client id as Idempotency-Key (ADR-0010)", async () => {
    signIn({ subject: "admin-1", displayName: "A", roles: ["CarnivalAdministrator"] });
    const entryPayload: EntryCreatePayload = {
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      studentId: "st-1",
      raceId: "r-2",
      display: { displayName: "Late Swimmer", yearLevel: 8, houseCode: "WATTLE", ageGroup: "U14" },
    };
    const item = await enqueue({
      kind: ENTRY_CREATE_KIND,
      payload: entryPayload,
      actorSubject: "admin-1",
    });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, { id: item.id, displayName: "Late Swimmer" }));

    const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
    expect(response.status).toBe(201);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/carnivals/c-1/events/ev-1/entries");
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe(item.id); // UUIDv7 = entry id
    expect(headers["X-Device-Id"]).toMatch(/^[0-9a-f-]{36}$/);
    expect(headers["X-Captured-Actor"]).toBe("admin-1"); // capture-time actor
    // Body is the same shape as an online direct create: the student and the
    // heat the marshal was standing at (ADR-0039 §2).
    expect(JSON.parse(init.body as string)).toEqual({ studentId: "st-1", raceId: "r-2" });
  });

  it("POSTs team.create items to the team-entries route on the same ADR-0010 shape", async () => {
    signIn({ subject: "marshal-1", displayName: "M", roles: ["Recorder"] });
    const teamPayload: TeamCreatePayload = {
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      houseCode: "BANKSIA",
      studentIds: ["st-1", "st-2", "st-3", "st-4"],
      raceId: "r-2",
      display: { ageGroup: "14", legs: [] },
    };
    const item = await enqueue({
      kind: TEAM_CREATE_KIND,
      payload: teamPayload,
      actorSubject: "marshal-1",
      actorDisplayName: "Marnie Marshal",
    });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, { id: item.id }));

    const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
    expect(response.status).toBe(201);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/carnivals/c-1/events/ev-1/team-entries");
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe(item.id); // UUIDv7 = team entry id
    expect(headers["X-Captured-Actor"]).toBe("marshal-1");
    expect(headers["X-Captured-Actor-Name"]).toBe("Marnie%20Marshal");
    // Leg ORDER is the contract; `display` is deck context and never sent.
    expect(JSON.parse(init.body as string)).toEqual({
      houseCode: "BANKSIA",
      studentIds: ["st-1", "st-2", "st-3", "st-4"],
      raceId: "r-2",
    });
  });

  it("sends raceId: null for a create that named no heat (an older queued item)", async () => {
    signIn({ subject: "admin-1", displayName: "A", roles: ["CarnivalAdministrator"] });
    const item = await enqueue({
      kind: ENTRY_CREATE_KIND,
      payload: { carnivalId: "c-1", carnivalEventId: "ev-1", studentId: "st-1" },
      actorSubject: "admin-1",
    });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, { id: item.id }));
    await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(JSON.parse(init.body as string)).toEqual({ studentId: "st-1", raceId: null });
  });

  it("sends the CAPTURE-TIME actor on an entry.create too, after a device handover", async () => {
    // The marshal who added the swimmer owns the entry, exactly as the recorder
    // who typed a time owns the result. Without this the create is attributed to
    // whoever signs in before the drain — and the entry's lane and heat decide
    // house points (ADR-0015), so the attribution is not cosmetic.
    const item = await enqueue({
      kind: ENTRY_CREATE_KIND,
      payload: {
        carnivalId: "c-1",
        carnivalEventId: "ev-1",
        studentId: "st-1",
        display: { displayName: "Late Swimmer", yearLevel: 8, houseCode: "WATTLE", ageGroup: "U14" },
      } satisfies EntryCreatePayload,
      actorSubject: "marshal-departed",
      actorDisplayName: "Dana Departed",
    });
    signIn({ subject: "official-incoming", displayName: "Ines Incoming", roles: ["Recorder"] });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, { id: item.id }));

    await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    const headers = init.headers as Record<string, string>;
    expect(headers["X-Captured-Actor"]).toBe("marshal-departed");
    expect(headers["X-Captured-Actor-Name"]).toBe(encodeURIComponent("Dana Departed"));
    expect(headers["X-Dev-Subject"]).toBe("official-incoming"); // session still authenticates
  });

  it("POSTs adjustment.create items to the adjustments route with the capture-time actor (ADR-0005)", async () => {
    const adjPayload: AdjustmentCreatePayload = {
      carnivalId: "c-1",
      houseCode: "WATTLE",
      points: 10,
      category: "Best Dressed House",
      reason: "Great costumes",
    };
    const item = await enqueue({
      kind: ADJUSTMENT_CREATE_KIND,
      payload: adjPayload,
      actorSubject: "admin-departed",
      actorDisplayName: "Dana Departed",
    });
    signIn({ subject: "admin-incoming", displayName: "Ines", roles: ["CarnivalAdministrator"] });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, { id: item.id, houseCode: "WATTLE" }));

    const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
    expect(response.status).toBe(201);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/carnivals/c-1/adjustments");
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe(item.id); // UUIDv7 = adjustment id
    expect(headers["X-Captured-Actor"]).toBe("admin-departed"); // recorder, not sync session
    expect(JSON.parse(init.body as string)).toEqual({
      houseCode: "WATTLE",
      points: 10,
      category: "Best Dressed House",
      reason: "Great costumes",
    });
  });

  it("POSTs race.create items to the races route with the client id as Idempotency-Key (ADR-0015)", async () => {
    const racePayload: RaceCreatePayload = {
      carnivalId: "c-1",
      scheduleSlotId: "slot-1",
      label: "Race 14",
    };
    const item = await enqueue({
      kind: RACE_CREATE_KIND,
      payload: racePayload,
      actorSubject: "admin-1",
    });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, { id: item.id, label: "Race 14" }));

    const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
    expect(response.status).toBe(201);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/carnivals/c-1/races");
    expect(init.method).toBe("POST");
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe(item.id); // UUIDv7 = race id
    expect(headers["X-Captured-Actor"]).toBe("admin-1");
    expect(JSON.parse(init.body as string)).toEqual({
      scheduleSlotId: "slot-1",
      sortOrder: null,
      label: "Race 14",
    });
  });

  it("POSTs race.materialise to the materialise route, keyed on the client race id (ADR-0019)", async () => {
    const materialisePayload: RaceMaterialisePayload = {
      carnivalId: "c-1",
      eventId: "ev-13f",
      scheduleSlotId: "slot-1",
      display: { title: "13 · F" },
    };
    const item = await enqueue({
      kind: RACE_MATERIALISE_KIND,
      payload: materialisePayload,
      actorSubject: "marshal-1",
      actorDisplayName: "Mia Marshal",
    });
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(201, { race: { id: item.id }, warnings: [] }));

    const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
    expect(response.status).toBe(201);
    expect(response.outcome).toBe("accepted");

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/carnivals/c-1/races/materialise");
    expect(init.method).toBe("POST");
    const headers = init.headers as Record<string, string>;
    // The item id IS the race id, so a redrain mints one race for one human act.
    expect(headers["Idempotency-Key"]).toBe(item.id);
    expect(headers["X-Captured-Actor"]).toBe("marshal-1");
    expect(headers["X-Captured-Actor-Name"]).toBe("Mia%20Marshal");
    // `display` is context for the outbox screen and never reaches the server.
    expect(JSON.parse(init.body as string)).toEqual({
      eventId: "ev-13f",
      scheduleSlotId: "slot-1",
      label: null,
    });
  });

  it("parks a queued materialisation whose carnival or event vanished, rather than rejecting it", async () => {
    // 404 falls to `rejected` on status alone, which is the one-way door. A
    // marshal's minted race is recoverable work: it is held for a person.
    const item = await enqueue({
      kind: RACE_MATERIALISE_KIND,
      payload: {
        carnivalId: "c-1",
        eventId: "ev-13f",
        display: { title: "13 · F" },
      } satisfies RaceMaterialisePayload,
      actorSubject: "marshal-1",
    });
    const transport = createOutboxTransport(
      vi.fn().mockResolvedValue(
        jsonResponse(404, { error: "EVENT_NOT_FOUND", detail: "No such event." }),
      ) as unknown as typeof fetch,
    );
    expect((await transport(item)).outcome).toBe("conflict");

    // …while an unrelated 404 on the same route family keeps the ordinary verdict.
    const stillRejected = createOutboxTransport(
      vi.fn().mockResolvedValue(
        jsonResponse(404, { error: "SOMETHING_ELSE", detail: "Nope." }),
      ) as unknown as typeof fetch,
    );
    expect((await stillRejected(item)).outcome).toBe("rejected");
  });

  it("keeps a materialisation QUEUED while the carnival is not open yet", () => {
    // The capture family (ADR-0018 §6 gates materialisation exactly as placement
    // is gated): the same request lands once an administrator opens the carnival.
    expect(classifyResponse(409, { error: "CARNIVAL_NOT_OPEN" })).toBe("retry");
    expect(classifyResponse(409, { error: "EVENT_NOT_OPEN" })).toBe("retry");
  });

  it("PUTs entry.assign items to the placement route with the CAPTURE-TIME marshal (ADR-0015 §5)", async () => {
    // The heat this write assigns decides house points, so the marshal who
    // tapped the lane must survive a device handover (ADR-0002/0006) — never
    // re-attributed to whoever is signed in when the queue drains.
    const assignPayload: EntryAssignPayload = {
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      raceId: "race-1",
      lane: 4,
      heat: null, // the ordinary case: derived server-side
      display: { entryDisplayName: "Sam Swimmer" },
    };
    const item = await enqueue({
      kind: ENTRY_ASSIGN_KIND,
      payload: assignPayload,
      actorSubject: "marshal-departed",
      actorDisplayName: "Dana Departed",
    });
    signIn({ subject: "marshal-incoming", displayName: "Ines", roles: ["Recorder"] });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, { id: "en-1", heat: 1, lane: 4 }));

    const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
    expect(response.status).toBe(200);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/carnivals/c-1/events/ev-1/entries/en-1/placement");
    expect(init.method).toBe("PUT");
    const headers = init.headers as Record<string, string>;
    expect(headers["Idempotency-Key"]).toBe(item.id); // dedups a replayed drain
    expect(headers["X-Captured-Actor"]).toBe("marshal-departed");
    expect(headers["X-Dev-Subject"]).toBe("marshal-incoming");
    expect(JSON.parse(init.body as string)).toEqual({ raceId: "race-1", lane: 4, heat: null });
  });

  it("keeps a placement's ENTRY_NOT_FOUND recoverable — its entry.create may still land", async () => {
    // Dependency ordering: entry.create sorts before the placement that targets
    // it, so a placement can reach the server while its entry is still parked as
    // a conflict on this device. `rejected` is a one-way door (sync/outbox.ts
    // TRANSITIONS), so the marshalled lane would be destroyed by a 404 that a
    // person can still fix. Scoped to the kinds that NAME an entry, not put in
    // CONFLICT_CODES, so `classifyResponse` keeps refusing the code by class.
    const assignPayload: EntryAssignPayload = {
      carnivalId: "c-1",
      carnivalEventId: "ev-1",
      entryId: "en-1",
      raceId: "race-1",
      lane: 4,
      heat: null,
      display: { entryDisplayName: "Sam Swimmer" },
    };
    const item = await enqueue({
      kind: ENTRY_ASSIGN_KIND,
      payload: assignPayload,
      actorSubject: "marshal-1",
    });
    const fetchMock = vi
      .fn()
      .mockResolvedValue(jsonResponse(404, { error: "ENTRY_NOT_FOUND", detail: "…" }));

    const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
    expect(response.status).toBe(404);
    expect(response.outcome).toBe("conflict");
  });

  it("keeps a result's ENTRY_NOT_FOUND recoverable on every capture kind", async () => {
    // A swum time behind a parked entry.create used to 404 into `rejected`,
    // whose only exit is `discarded` — the capture was destroyed by the order
    // it drained in.
    const kinds = [
      { kind: RESULT_SUBMIT_KIND, body: payload as unknown },
      {
        kind: RESULT_ATTEMPT_KIND,
        body: { ...payload, ordinal: 2, foul: false } as unknown,
      },
      { kind: RESULT_JUMP_KIND, body: { ...payload, height: 1.4, mark: "O" } as unknown },
    ];
    for (const { kind, body } of kinds) {
      const item = await enqueue({ kind, payload: body, actorSubject: "official-1" });
      const fetchMock = vi
        .fn()
        .mockResolvedValue(jsonResponse(404, { error: "ENTRY_NOT_FOUND", detail: "…" }));

      const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
      expect(response.outcome, kind).toBe("conflict");
    }
  });

  it("refuses unknown kinds locally as a definitive 400 (→ rejected, kept for audit)", async () => {
    const item = await enqueue({ kind: "mystery.kind", payload: {}, actorSubject: "a" });
    const fetchMock = vi.fn();
    const response = await createOutboxTransport(fetchMock as unknown as typeof fetch)(item);
    expect(response.status).toBe(400);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("drains through the existing worker: 201 → accepted with the narrowed ack stored", async () => {
    const item = await enqueue({ kind: RESULT_SUBMIT_KIND, payload, actorSubject: "official-1" });
    const transport = createOutboxTransport(
      vi.fn().mockResolvedValue(jsonResponse(201, resultDto(item.id))) as unknown as typeof fetch,
    );

    const result = await drainOnce({ transport, isOnline: () => true });
    expect(result).toMatchObject({ stopReason: "drained", accepted: 1 });
    const stored = await db.outbox.get(item.id);
    expect(stored?.status).toBe("accepted");
    expect(stored?.serverResponse).toEqual({ id: item.id, currentRevision: 3 });
  });

  it("keeps items queued when the API is unreachable (offline-first path)", async () => {
    const item = await enqueue({ kind: RESULT_SUBMIT_KIND, payload, actorSubject: "official-1" });
    const transport = createOutboxTransport(
      vi.fn().mockRejectedValue(new TypeError("fetch failed")) as unknown as typeof fetch,
    );

    const result = await drainOnce({ transport, isOnline: () => true });
    expect(result.stopReason).toBe("network-error");
    const pending = await pendingInOrder();
    expect(pending.map((p) => p.id)).toEqual([item.id]);
  });

  it("maps a 409 (duplicate/conflicting result) to conflict for an administrator to resolve", async () => {
    const item = await enqueue({ kind: RESULT_SUBMIT_KIND, payload, actorSubject: "official-1" });
    const transport = createOutboxTransport(
      vi.fn().mockResolvedValue(jsonResponse(409, { error: "This entry already has a result." })) as unknown as typeof fetch,
    );

    const result = await drainOnce({ transport, isOnline: () => true });
    expect(result.conflicts).toBe(1);
    const stored = await db.outbox.get(item.id);
    expect(stored?.status).toBe("conflict");
  });
});

/**
 * K1: what happens to a queued capture is decided by the ADR-0011 machine code,
 * because the status alone cannot separate "not open YET" from "two officials
 * recorded this swimmer" — both are 409, and one of them must never be parked
 * for an administrator while the other must never be retried into the void.
 */
describe("classifyResponse", () => {
  it("retries a state-dependent refusal, whatever its status", () => {
    // C1: the server no longer burns the idempotency key on these, so the same
    // payload lands once an admin opens the event / promotes the roster.
    expect(classifyResponse(409, { error: "EVENT_NOT_OPEN", detail: "…" })).toBe("retry");
    expect(classifyResponse(409, { error: "CARNIVAL_NOT_OPEN", detail: "…" })).toBe("retry");
    expect(classifyResponse(422, { error: "ROSTER_SNAPSHOT_MISSING", detail: "…" })).toBe("retry");
    expect(classifyResponse(403, { error: "NOT_ASSIGNED_OFFICIAL", detail: "…" })).toBe("retry");
    expect(classifyResponse(429, { error: "TOO_MANY_REQUESTS", detail: "…" })).toBe("retry");
  });

  it("parks a concurrency clash or duplicate capture as a conflict", () => {
    expect(classifyResponse(409, { error: "CONCURRENCY_CONFLICT", detail: "…" })).toBe("conflict");
    expect(classifyResponse(409, { error: "RESULT_EXISTS", detail: "…" })).toBe("conflict");
  });

  it("parks every attempts refusal for a person, never on the rejected pile (ADR-0053 §3)", () => {
    // None of these clears by retrying, and all of them hold a real typed mark.
    for (const error of [
      "ATTEMPTS_NOT_CONFIGURED",
      "ATTEMPT_ORDINAL_OUT_OF_RANGE",
      "ATTEMPT_ALREADY_RECORDED",
      "RESULT_STATUS_BLOCKS_ATTEMPTS",
      "ATTEMPTS_REQUIRED",
    ])
      expect([error, classifyResponse(409, { error, detail: "…" })]).toEqual([error, "conflict"]);
  });

  it("parks validation refusals as conflicts — an operator must see the payload", () => {
    // A 400 would otherwise be rejected: a terminal pile nobody opens, holding
    // a time that was really swum.
    expect(classifyResponse(400, { error: "RESULT_VALUE_INVALID", detail: "…" })).toBe("conflict");
    expect(classifyResponse(400, { error: "VALIDATION_FAILED", detail: "…" })).toBe("conflict");
  });

  /**
   * The request-shape codes (400 REQUEST_INVALID, 413 REQUEST_BODY_TOO_LARGE).
   * Both would fall through `outcomeFromStatus` to `rejected` — a TERMINAL
   * state with no transition back to `queued` (sync/outbox.ts) — which would
   * destroy a time really swum. They are parked instead, like the rest of the
   * validation family.
   */
  it("parks an unreadable body as a conflict, not on the terminal rejected pile", () => {
    expect(classifyResponse(400, { error: "REQUEST_INVALID", detail: "…" })).toBe("conflict");
    // Never retry: the same bytes never parse, and retry stops the drain — one
    // unreadable item would head-of-line block every later capture.
    expect(classifyResponse(400, { error: "REQUEST_INVALID", detail: "…" })).not.toBe("retry");
  });

  it("parks an oversized body as a conflict — the size limit is a fixable ops setting", () => {
    // 413 is terminal for these bytes but not for this capture: raise the
    // server/proxy limit and the very same queued item lands.
    expect(classifyResponse(413, { error: "REQUEST_BODY_TOO_LARGE", detail: "…" })).toBe(
      "conflict",
    );
    // Without the code, 413 is just "other 4xx" → rejected. The code is the
    // whole difference, which is why it must stay in CONFLICT_CODES.
    expect(classifyResponse(413, undefined)).toBe("rejected");
  });

  it("parks an unparseable lifecycle status like its result-status sibling, not on the rejected pile", () => {
    // UNKNOWN_STATUS and UNKNOWN_RESULT_STATUS are one validation family and
    // must not take different outcomes; unlisted, the 400 falls through to
    // terminal `rejected`.
    expect(classifyResponse(400, { error: "UNKNOWN_STATUS", detail: "…" })).toBe("conflict");
    expect(classifyResponse(400, { error: "UNKNOWN_RESULT_STATUS", detail: "…" })).toBe("conflict");
  });

  it("parks a relay whose house is gone; rejects an individual in a team event", () => {
    expect(classifyResponse(400, { error: "HOUSE_UNKNOWN", detail: "…" })).toBe("conflict");
    expect(classifyResponse(400, { error: "NOT_AN_INDIVIDUAL_EVENT", detail: "…" })).toBe(
      "rejected",
    );
  });

  it("parks NM typed on a time event rather than rejecting the capture (ADR-0052 §4)", () => {
    // The mark is real: either the deck held a stale measure or the wrong row
    // was typed into. Unlisted, the 400 would fall through to terminal rejected.
    expect(classifyResponse(400, { error: "NM_REQUIRES_FIELD_MEASURE", detail: "…" })).toBe(
      "conflict",
    );
    expect(classifyResponse(400, { error: "NM_REQUIRES_FIELD_MEASURE", detail: "…" })).not.toBe(
      "rejected",
    );
  });

  it("retries a missing idempotency-buffering pipeline — a config fault an ops fix clears", () => {
    expect(classifyResponse(500, { error: "IDEMPOTENCY_BUFFERING_UNAVAILABLE", detail: "…" })).toBe(
      "retry",
    );
    // Never parked: a server misconfiguration would otherwise strand every
    // capture on the device in a queue a person has to unpick by hand.
    expect(
      classifyResponse(500, { error: "IDEMPOTENCY_BUFFERING_UNAVAILABLE", detail: "…" }),
    ).not.toBe("conflict");
  });

  it("parks a dual-listed student's second entry for a person (ADR-0023)", () => {
    // Every student is eligible in their own category's division and in Open
    // (ADR-0026 §1), and may hold one entry across them. The sibling never
    // clears itself, so a retry would head-of-line block every later capture —
    // but the capture is real: remove whichever entry is wrong and it re-queues.
    expect(classifyResponse(409, { error: "ENTRY_IN_OTHER_DIVISION", detail: "…" })).toBe(
      "conflict",
    );
    expect(classifyResponse(409, { error: "ENTRY_IN_OTHER_DIVISION", detail: "…" })).not.toBe(
      "retry",
    );
  });

  it("parks an add whose heat is not this event's (ADR-0039 §2), never retries it", () => {
    // 422: the create named a race the event does not hold — a deleted heat, or
    // another division's. The entry is real, so it waits for a person.
    expect(classifyResponse(422, { error: "RACE_NOT_IN_EVENT", detail: "…" })).toBe("conflict");
    expect(classifyResponse(422, { error: "RACE_NOT_IN_EVENT", detail: "…" })).not.toBe("retry");
    expect(classifyResponse(422, { error: "RACE_NOT_IN_EVENT", detail: "…" })).not.toBe("rejected");
  });

  it("parks marshalling refusals for a person (ADR-0015): none of them resolve themselves", () => {
    // A taken lane never frees itself, an ambiguous heat never disambiguates
    // itself — and a retry would head-of-line block every later capture.
    expect(classifyResponse(409, { error: "LANE_TAKEN", detail: "…" })).toBe("conflict");
    expect(classifyResponse(422, { error: "HEAT_AMBIGUOUS", detail: "…" })).toBe("conflict");
    expect(classifyResponse(400, { error: "ENTRY_LANE_INVALID", detail: "…" })).toBe("conflict");
    expect(classifyResponse(400, { error: "ENTRY_HEAT_INVALID", detail: "…" })).toBe("conflict");
    // The race was deleted while the assignment sat in the queue: a 404 would
    // fall through to the terminal rejected pile, losing a real capture.
    expect(classifyResponse(404, { error: "RACE_NOT_FOUND", detail: "…" })).toBe("conflict");
  });

  it("parks a queued race.create whose program slot was deleted under it", () => {
    // The schedule is advisory, so deleting a slot is an ordinary organiser
    // action — but rejecting the queued race would destroy the thing a whole
    // marshalled field's queued assignments depend on.
    expect(classifyResponse(404, { error: "SCHEDULE_SLOT_NOT_FOUND", detail: "…" })).toBe(
      "conflict",
    );
  });

  it("still refuses definitively when the code says the request can never land", () => {
    expect(classifyResponse(403, { error: "FORBIDDEN", detail: "…" })).toBe("rejected");
    expect(classifyResponse(404, { error: "ENTRY_NOT_FOUND", detail: "…" })).toBe("rejected");
  });

  it("pauses on 401 and accepts 2xx regardless of any code", () => {
    expect(classifyResponse(401, { error: "AUTHENTICATION_REQUIRED", detail: "…" })).toBe("pause");
    expect(classifyResponse(201, { id: "r-1" })).toBe("accepted");
  });

  // ADR-0011 transition note: anything without a code behaves exactly as the
  // worker always behaved.
  it("falls back to the status rule for pre-contract bodies", () => {
    expect(classifyResponse(409, { error: "This entry already has a result." })).toBe("conflict");
    expect(classifyResponse(400, { title: "Bad Request" })).toBe("rejected");
    expect(classifyResponse(422, undefined)).toBe("conflict");
    expect(classifyResponse(503, undefined)).toBe("retry");
    expect(classifyResponse(429, undefined)).toBe("retry");
  });
});

describe("drain outcomes per code class", () => {
  async function drainWith(status: number, body: unknown) {
    const item = await enqueue({ kind: RESULT_SUBMIT_KIND, payload, actorSubject: "official-1" });
    const transport = createOutboxTransport(
      vi.fn().mockResolvedValue(jsonResponse(status, body)) as unknown as typeof fetch,
    );
    const result = await drainOnce({ transport, isOnline: () => true });
    return { result, stored: await db.outbox.get(item.id) };
  }

  it("keeps a not-yet-open event queued and does not blame the uplink", async () => {
    const { result, stored } = await drainWith(409, {
      error: "EVENT_NOT_OPEN",
      detail: "The event is not open for result capture.",
    });
    expect(stored?.status).toBe("queued");
    expect(result.conflicts).toBe(0);
    // NOT "network-error": the server answered, so U5 must not report it offline.
    expect(result.stopReason).toBe("retry-later");
  });

  it("keeps a rate-limited capture queued, reads Retry-After, and does not blame the uplink", async () => {
    const item = await enqueue({ kind: RESULT_SUBMIT_KIND, payload, actorSubject: "official-1" });
    const throttled = new Response(
      JSON.stringify({ error: "TOO_MANY_REQUESTS", detail: "Too many requests." }),
      { status: 429, headers: { "Content-Type": "application/json", "Retry-After": "42" } },
    );
    const transport = createOutboxTransport(
      vi.fn().mockResolvedValue(throttled) as unknown as typeof fetch,
    );

    const result = await drainOnce({ transport, isOnline: () => true });

    const stored = await db.outbox.get(item.id);
    expect(stored?.status).toBe("queued");
    expect(stored?.lastError).toBe("throttled: HTTP 429");
    expect(result.rejected).toBe(0);
    expect(result.stopReason).toBe("throttled");
    expect(result.retryAfterMs).toBe(42_000);
  });

  it("parks a refused value for a person instead of rejecting it", async () => {
    const { result, stored } = await drainWith(400, {
      error: "RESULT_VALUE_INVALID",
      detail: "The time did not parse.",
    });
    expect(stored?.status).toBe("conflict");
    expect(result.rejected).toBe(0);
  });

  it("keeps an unreadable capture recoverable: conflict, payload intact, re-queueable", async () => {
    const { result, stored } = await drainWith(400, {
      error: "REQUEST_INVALID",
      detail: "Check that the body is valid JSON and matches the documented shape.",
    });
    expect(stored?.status).toBe("conflict");
    expect(result.rejected).toBe(0);
    expect(result.conflicts).toBe(1);
    // The captured time survives, and conflict -> queued is a legal transition
    // (rejected is terminal), so a resolved item can still be delivered.
    expect((stored?.payload as ResultSubmitPayload).value).toBe("1:23.45");
    await expect(transition(stored!.id, "queued")).resolves.toMatchObject({ status: "queued" });
  });

  it("keeps an oversized capture recoverable rather than discarding it (413)", async () => {
    const { result, stored } = await drainWith(413, {
      error: "REQUEST_BODY_TOO_LARGE",
      detail: "The request body exceeded the maximum accepted size.",
    });
    expect(stored?.status).toBe("conflict");
    expect(result.rejected).toBe(0);
    // The drain does NOT stop: 413 is one item's problem, not the uplink's.
    expect(result.stopReason).toBe("drained");
  });
});

/**
 * ADR-0051: what a row is allowed to keep of the server's answer. The bodies
 * below are the real DTO shapes (EntryEndpoints/ResultEndpoints/RaceEndpoints),
 * because the point of the rule is the student data they carry.
 */

function resultDto(id: string): Record<string, unknown> {
  return {
    id,
    currentRevision: 3,
    entryId: "en-1",
    status: "Ok",
    measuredValue: "34.56",
    place: 2,
    lifecycleState: "Verified",
    recordedBySubject: "official-1",
    recordedAtUtc: "2026-08-14T09:50:00.000Z",
  };
}

function entryDto(id: string): Record<string, unknown> {
  return {
    id,
    carnivalEventId: "ev-1",
    studentId: "st-1",
    displayName: "Sam Swimmer",
    yearLevel: 8,
    houseCode: "WATTLE",
    ageGroup: "U13",
    raceId: "r-1",
    lane: 4,
    heat: 1,
  };
}

function teamDto(id: string): Record<string, unknown> {
  return {
    ...entryDto(id),
    displayName: "WATTLE Relay",
    legs: [
      { studentId: "st-1", displayName: "Sam Swimmer", yearLevel: 8, stroke: "Free" },
      { studentId: "st-2", displayName: "Nina Novak", yearLevel: 8, stroke: "Free" },
    ],
  };
}

function materialiseResponse(id: string): Record<string, unknown> {
  return {
    race: {
      id,
      label: "13 · F",
      entries: [
        { lane: 1, displayName: "Sam Swimmer", houseCode: "WATTLE" },
        { lane: 2, displayName: "Nina Novak", houseCode: "KIN" },
      ],
    },
    warnings: ["This race holds 14 entries but this venue has 8 lanes (carnival settings)."],
  };
}

/** Every key anywhere in the stored value, however deep. */
function keysDeep(value: unknown): string[] {
  if (Array.isArray(value)) return value.flatMap(keysDeep);
  if (value === null || typeof value !== "object") return [];
  return Object.entries(value as Record<string, unknown>).flatMap(([k, v]) => [k, ...keysDeep(v)]);
}

const STUDENT_KEYS = ["displayName", "yearLevel", "studentId", "houseCode", "legs"];

// Read off the module rather than hand-listed: a kind added to the transport
// without an ack rule has to fail the completeness test below.
const SENDABLE_KINDS = Object.entries(outboxTransport).flatMap(([name, value]) =>
  name.endsWith("_KIND") && typeof value === "string" ? [value] : [],
);

const narrowEntryPayload: EntryCreatePayload = {
  carnivalId: "c-1",
  carnivalEventId: "ev-1",
  studentId: "st-1",
  raceId: "r-2",
  display: { displayName: "Sam Swimmer", yearLevel: 8, houseCode: "WATTLE", ageGroup: "U13" },
};

const narrowMaterialisePayload: RaceMaterialisePayload = {
  carnivalId: "c-1",
  eventId: "ev-13f",
  scheduleSlotId: "slot-1",
  display: { title: "13 · F" },
};

describe("narrowAck — what each kind keeps of a 2xx ack", () => {
  it("the four result kinds keep the id and the revision, nothing else", () => {
    for (const kind of [
      RESULT_SUBMIT_KIND,
      RESULT_CORRECT_KIND,
      RESULT_ATTEMPT_KIND,
      RESULT_JUMP_KIND,
    ]) {
      expect(narrowAck(kind, 201, resultDto("res-1"))).toEqual({ id: "res-1", currentRevision: 3 });
    }
  });

  it("race.materialise keeps the warnings and drops the race (a name per lane)", () => {
    const ack = narrowAck(RACE_MATERIALISE_KIND, 201, materialiseResponse("r-1"));
    expect(ack).toEqual({
      warnings: ["This race holds 14 entries but this venue has 8 lanes (carnival settings)."],
    });
  });

  it("the five non-result kinds keep nothing at all", () => {
    expect(narrowAck(ENTRY_CREATE_KIND, 201, entryDto("en-1"))).toBeUndefined();
    expect(narrowAck(TEAM_CREATE_KIND, 201, teamDto("tm-1"))).toBeUndefined();
    expect(narrowAck(ENTRY_ASSIGN_KIND, 200, entryDto("en-1"))).toBeUndefined();
    expect(narrowAck(RACE_CREATE_KIND, 201, { id: "r-1", label: "13 · F" })).toBeUndefined();
    expect(
      narrowAck(ADJUSTMENT_CREATE_KIND, 201, { id: "a-1", houseCode: "WATTLE", points: 5 }),
    ).toBeUndefined();
  });

  it("keeps no student key from ANY kind's 2xx ack", () => {
    const bodies: [string, Record<string, unknown>][] = [
      [RESULT_SUBMIT_KIND, resultDto("res-1")],
      [RESULT_CORRECT_KIND, resultDto("res-1")],
      [RESULT_ATTEMPT_KIND, resultDto("res-1")],
      [RESULT_JUMP_KIND, resultDto("res-1")],
      [ENTRY_CREATE_KIND, entryDto("en-1")],
      [TEAM_CREATE_KIND, teamDto("tm-1")],
      [ENTRY_ASSIGN_KIND, entryDto("en-1")],
      [RACE_CREATE_KIND, { id: "r-1", label: "13 · F" }],
      [RACE_MATERIALISE_KIND, materialiseResponse("r-1")],
      [ADJUSTMENT_CREATE_KIND, { id: "a-1", houseCode: "WATTLE", points: 5, reason: "Best team" }],
    ];
    for (const [kind, body] of bodies) {
      const kept = keysDeep(narrowAck(kind, 201, body)).filter((k) => STUDENT_KEYS.includes(k));
      expect([kind, kept]).toEqual([kind, []]);
    }
  });

  it("cuts a refusal to the two ADR-0011 contract fields, whatever the kind", () => {
    const refusal = {
      error: "STUDENT_NOT_ELIGIBLE",
      detail: "Sam Swimmer is not in this division.",
      context: { studentId: "st-1", displayName: "Sam Swimmer" },
    };
    for (const kind of ACK_KINDS) {
      expect(narrowAck(kind, 409, refusal)).toEqual({
        error: "STUDENT_NOT_ELIGIBLE",
        detail: "Sam Swimmer is not in this division.",
      });
    }
    // Even a kind with no rule at all: a refusal is readable copy, not a DTO.
    expect(narrowAck("mystery.kind", 400, refusal)).toMatchObject({ error: "STUDENT_NOT_ELIGIBLE" });
  });

  it("has a rule for every kind the transport can send, and keeps nothing for one it cannot", () => {
    expect(SENDABLE_KINDS.length).toBeGreaterThan(0);
    expect([...ACK_KINDS].sort()).toEqual([...SENDABLE_KINDS].sort());
    expect(narrowAck("mystery.kind", 200, resultDto("res-1"))).toBeUndefined();
  });

  it("keeps nothing from a body it cannot read", () => {
    expect(narrowAck(RESULT_SUBMIT_KIND, 201, undefined)).toBeUndefined();
    expect(narrowAck(RESULT_SUBMIT_KIND, 201, "created")).toBeUndefined();
    expect(narrowAck(RACE_MATERIALISE_KIND, 201, { race: { id: "r-1" } })).toBeUndefined();
    expect(narrowAck(RESULT_SUBMIT_KIND, 409, { serverVersion: 5 })).toBeUndefined();
  });
});

describe("the transport narrows before the worker stores", () => {
  it("stores nothing for an accepted entry.create, however fat the ack", async () => {
    const item = await enqueue({
      kind: ENTRY_CREATE_KIND,
      payload: narrowEntryPayload,
      actorSubject: "official-1",
    });
    const transport = createOutboxTransport(
      vi.fn().mockResolvedValue(jsonResponse(201, entryDto(item.id))) as unknown as typeof fetch,
    );

    await drainOnce({ transport, isOnline: () => true });
    const stored = await db.outbox.get(item.id);
    expect(stored?.status).toBe("accepted");
    expect(stored?.serverResponse).toBeUndefined();
  });

  it("stores a materialise ack's warnings without the lane-by-lane names", async () => {
    const item = await enqueue({
      kind: RACE_MATERIALISE_KIND,
      payload: narrowMaterialisePayload,
      actorSubject: "official-1",
    });
    const fat = jsonResponse(201, materialiseResponse(item.id));
    const transport = createOutboxTransport(
      vi.fn().mockResolvedValue(fat) as unknown as typeof fetch,
    );

    await drainOnce({ transport, isOnline: () => true });
    const stored = await db.outbox.get(item.id);
    expect(keysDeep(stored?.serverResponse)).toEqual(["warnings"]);
  });

  it("stores a refusal as {error, detail}, and the verdict still reads the full body", async () => {
    const item = await enqueue({ kind: RESULT_SUBMIT_KIND, payload, actorSubject: "official-1" });
    const transport = createOutboxTransport(
      vi.fn().mockResolvedValue(
        jsonResponse(409, {
          error: "CONCURRENCY_CONFLICT",
          detail: "Revision moved on.",
          context: { currentRevision: 4, recordedBySubject: "official-2" },
        }),
      ) as unknown as typeof fetch,
    );

    const result = await drainOnce({ transport, isOnline: () => true });
    expect(result.conflicts).toBe(1);
    const stored = await db.outbox.get(item.id);
    expect(stored?.status).toBe("conflict");
    expect(stored?.serverResponse).toEqual({
      error: "CONCURRENCY_CONFLICT",
      detail: "Revision moved on.",
    });
  });
});
