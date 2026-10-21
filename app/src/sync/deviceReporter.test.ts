import { describe, expect, it } from "vitest";
import {
  DEVICE_REPORT_INTERVAL_MS,
  shouldReport,
  type DeviceReportDecision,
} from "./deviceReporter";

/**
 * The reporting rule (ADR-0034 §3). Telemetry is allowed to be silent; it is
 * never allowed to be chatty, and it is never allowed to disturb capture.
 */

const NOW = 1_800_000_000_000;

function input(over: Partial<DeviceReportDecision> = {}): DeviceReportDecision {
  return {
    online: true,
    carnivalId: "c-1",
    pending: 0,
    parked: 0,
    changed: false,
    lastReportAt: null,
    now: NOW,
    ...over,
  };
}

describe("shouldReport", () => {
  it("reports on first contact — that is what makes the device known", () => {
    expect(shouldReport(input())).toBe(true);
  });

  it("never reports with no carnival selected — there is nothing to key the row to", () => {
    expect(shouldReport(input({ carnivalId: null, pending: 4, changed: true }))).toBe(false);
  });

  it("never reports while offline", () => {
    // Offline is a supported state (ADR-0002/0029 §3): the queue holds, and a
    // report that cannot be sent is not a fault to escalate.
    expect(shouldReport(input({ online: false, pending: 9, changed: true }))).toBe(false);
  });

  it("reports at once when the pass moved something, window or not", () => {
    expect(shouldReport(input({ changed: true, lastReportAt: NOW - 1_000 }))).toBe(true);
  });

  it("stays quiet inside the window — what stops a 15s poll reporting four times a minute", () => {
    expect(
      shouldReport(input({ pending: 5, lastReportAt: NOW - (DEVICE_REPORT_INTERVAL_MS - 1) })),
    ).toBe(false);
  });

  it("heartbeats once the window passes while work is outstanding", () => {
    const justOutside = { lastReportAt: NOW - DEVICE_REPORT_INTERVAL_MS };
    expect(shouldReport(input({ ...justOutside, pending: 5 }))).toBe(true);
    expect(shouldReport(input({ ...justOutside, parked: 1 }))).toBe(true);
  });

  it("goes silent once the queue is empty — an idle device has nothing to say", () => {
    expect(
      shouldReport(input({ pending: 0, parked: 0, lastReportAt: NOW - 60 * 60_000 })),
    ).toBe(false);
  });
});
