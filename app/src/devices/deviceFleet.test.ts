import { describe, expect, it } from "vitest";
import type { DeviceStatus } from "../api/types";
import {
  clockOffsetMs,
  DEVICE_STALE_AFTER_MS,
  deviceState,
  finaliseReason,
  fleetWarning,
  relativeAge,
} from "./deviceFleet";

/**
 * The fleet rules, driven by an injected clock (ADR-0034 §4). "A device heard
 * from sixteen minutes ago is stranded" has to be an assertion, not a wait.
 */

const NOW = Date.parse("2027-02-25T11:30:00Z");

const minutesAgo = (n: number) => new Date(NOW - n * 60_000).toISOString();

function device(over: Partial<DeviceStatus> = {}): DeviceStatus {
  return {
    deviceId: "device-1",
    lastSeenUtc: minutesAgo(1),
    lastDrainUtc: minutesAgo(1),
    pendingCount: 0,
    parkedCount: 0,
    oldestPendingCapturedAtUtc: null,
    lastSubject: "marshal-1",
    lastDisplayName: "Mia Marshal",
    appVersion: null,
    ...over,
  };
}

describe("deviceState", () => {
  it("is ok while the device is still reporting", () => {
    expect(deviceState(device({ lastSeenUtc: minutesAgo(14) }), NOW)).toBe("ok");
  });

  it("crosses at the threshold exactly, and the comparison is >=", () => {
    const at = new Date(NOW - DEVICE_STALE_AFTER_MS).toISOString();
    expect(deviceState(device({ lastSeenUtc: at, pendingCount: 1 }), NOW)).toBe("stranded");
  });

  it("is only QUIET when a stale device has nothing waiting", () => {
    // A drained device stops reporting by design (ADR-0034 §3), so an aging
    // last-heard on an empty queue is silence, not loss — and must not be red.
    expect(deviceState(device({ lastSeenUtc: minutesAgo(90) }), NOW)).toBe("stale");
  });

  it("is STRANDED when a stale device still carries undelivered captures", () => {
    expect(deviceState(device({ lastSeenUtc: minutesAgo(20), pendingCount: 6 }), NOW))
      .toBe("stranded");
  });

  it("treats an unreadable timestamp as ok rather than raising a false alarm", () => {
    expect(deviceState(device({ lastSeenUtc: "not a date", pendingCount: 3 }), NOW)).toBe("ok");
  });

  // The paused device: online, refused, and reporting on the worker's cadence
  // for as long as anyone holds it. Fresh "last heard", queue that never moves.
  it("is NOT-DRAINING when a device is still talking but nothing is leaving it", () => {
    expect(
      deviceState(
        device({ lastSeenUtc: minutesAgo(1), lastDrainUtc: minutesAgo(40), pendingCount: 6 }),
        NOW,
      ),
    ).toBe("not-draining");
  });

  it("is NOT-DRAINING when a reporting device has never once drained", () => {
    expect(
      deviceState(
        device({ lastSeenUtc: minutesAgo(1), lastDrainUtc: null, pendingCount: 2 }),
        NOW,
      ),
    ).toBe("not-draining");
  });

  it("leaves a device that is delivering alone, however much it is carrying", () => {
    expect(
      deviceState(
        device({ lastSeenUtc: minutesAgo(1), lastDrainUtc: minutesAgo(2), pendingCount: 9 }),
        NOW,
      ),
    ).toBe("ok");
  });

  it("says nothing about a fresh empty device that has never drained", () => {
    // Nothing outstanding is nothing to deliver: a device that has only ever
    // reported an empty queue is not failing to drain it.
    expect(
      deviceState(device({ lastSeenUtc: minutesAgo(1), lastDrainUtc: null }), NOW),
    ).toBe("ok");
  });
});

describe("clockOffsetMs", () => {
  it("is the gap between the server's clock and this browser's", () => {
    expect(clockOffsetMs(new Date(NOW + 600_000).toISOString(), NOW)).toBe(600_000);
    expect(clockOffsetMs(new Date(NOW - 600_000).toISOString(), NOW)).toBe(-600_000);
  });

  it("assumes no skew rather than guessing at one", () => {
    expect(clockOffsetMs(undefined, NOW)).toBe(0);
    expect(clockOffsetMs(new Date(NOW).toISOString(), undefined)).toBe(0);
    expect(clockOffsetMs("not a date", NOW)).toBe(0);
  });

  it("moves a device across the staleness window when the desk clock is wrong", () => {
    // Desk clock ten minutes SLOW: a device last heard fourteen desk-minutes ago
    // was really heard twenty-four minutes ago, and is stranded.
    const offset = clockOffsetMs(new Date(NOW + 10 * 60_000).toISOString(), NOW);
    const quiet = device({ lastSeenUtc: minutesAgo(14), pendingCount: 3 });
    expect(deviceState(quiet, NOW)).toBe("ok");
    expect(deviceState(quiet, NOW + offset)).toBe("stranded");
  });
});

