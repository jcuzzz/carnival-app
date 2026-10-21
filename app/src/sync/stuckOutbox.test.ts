import { describe, expect, it } from "vitest";
import type { OutboxStatus } from "../db/db";
import { OUTBOX_STATUSES, type QueueStats } from "./outbox";
import {
  STUCK_AFTER_MS,
  deriveOutboxHealth,
  stuckBanner,
  type OutboxHealthInput,
} from "./stuckOutbox";

/**
 * The whole rule under a controlled clock (ADR-0029 §7) — every transition,
 * both sides of the threshold, and the offline case the design lives on.
 */

const NOW = Date.parse("2027-03-06T11:20:00.000Z");

type StatsOver = Partial<Record<OutboxStatus, number>> & { paused?: boolean };

function stats(over: StatsOver = {}): QueueStats {
  const byStatus = Object.fromEntries(
    OUTBOX_STATUSES.map((status) => [status, over[status] ?? 0]),
  ) as Record<OutboxStatus, number>;
  return {
    total: Object.values(byStatus).reduce((sum, n) => sum + n, 0),
    byStatus,
    paused: over.paused ?? false,
  };
}

/** Healthy defaults, so each test states only the thing it is about. */
function input(over: Partial<OutboxHealthInput> = {}): OutboxHealthInput {
  return {
    stats: stats(),
    oldestPendingCapturedAt: null,
    oldestParkedCapturedAt: null,
    connected: true,
    now: NOW,
    ...over,
  };
}

/** An ISO capture time `ms` before the fixed now. */
function agedBy(ms: number): string {
  return new Date(NOW - ms).toISOString();
}

