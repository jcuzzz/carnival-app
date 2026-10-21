import { beforeEach, describe, expect, it, vi } from "vitest";
import { db } from "../db/db";
import { _resetUuidv7StateForTests } from "../sync/uuidv7";
import { RACE_MATERIALISE_KIND, type RaceMaterialisePayload } from "../api/outboxTransport";

/**
 * ADR-0015 phase 2/3: a race is minted through the OUTBOX, never a direct POST.
 * These pin the payload the transport will send and the client-minted id that
 * BECOMES the race id — the two properties a queued lane assignment depends on.
 */

const syncNow = vi.fn().mockResolvedValue({
  stopReason: "offline",
  accepted: 0,
  rejected: 0,
  conflicts: 0,
});

vi.mock("../api/syncSetup", () => ({
  syncWorker: { start: vi.fn(), stop: vi.fn(), syncNow: () => syncNow() },
  resumeAfterSignIn: vi.fn(),
}));

beforeEach(async () => {
  _resetUuidv7StateForTests();
  await db.outbox.clear();
  syncNow.mockClear();
});

describe("submitMaterialiseViaOutbox", () => {
  it("queues ONE race.materialise naming the division, and asks for a drain", async () => {
    // ADR-0019: one act, one audit record — never N queued placements, which is
    // N chances to half-complete on a device that drops mid-batch.
    const { submitMaterialiseViaOutbox } = await import("./submitRace");
    const item = await submitMaterialiseViaOutbox({
      carnivalId: "c-1",
      eventId: "ev-13f",
      scheduleSlotId: "slot-1",
      title: "13 · F",
      actorSubject: "marshal-1",
      actorDisplayName: "Mia Marshal",
    });

    const stored = await db.outbox.get(item.id);
    expect(stored?.kind).toBe(RACE_MATERIALISE_KIND);
    expect(stored?.status).toBe("queued");
    expect(stored?.payload).toEqual({
      carnivalId: "c-1",
      eventId: "ev-13f",
      scheduleSlotId: "slot-1",
      label: null,
      display: { title: "13 · F" },
    } satisfies RaceMaterialisePayload);
    // The client-minted id IS the race id the server will adopt.
    expect(item.id).toMatch(/^[0-9a-f-]{36}$/);
    expect(stored?.actorSubject).toBe("marshal-1");
    expect(stored?.actorDisplayName).toBe("Mia Marshal");
    expect(syncNow).toHaveBeenCalledTimes(1);
  });

  it("sends a null slot and a null label rather than blanks", async () => {
    const { submitMaterialiseViaOutbox } = await import("./submitRace");
    const item = await submitMaterialiseViaOutbox({
      carnivalId: "c-1",
      eventId: "ev-1",
      label: "  ",
      title: "13 · F",
      actorSubject: "marshal-1",
    });
    const payload = (await db.outbox.get(item.id))?.payload as RaceMaterialisePayload;
    expect(payload.scheduleSlotId).toBeNull();
    expect(payload.label).toBeNull();
  });
});
