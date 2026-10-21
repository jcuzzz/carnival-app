import { describe, expect, it } from "vitest";
import Dexie from "dexie";
import { db, type OutboxItem } from "./db";

/**
 * ADR-0051: a v7 device kept the whole response body. An accepted
 * `entry.create` therefore still holds the student's name (ADR-0031 sheds the
 * PAYLOAD's, not the ack's) and a `conflict` row is kept indefinitely, so
 * neither ages out. The v8 upgrade is what makes an EXISTING device narrow —
 * and it must not touch anything else about a row.
 */

const V7_STORES = {
  outbox: "id, status, capturedAt, [status+syncedAt], [status+resolvedAt]",
  syncMeta: "key",
  eventRosters: "carnivalEventId, fetchedAt",
  appSettings: "key",
};

const base = {
  actorSubject: "official-1",
  actorDisplayName: "Olive Official",
  capturedAt: "2026-08-14T09:50:00.000Z",
  attempts: 1,
};

const acceptedEntry = {
  ...base,
  id: "01920000-0000-7000-8000-00000000000a",
  kind: "entry.create",
  payload: { carnivalId: "c-1", carnivalEventId: "ev-1", studentId: "st-1" },
  status: "accepted",
  syncedAt: "2026-08-14T09:50:01.000Z",
  serverResponse: {
    id: "01920000-0000-7000-8000-00000000000a",
    carnivalEventId: "ev-1",
    studentId: "st-1",
    displayName: "Sam Swimmer",
    yearLevel: 8,
    houseCode: "WATTLE",
    ageGroup: "U13",
    lane: 4,
    heat: 1,
  },
};

const acceptedResult = {
  ...base,
  id: "01920000-0000-7000-8000-00000000000b",
  kind: "result.submit",
  payload: { carnivalId: "c-1", carnivalEventId: "ev-1", entryId: "en-1", value: "34.56" },
  status: "accepted",
  syncedAt: "2026-08-14T09:51:01.000Z",
  serverResponse: {
    id: "01920000-0000-7000-8000-00000000000b",
    currentRevision: 2,
    entryId: "en-1",
    measuredValue: "34.56",
    place: 1,
    lifecycleState: "Verified",
    recordedBySubject: "official-1",
  },
};

const acceptedMaterialise = {
  ...base,
  id: "01920000-0000-7000-8000-00000000000c",
  kind: "race.materialise",
  payload: { carnivalId: "c-1", eventId: "ev-13f" },
  status: "accepted",
  syncedAt: "2026-08-14T09:52:01.000Z",
  serverResponse: {
    race: {
      id: "01920000-0000-7000-8000-00000000000c",
      entries: [{ lane: 1, displayName: "Sam Swimmer", houseCode: "WATTLE" }],
    },
    warnings: ["This race holds 14 entries but this venue has 8 lanes (carnival settings)."],
  },
};

const parkedTeam = {
  ...base,
  id: "01920000-0000-7000-8000-00000000000d",
  kind: "team.create",
  payload: { carnivalId: "c-1", carnivalEventId: "ev-1", houseCode: "WATTLE", studentIds: ["st-1"] },
  status: "conflict",
  attempts: 3,
  lastError: "conflict: HTTP 409",
  serverResponse: {
    error: "TEAM_EXISTS",
    detail: "This house already has a team in this division.",
    context: { studentId: "st-1", displayName: "Sam Swimmer" },
  },
};

const queuedResult = {
  ...base,
  id: "01920000-0000-7000-8000-00000000000e",
  kind: "result.submit",
  payload: { carnivalId: "c-1", carnivalEventId: "ev-1", entryId: "en-2", value: "35.67" },
  status: "queued",
  attempts: 0,
};

const seeded = [acceptedEntry, acceptedResult, acceptedMaterialise, parkedTeam, queuedResult];

async function seedV7(): Promise<void> {
  const legacy = new Dexie("sports-platform");
  legacy.version(7).stores(V7_STORES);
  await legacy.open();
  await legacy.table("outbox").bulkPut(seeded);
  legacy.close();
}

/** Every key anywhere in the stored value, however deep. */
function keysDeep(value: unknown): string[] {
  if (Array.isArray(value)) return value.flatMap(keysDeep);
  if (value === null || typeof value !== "object") return [];
  return Object.entries(value as Record<string, unknown>).flatMap(([k, v]) => [k, ...keysDeep(v)]);
}

describe("the v8 upgrade", () => {
  it("narrows every stored ack in place, and moves nothing else about the rows", async () => {
    await seedV7();
    await db.open();

    const rows = await db.outbox.orderBy("id").toArray();
    expect(rows.map((r) => r.id)).toEqual(seeded.map((s) => s.id));
    expect(rows.map((r) => r.status)).toEqual(seeded.map((s) => s.status));
    for (const before of seeded) {
      const after = rows.find((r) => r.id === before.id)!;
      expect(after.payload).toEqual(before.payload);
      expect(after.kind).toBe(before.kind);
      expect(after.capturedAt).toBe(before.capturedAt);
      expect(after.attempts).toBe(before.attempts);
      expect(after.actorSubject).toBe(before.actorSubject);
      expect(after.syncedAt).toBe((before as { syncedAt?: string }).syncedAt);
    }

    const ackOf = async (id: string): Promise<OutboxItem["serverResponse"]> =>
      (await db.outbox.get(id))!.serverResponse;

    // The accepted entry.create's whole EntryDto — the student's name included.
    expect(await ackOf(acceptedEntry.id)).toBeUndefined();
    expect(await ackOf(acceptedResult.id)).toEqual({ id: acceptedResult.id, currentRevision: 2 });
    expect(await ackOf(acceptedMaterialise.id)).toEqual({
      warnings: ["This race holds 14 entries but this venue has 8 lanes (carnival settings)."],
    });
    // A conflict is kept forever, so its refusal is exactly the ADR-0011 pair.
    expect(await ackOf(parkedTeam.id)).toEqual({
      error: "TEAM_EXISTS",
      detail: "This house already has a team in this division.",
    });
    expect(await ackOf(queuedResult.id)).toBeUndefined();

    for (const row of rows) {
      for (const banned of ["displayName", "yearLevel", "studentId", "houseCode", "legs"]) {
        expect([row.id, keysDeep(row.serverResponse)]).toEqual([
          row.id,
          expect.not.arrayContaining([banned]),
        ]);
      }
    }
  });
});