describe("deriveOutboxHealth", () => {
  it("says nothing while the first stats read is in flight", () => {
    expect(deriveOutboxHealth(input({ stats: undefined }))).toEqual({
      pending: 0,
      parked: 0,
      waitingMs: null,
      cause: null,
    });
  });

  it("says nothing about an empty, connected queue", () => {
    expect(deriveOutboxHealth(input()).cause).toBeNull();
  });

  it("does not escalate a queue that is merely large but moving", () => {
    const health = deriveOutboxHealth(
      input({
        stats: stats({ queued: 40 }),
        oldestPendingCapturedAt: agedBy(20_000),
      }),
    );
    expect(health.cause).toBeNull();
    expect(health.pending).toBe(40);
  });

  describe("a 401 pause (§2a)", () => {
    it("is stuck IMMEDIATELY — a capture taken this second is already behind it", () => {
      const health = deriveOutboxHealth(
        input({ stats: stats({ queued: 1, paused: true }), oldestPendingCapturedAt: agedBy(0) }),
      );
      expect(health.cause).toBe("paused");
    });

    it("escalates offline too — the lapsed session is a fact, not a prediction", () => {
      const health = deriveOutboxHealth(
        input({
          stats: stats({ queued: 3, paused: true }),
          oldestPendingCapturedAt: agedBy(1_000),
          connected: false,
        }),
      );
      expect(health.cause).toBe("paused");
    });

    it("escalates even with nothing waiting yet — the next capture will pile up", () => {
      expect(deriveOutboxHealth(input({ stats: stats({ paused: true }) })).cause).toBe("paused");
    });

    it("outranks a stall and parked work — one banner, worst cause wins (§4)", () => {
      const health = deriveOutboxHealth(
        input({
          stats: stats({ queued: 6, conflict: 2, paused: true }),
          oldestPendingCapturedAt: agedBy(STUCK_AFTER_MS * 4),
          oldestParkedCapturedAt: agedBy(STUCK_AFTER_MS * 4),
        }),
      );
      expect(health.cause).toBe("paused");
    });

    it("clears the moment the queue resumes, revealing the next cause down (§5)", () => {
      const stalled = input({
        stats: stats({ queued: 6 }),
        oldestPendingCapturedAt: agedBy(STUCK_AFTER_MS),
      });
      expect(deriveOutboxHealth(stalled).cause).toBe("stalled");
      const resumedAndEmpty = input({ stats: stats(), oldestPendingCapturedAt: null });
      expect(deriveOutboxHealth(resumedAndEmpty).cause).toBeNull();
    });
  });

  describe("a head-of-line stall (§2b)", () => {
    it("stays quiet one millisecond under the threshold", () => {
      const health = deriveOutboxHealth(
        input({
          stats: stats({ queued: 5 }),
          oldestPendingCapturedAt: agedBy(STUCK_AFTER_MS - 1),
        }),
      );
      expect(health.cause).toBeNull();
    });

    it("escalates exactly at the threshold", () => {
      const health = deriveOutboxHealth(
        input({ stats: stats({ queued: 5 }), oldestPendingCapturedAt: agedBy(STUCK_AFTER_MS) }),
      );
      expect(health.cause).toBe("stalled");
      expect(health.waitingMs).toBe(STUCK_AFTER_MS);
    });

    it("reads the head item, so an item in flight still counts as undelivered", () => {
      const health = deriveOutboxHealth(
        input({
          stats: stats({ syncing: 1, queued: 4 }),
          oldestPendingCapturedAt: agedBy(STUCK_AFTER_MS + 60_000),
        }),
      );
      expect(health.cause).toBe("stalled");
      expect(health.pending).toBe(5);
    });

    it("clears when the head drains and the queue is young again", () => {
      const before = input({
        stats: stats({ queued: 9 }),
        oldestPendingCapturedAt: agedBy(STUCK_AFTER_MS * 3),
      });
      expect(deriveOutboxHealth(before).cause).toBe("stalled");
      const after = input({ stats: stats({ queued: 8 }), oldestPendingCapturedAt: agedBy(4_000) });
      expect(deriveOutboxHealth(after).cause).toBeNull();
    });

    it("clears when the queue empties entirely", () => {
      const drained = input({ stats: stats({ accepted: 40 }), oldestPendingCapturedAt: null });
      expect(deriveOutboxHealth(drained).cause).toBeNull();
    });
  });

  describe("offline is NORMAL and never escalates (§3)", () => {
    it("stays silent with an hour-old queue and no network", () => {
      const health = deriveOutboxHealth(
        input({
          stats: stats({ queued: 25 }),
          oldestPendingCapturedAt: agedBy(60 * 60 * 1000),
          connected: false,
        }),
      );
      expect(health.cause).toBeNull();
      // The strip still names the backlog it is holding.
      expect(health.pending).toBe(25);
    });

    it("stays silent however long the outage lasts — no second, longer threshold", () => {
      for (const hours of [1, 3, 8]) {
        const health = deriveOutboxHealth(
          input({
            stats: stats({ queued: 100 }),
            oldestPendingCapturedAt: agedBy(hours * 60 * 60 * 1000),
            connected: false,
          }),
        );
        expect(health.cause).toBeNull();
      }
    });

    it("escalates the same queue the moment the server is reachable again", () => {
      const queue = {
        stats: stats({ queued: 25 }),
        oldestPendingCapturedAt: agedBy(60 * 60 * 1000),
      };
      expect(deriveOutboxHealth(input({ ...queue, connected: false })).cause).toBeNull();
      expect(deriveOutboxHealth(input({ ...queue, connected: true })).cause).toBe("stalled");
    });

    it("going offline hands a live stall back to the connectivity strip", () => {
      const queue = {
        stats: stats({ queued: 3 }),
        oldestPendingCapturedAt: agedBy(STUCK_AFTER_MS + 1),
      };
      expect(deriveOutboxHealth(input({ ...queue, connected: true })).cause).toBe("stalled");
      expect(deriveOutboxHealth(input({ ...queue, connected: false })).cause).toBeNull();
    });
  });

  describe("parked work (§2c)", () => {
    it("stays chip-level while it is fresh — the deck row already showed it", () => {
      const health = deriveOutboxHealth(
        input({ stats: stats({ conflict: 2 }), oldestParkedCapturedAt: agedBy(30_000) }),
      );
      expect(health.cause).toBeNull();
      expect(health.parked).toBe(2);
    });

    it("escalates once nobody has come back for it", () => {
      const health = deriveOutboxHealth(
        input({
          stats: stats({ conflict: 2, rejected: 1 }),
          oldestParkedCapturedAt: agedBy(STUCK_AFTER_MS),
        }),
      );
      expect(health.cause).toBe("parked");
      expect(health.parked).toBe(3);
    });

    it("escalates offline — every ADR-0025 action works without a network", () => {
      const health = deriveOutboxHealth(
        input({
          stats: stats({ conflict: 1 }),
          oldestParkedCapturedAt: agedBy(STUCK_AFTER_MS),
          connected: false,
        }),
      );
      expect(health.cause).toBe("parked");
    });

    it("ranks below a stall, which is holding everything behind it", () => {
      const health = deriveOutboxHealth(
        input({
          stats: stats({ queued: 4, conflict: 2 }),
          oldestPendingCapturedAt: agedBy(STUCK_AFTER_MS),
          oldestParkedCapturedAt: agedBy(STUCK_AFTER_MS * 2),
        }),
      );
      expect(health.cause).toBe("stalled");
    });

    it("clears once every parked item is requeued or discarded", () => {
      const dealt = input({ stats: stats({ discarded: 3 }), oldestParkedCapturedAt: null });
      expect(deriveOutboxHealth(dealt).cause).toBeNull();
    });

    it("ignores a count with no parked row to date it", () => {
      const health = deriveOutboxHealth(
        input({ stats: stats({ conflict: 2 }), oldestParkedCapturedAt: null }),
      );
      expect(health.cause).toBeNull();
    });
  });

  it("treats a clock that ran backwards as young, not stuck", () => {
    const ahead = new Date(NOW + 5_000).toISOString();
    const health = deriveOutboxHealth(
      input({ stats: stats({ queued: 2 }), oldestPendingCapturedAt: ahead }),
    );
    expect(health.cause).toBeNull();
  });

  it("ignores an unparseable capture time rather than escalating on it", () => {
    const health = deriveOutboxHealth(
      input({ stats: stats({ queued: 2 }), oldestPendingCapturedAt: "not-a-date" }),
    );
    expect(health.cause).toBeNull();
    expect(health.waitingMs).toBeNull();
  });
});

