import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { db } from "../db/db";
import { enqueue, transition } from "../sync/outbox";
import { DEVICE_REPORT_INTERVAL_MS } from "../sync/deviceReporter";
import type { DrainResult } from "../sync/syncWorker";
import {
  _resetSelectedCarnivalForTests,
  selectCarnival,
} from "../carnivals/selectedCarnival";
import type { Carnival } from "./types";

/**
 * The reporter as it is actually wired (ADR-0034 §3): counted off the real
 * outbox, throttled by the real window, and — the load-bearing property —
 * incapable of turning a failed report into a failed drain.
 */

vi.mock("./resources", () => ({ reportDeviceSyncStatus: vi.fn() }));

import { reportDeviceSyncStatus } from "./resources";
import { afterDrain, _resetDeviceReportWindowForTests, reportDeviceStatus } from "./syncSetup";
import { createQueryClient } from "./queryClient";

const carnival = { id: "c-1", name: "Preston", date: "2027-03-06", status: "Open" } as Carnival;

const drained = (over: Partial<DrainResult> = {}): DrainResult => ({
  stopReason: "drained",
  accepted: 0,
  rejected: 0,
  conflicts: 0,
  ...over,
});

async function queueOne(): Promise<string> {
  const item = await enqueue({ kind: "result.submit", payload: {}, actorSubject: "marshal-1" });
  return item.id;
}

// Only Date is faked: Dexie and fake-indexeddb still need real timers to
// resolve, and the throttle is a wall-clock window, not a scheduled callback.
let elapsed = 0;
let base = 0;
const advance = (ms: number) => vi.setSystemTime(base + (elapsed += ms));

beforeEach(async () => {
  await db.outbox.clear();
  _resetDeviceReportWindowForTests();
  _resetSelectedCarnivalForTests();
  selectCarnival(carnival);
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  vi.mocked(reportDeviceSyncStatus).mockReset().mockResolvedValue({} as never);
  vi.useFakeTimers({ toFake: ["Date"] });
  base = Date.now();
  elapsed = 0;
});

afterEach(() => {
  vi.useRealTimers();
  _resetSelectedCarnivalForTests();
});

describe("reportDeviceStatus", () => {
  it("reports the counts read off the outbox, not the drain's tally", async () => {
    await queueOne();
    await queueOne();
    const parked = await queueOne();
    await transition(parked, "syncing");
    await transition(parked, "conflict");

    await reportDeviceStatus(drained());

    const [carnivalId, deviceId, body] = vi.mocked(reportDeviceSyncStatus).mock.calls[0]!;
    expect(carnivalId).toBe("c-1");
    expect(deviceId).toBeTruthy();
    expect(body.pending).toBe(2);
    expect(body.parked).toBe(1);
    expect(body.oldestPendingCapturedAt).toBeTruthy();
  });

  it("sends nothing while offline", async () => {
    Object.defineProperty(navigator, "onLine", { value: false, configurable: true });
    await queueOne();

    await reportDeviceStatus(drained({ stopReason: "offline" }));

    expect(vi.mocked(reportDeviceSyncStatus)).not.toHaveBeenCalled();
  });

  it("sends nothing when no carnival is selected", async () => {
    _resetSelectedCarnivalForTests();
    await queueOne();

    await reportDeviceStatus(drained({ accepted: 1 }));

    expect(vi.mocked(reportDeviceSyncStatus)).not.toHaveBeenCalled();
  });

  it("throttles the worker's 15s cadence down to one report a minute", async () => {
    await queueOne();
    await reportDeviceStatus(drained());
    expect(vi.mocked(reportDeviceSyncStatus)).toHaveBeenCalledTimes(1);

    advance(15_000);
    await reportDeviceStatus(drained());
    expect(vi.mocked(reportDeviceSyncStatus)).toHaveBeenCalledTimes(1);

    advance(DEVICE_REPORT_INTERVAL_MS);
    await reportDeviceStatus(drained());
    expect(vi.mocked(reportDeviceSyncStatus)).toHaveBeenCalledTimes(2);
  });

  it("reports inside the window when the pass actually moved something", async () => {
    await reportDeviceStatus(drained());
    advance(1_000);

    await reportDeviceStatus(drained({ accepted: 1 }));

    expect(vi.mocked(reportDeviceSyncStatus)).toHaveBeenCalledTimes(2);
  });

  it("swallows a failed report — telemetry never disturbs capture", async () => {
    vi.mocked(reportDeviceSyncStatus).mockRejectedValue(new Error("403"));
    await queueOne();

    await expect(reportDeviceStatus(drained())).resolves.toBeUndefined();
  });

  it("spends the window on a FAILED report, so a refusing server is not hammered", async () => {
    vi.mocked(reportDeviceSyncStatus).mockRejectedValue(new Error("403"));
    await queueOne();

    await reportDeviceStatus(drained());
    advance(5_000);
    await reportDeviceStatus(drained());

    expect(vi.mocked(reportDeviceSyncStatus)).toHaveBeenCalledTimes(1);
  });

  it("does not read the outbox at all on a pass it was never going to report", async () => {
    // The worker polls every 15 s for six hours; seven Dexie counts per poll to
    // decide to say nothing is the cost this gate removes.
    await queueOne();
    await reportDeviceStatus(drained());
    const reads = vi.spyOn(db.outbox, "where");

    advance(15_000);
    await reportDeviceStatus(drained());

    expect(reads).not.toHaveBeenCalled();
    reads.mockRestore();
  });

  it("never delays the next drain, however long a report hangs", async () => {
    // A half-open uplink can hold a PUT open indefinitely. afterDrain runs
    // inside the worker's serialised path, so an awaited report would block
    // every later syncNow — telemetry can never stop delivery (ADR-0034 §3).
    vi.mocked(reportDeviceSyncStatus).mockReturnValue(new Promise(() => {}) as never);
    await queueOne();

    // If afterDrain awaited the report this never resolves and the test hangs.
    await expect(afterDrain(drained(), createQueryClient())).resolves.toBeUndefined();
    await vi.waitFor(() =>
      expect(vi.mocked(reportDeviceSyncStatus)).toHaveBeenCalledTimes(1),
    );
  });
});
