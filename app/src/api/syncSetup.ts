import type { QueryClient } from "@tanstack/react-query";
import { createSyncWorker, type DrainResult, type SyncWorker } from "../sync/syncWorker";
import { getDeviceId } from "../auth/deviceId";
import { getSelectedCarnival } from "../carnivals/selectedCarnival";
import { oldestCapturedAt, queueStats, resumeQueue, UNDELIVERED_STATUSES } from "../sync/outbox";
import { mayReport, shouldReport } from "../sync/deviceReporter";
import { pruneResolvedOutboxItems } from "../sync/retention";
import { appVersion } from "./client";
import { createOutboxTransport } from "./outboxTransport";
import { queryClient } from "./queryClient";
import { reportReachable, reportUnreachable } from "./reachability";
import { reportDeviceSyncStatus } from "./resources";

export async function afterDrain(
  result: DrainResult,
  client: QueryClient = queryClient,
): Promise<void> {
  // "retry-later" and "throttled" mean the server answered, so they count as reached.
  if (result.stopReason === "network-error") reportUnreachable();
  else if (result.stopReason === "retry-later" || result.stopReason === "throttled"
    || result.accepted + result.rejected + result.conflicts > 0) reportReachable();
  // Non-refetching: the drain does not know which resources its items touched, and an unmounted
  // screen refetches on its next mount.
  if (result.accepted > 0) void client.invalidateQueries({ refetchType: "none" });
  if (result.stopReason === "drained") await pruneResolvedOutboxItems();
  // Not awaited: a half-open uplink holding the report open would block every later syncNow.
  void reportDeviceStatus(result);
}

/** Last attempt, not last success: a server saying no is asked once a minute, not every poll. */
let lastDeviceReportAt: number | null = null;

export async function reportDeviceStatus(result: DrainResult): Promise<void> {
  const carnivalId = getSelectedCarnival()?.id ?? null;
  const now = Date.now();
  const decision = {
    online: navigator.onLine,
    carnivalId,
    changed: result.accepted + result.rejected + result.conflicts > 0,
    lastReportAt: lastDeviceReportAt,
    now,
  };
  if (!mayReport(decision) || carnivalId === null) return;

  try {
    const stats = await queueStats();
    const pending = stats.byStatus.queued + stats.byStatus.syncing;
    const parked = stats.byStatus.conflict + stats.byStatus.rejected;
    if (!shouldReport({ ...decision, pending, parked })) return;
    lastDeviceReportAt = now;
    await reportDeviceSyncStatus(carnivalId, getDeviceId(), {
      pending,
      parked,
      oldestPendingCapturedAt: await oldestCapturedAt(UNDELIVERED_STATUSES),
      appVersion: appVersion(),
    });
  } catch {
    // Telemetry never disturbs capture.
  }
}

export function _resetDeviceReportWindowForTests(): void {
  lastDeviceReportAt = null;
}

export const syncWorker: SyncWorker = createSyncWorker({
  transport: createOutboxTransport(),
  afterDrain: (result) => afterDrain(result),
});

export async function resumeAfterSignIn(): Promise<void> {
  await resumeQueue();
  void syncWorker.syncNow();
}