describe("stuckBanner copy", () => {
  function bannerFor(over: Partial<OutboxHealthInput>) {
    return stuckBanner(deriveOutboxHealth(input(over)));
  }

  it("is nothing at all when nothing is stuck", () => {
    expect(stuckBanner(deriveOutboxHealth(input()))).toBeNull();
  });

  it("names the count and offers sign-in when paused", () => {
    const banner = bannerFor({
      stats: stats({ queued: 7, paused: true }),
      oldestPendingCapturedAt: agedBy(1_000),
    });
    expect(banner?.text).toContain("7 captures are waiting");
    expect(banner?.text).toContain("sign in");
    expect(banner?.action).toBe("sign-in");
    expect(banner?.actionLabel).toBe("Sign in to resume");
  });

  it("does not claim captures are waiting when none are", () => {
    const banner = bannerFor({ stats: stats({ paused: true }) });
    expect(banner?.text).not.toMatch(/\d/);
    expect(banner?.text).toContain("Nothing will sync until you sign in");
  });

  it("names how long a stall has lasted, rounded down to whole minutes", () => {
    const banner = bannerFor({
      stats: stats({ queued: 1 }),
      oldestPendingCapturedAt: agedBy(12 * 60_000 + 30_000),
    });
    expect(banner?.text).toContain("12 minutes");
    expect(banner?.text).toContain("1 capture is waiting");
    expect(banner?.action).toBe("outbox");
  });

  it("never reads '0 minutes' at the bottom of the range", () => {
    const banner = bannerFor({
      stats: stats({ queued: 2 }),
      // 5 minutes to the millisecond: the sentence must never read "0 minutes".
      oldestPendingCapturedAt: agedBy(STUCK_AFTER_MS),
    });
    expect(banner?.text).toContain("5 minutes");
  });

  it("reads as one sentence for a single parked capture", () => {
    const banner = bannerFor({
      stats: stats({ conflict: 1 }),
      oldestParkedCapturedAt: agedBy(STUCK_AFTER_MS),
    });
    expect(banner?.text).toBe(
      "1 capture was refused and needs a decision —" +
        " it will not sync until someone deals with it.",
    );
    expect(banner?.actionLabel).toBe("Open the Outbox");
  });

  it("and for several", () => {
    const banner = bannerFor({
      stats: stats({ conflict: 2, rejected: 1 }),
      oldestParkedCapturedAt: agedBy(STUCK_AFTER_MS),
    });
    expect(banner?.text).toBe(
      "3 captures were refused and need a decision —" +
        " they will not sync until someone deals with them.",
    );
  });
});
