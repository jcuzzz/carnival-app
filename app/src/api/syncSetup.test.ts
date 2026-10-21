import { describe, expect, it, vi } from "vitest";
import { QueryObserver } from "@tanstack/react-query";
import { createQueryClient } from "./queryClient";
import { keys } from "./queryKeys";
import { afterDrain, resumeAfterSignIn, syncWorker } from "./syncSetup";
import { db } from "../db/db";
import { pauseQueue, queuePauseState } from "../sync/outbox";
import type { DrainResult } from "../sync/syncWorker";

/**
 * The read cache learns the server moved from the DRAIN, not only from the
 * screen that captured the work (ADR-0012). A per-screen effect cannot
 * invalidate for a screen that has been navigated away from, so a capture
 * drained in the background sat behind the 30 s window or the next focus.
 *
 * The drain MARKS stale and stops there: the mounted screens keep doing their
 * own scoped refetching, so the network cost of a burst is unchanged.
 */

const drained = (over: Partial<DrainResult> = {}): DrainResult => ({
  stopReason: "drained",
  accepted: 0,
  rejected: 0,
  conflicts: 0,
  ...over,
});

describe("afterDrain", () => {
  it("invalidates reads for a screen that is not mounted when the item lands", async () => {
    const client = createQueryClient();
    client.setQueryData(keys.results("c-1", "ev-1"), []);
    client.setQueryData(keys.standings("c-1"), { houses: [] });

    await afterDrain(drained({ accepted: 1 }), client);

    // The accepted row's own event, and the standings it moved two screens away.
    expect(client.getQueryState(keys.results("c-1", "ev-1"))?.isInvalidated).toBe(true);
    expect(client.getQueryState(keys.standings("c-1"))?.isInvalidated).toBe(true);
  });

  it("leaves the cache alone when nothing was accepted", async () => {
    // A parked or refused item changed nothing server-side; refetching every
    // read on the device over it would be noise on a venue uplink.
    const client = createQueryClient();
    client.setQueryData(keys.results("c-1", "ev-1"), []);

    await afterDrain(drained({ conflicts: 1, rejected: 1 }), client);

    expect(client.getQueryState(keys.results("c-1", "ev-1"))?.isInvalidated).toBe(false);
  });

  it("invalidates on a pass that stopped early, not only on a drained queue", async () => {
    // One item landed and the next was refused: the first one's change is on
    // the server whatever the stop reason says.
    const client = createQueryClient();
    client.setQueryData(keys.entries("c-1", "ev-1"), []);

    await afterDrain(drained({ stopReason: "retry-later", accepted: 1 }), client);

    expect(client.getQueryState(keys.entries("c-1", "ev-1"))?.isInvalidated).toBe(true);
  });

  it("marks a MOUNTED screen's read stale without refetching it", async () => {
    // A burst of captures calls syncNow per enqueue; refetching every live query
    // on the deck each time would multiply the venue uplink's load by the number
    // of queries on screen. The scoped effect below is what still refetches.
    const client = createQueryClient();
    const fetchResults = vi.fn().mockResolvedValue([]);
    const observer = new QueryObserver(client, {
      queryKey: keys.results("c-1", "ev-1"),
      queryFn: fetchResults,
    });
    const unsubscribe = observer.subscribe(() => {});
    await vi.waitFor(() =>
      expect(client.getQueryState(keys.results("c-1", "ev-1"))?.status).toBe("success"),
    );
    expect(fetchResults).toHaveBeenCalledTimes(1);

    await afterDrain(drained({ accepted: 1 }), client);

    expect(client.getQueryState(keys.results("c-1", "ev-1"))?.isInvalidated).toBe(true);
    expect(fetchResults).toHaveBeenCalledTimes(1);

    // The screen's own scoped invalidation (useInvalidate) still refetches now.
    await client.invalidateQueries({ queryKey: keys.results("c-1", "ev-1") });
    expect(fetchResults).toHaveBeenCalledTimes(2);
    unsubscribe();
  });
});

describe("resumeAfterSignIn", () => {
  it("lifts a 401 pause and asks for a drain under the new session", async () => {
    await db.syncMeta.clear();
    const syncNow = vi.spyOn(syncWorker, "syncNow").mockResolvedValue(drained());
    try {
      await pauseQueue("Authentication expired");
      expect(await queuePauseState()).not.toBeNull();

      await resumeAfterSignIn();

      expect(await queuePauseState()).toBeNull();
      expect(syncNow).toHaveBeenCalledTimes(1);
    } finally {
      syncNow.mockRestore();
      await db.syncMeta.clear();
    }
  });
});