describe("relativeAge", () => {
  it("reads as elapsed time, which is what a desk needs", () => {
    expect(relativeAge(minutesAgo(0), NOW)).toBe("just now");
    expect(relativeAge(minutesAgo(12), NOW)).toBe("12 min ago");
    expect(relativeAge(minutesAgo(125), NOW)).toBe("2 h 05 ago");
    expect(relativeAge(null, NOW)).toBe("never");
  });
});

describe("fleetWarning", () => {
  it("says nothing about a fleet that is reporting", () => {
    expect(fleetWarning([device(), device({ deviceId: "device-2" })], NOW)).toBeNull();
  });

  it("says nothing when no device has ever reported — silence is not evidence", () => {
    expect(fleetWarning([], NOW)).toBeNull();
    expect(fleetWarning(undefined, NOW)).toBeNull();
  });

  it("names the stranded devices and the captures they are holding", () => {
    const warning = fleetWarning(
      [
        device({ deviceId: "d-1", lastSeenUtc: minutesAgo(40), pendingCount: 6 }),
        device({ deviceId: "d-2", lastSeenUtc: minutesAgo(22), pendingCount: 2 }),
        device({ deviceId: "d-3" }),
      ],
      NOW,
    );
    expect(warning?.level).toBe("stranded");
    expect(warning?.text).toContain("2 devices");
    expect(warning?.text).toContain("8 captures");
  });

  it("falls back to parked work, which never syncs on its own", () => {
    const warning = fleetWarning([device({ parkedCount: 3 })], NOW);
    expect(warning?.level).toBe("parked");
    expect(warning?.text).toContain("3 captures");
    expect(warning?.text).toContain("1 device");
  });

  it("ranks a stranded device above parked work — one warning, worst first", () => {
    const warning = fleetWarning(
      [
        device({ deviceId: "d-1", parkedCount: 9 }),
        device({ deviceId: "d-2", lastSeenUtc: minutesAgo(30), pendingCount: 1 }),
      ],
      NOW,
    );
    expect(warning?.level).toBe("stranded");
  });

  it("does not warn about a quiet device that is carrying nothing", () => {
    expect(fleetWarning([device({ lastSeenUtc: minutesAgo(300) })], NOW)).toBeNull();
  });

  it("names the device that is reporting and delivering nothing", () => {
    const warning = fleetWarning(
      [device({ lastSeenUtc: minutesAgo(1), lastDrainUtc: null, pendingCount: 4 })],
      NOW,
    );
    expect(warning?.level).toBe("not-draining");
    expect(warning?.text).toContain("4 captures");
    expect(warning?.text).toContain("signed-out");
  });

  it("ranks the reporting-but-not-draining device above the stranded one", () => {
    // It is the one still in somebody's hands, so it is the one an organiser can
    // fix before publishing.
    const warning = fleetWarning(
      [
        device({ deviceId: "d-1", lastSeenUtc: minutesAgo(40), pendingCount: 2 }),
        device({
          deviceId: "d-2",
          lastSeenUtc: minutesAgo(1),
          lastDrainUtc: minutesAgo(30),
          pendingCount: 3,
        }),
      ],
      NOW,
    );
    expect(warning?.level).toBe("not-draining");
  });

  it("says so when the fleet could not be READ — silence from the server is not evidence", () => {
    const warning = fleetWarning(undefined, NOW, true);
    expect(warning?.level).toBe("unreadable");
    expect(warning?.text).toContain("could not be read");
    // …and an empty answer that DID arrive still says nothing.
    expect(fleetWarning([], NOW, false)).toBeNull();
  });

  it("prefers the specific warning to the read failure when it has stale rows to go on", () => {
    const warning = fleetWarning(
      [device({ lastSeenUtc: minutesAgo(40), pendingCount: 5 })],
      NOW,
      true,
    );
    expect(warning?.level).toBe("stranded");
  });
});

describe("finaliseReason", () => {
  it("counts the undrained devices and their captures, stranded or still reporting", () => {
    const reason = finaliseReason(
      [
        device({ deviceId: "a", lastSeenUtc: minutesAgo(40), pendingCount: 3 }),
        device({ deviceId: "b", lastDrainUtc: null, pendingCount: 2 }),
        device({ deviceId: "c", parkedCount: 4 }),
      ],
      NOW,
    );
    expect(reason).toBe("Finalised with 2 devices not drained (5 captures not yet on the server).");
  });

  it("names an unreadable fleet, and keeps the server's default for a clean one", () => {
    expect(finaliseReason(undefined, NOW, true)).toBe(
      "Finalised while device queue state could not be read.",
    );
    expect(finaliseReason([device()], NOW)).toBeUndefined();
    expect(finaliseReason([device({ parkedCount: 2 })], NOW)).toBeUndefined();
  });
});
