import { describe, expect, it } from "vitest";
import Dexie from "dexie";
import { db, type OutboxItem } from "./db";

/**
 * ADR-0026 §2 says the category is not cached on a device. That holds for new
 * writes only: a roster row already in IndexedDB still carries one per student,
 * and a parked `entry.create` still carries `display.category` — and ADR-0013
 * keeps a `conflict` item indefinitely, so it would never age out. The v6
 * upgrade is what makes an EXISTING device clean.
 */

const V5_STORES = {
  outbox: "id, status, capturedAt, [status+syncedAt], [status+resolvedAt]",
  syncMeta: "key",
  eventRosters: "carnivalEventId, fetchedAt",
};

const parked: OutboxItem = {
  id: "01920000-0000-7000-8000-00000000000a",
  kind: "entry.create",
  payload: {
    carnivalId: "c-1",
    carnivalEventId: "ev-1",
    studentId: "st-a",
    display: {
      displayName: "Ana Aardvark",
      yearLevel: 7,
      houseCode: "WATTLE",
      ageGroup: "U13",
      category: "F",
    },
  },
  actorSubject: "vol-1",
  actorDisplayName: "Val Volunteer",
  capturedAt: "2026-08-01T00:00:00.000Z",
  status: "conflict",
  attempts: 3,
};

async function seedV5(): Promise<void> {
  const legacy = new Dexie("carnival-app");
  legacy.version(5).stores(V5_STORES);
  await legacy.open();
  await legacy.table("eventRosters").put({
    carnivalEventId: "ev-1",
    students: [
      { studentId: "st-a", displayName: "Ana Aardvark", yearLevel: 7, houseCode: "WATTLE", ageGroup: "U13", category: "F" },
      { studentId: "st-b", displayName: "Bo Bilby", yearLevel: 8, houseCode: "KIN", ageGroup: "U13", category: "S" },
    ],
    fetchedAt: "2026-08-01T00:00:00.000Z",
    truncated: false,
    limit: 50,
  });
  await legacy.table("outbox").put(parked);
  legacy.close();
}

describe("the v6 upgrade", () => {
  it("strips the category a v5 device already cached, and keeps the capture intact", async () => {
    await seedV5();
    await db.open();

    const roster = await db.eventRosters.get("ev-1");
    const students = roster!.students as Record<string, unknown>[];
    expect(students).toHaveLength(2);
    for (const student of students) expect(student).not.toHaveProperty("category");
    expect(students.map((s) => s.studentId)).toEqual(["st-a", "st-b"]);
    expect(students[0]!.ageGroup).toBe("U13");
    expect(roster!.fetchedAt).toBe("2026-08-01T00:00:00.000Z");

    const item = (await db.outbox.get(parked.id))!;
    const display = (item.payload as { display: Record<string, unknown> }).display;
    expect(display).not.toHaveProperty("category");
    expect(display).toEqual({
      displayName: "Ana Aardvark",
      yearLevel: 7,
      houseCode: "WATTLE",
      ageGroup: "U13",
    });
    expect((item.payload as { studentId: string }).studentId).toBe("st-a");
    expect(item.id).toBe(parked.id);
    expect(item.status).toBe("conflict");
    expect(item.actorSubject).toBe("vol-1");
    expect(item.actorDisplayName).toBe("Val Volunteer");
    expect(item.capturedAt).toBe("2026-08-01T00:00:00.000Z");
    expect(item.attempts).toBe(3);
  });
});
