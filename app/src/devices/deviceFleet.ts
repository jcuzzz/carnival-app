import type { DeviceStatus } from "../api/types";

// The counts are as of each device's last contact: a stale row is evidence about the past.

/**
 * A device reports at most once a minute while it has work, so this is roughly fifteen missed
 * check-ins: clear of a concrete pillar, a lock screen, or a walk to the far end of the pool.
 */
export const DEVICE_STALE_AFTER_MS = 15 * 60 * 1000;

export type DeviceState =
  | "ok"
  /** Not a fault: a device that has drained everything stops reporting by design. */
  | "stale"
  | "stranded"
  /** Talking but delivering nothing, e.g. 401-paused: looks healthiest from the desk. */
  | "not-draining";

export function deviceState(device: DeviceStatus, now: number): DeviceState {
  const heardMs = ageMs(device.lastSeenUtc, now);
  if (heardMs !== null && heardMs >= DEVICE_STALE_AFTER_MS)
    return device.pendingCount > 0 ? "stranded" : "stale";
  if (device.pendingCount > 0) {
    const drainedMs = ageMs(device.lastDrainUtc, now);
    if (drainedMs === null || drainedMs >= DEVICE_STALE_AFTER_MS) return "not-draining";
  }
  return "ok";
}

/** 0 when either side is missing: guessing the skew is worse than assuming none. */
export function clockOffsetMs(
  serverNowUtc: string | null | undefined,
  receivedAt: number | undefined,
): number {
  if (!serverNowUtc || receivedAt === undefined) return 0;
  const parsed = Date.parse(serverNowUtc);
  return Number.isNaN(parsed) ? 0 : parsed - receivedAt;
}

export function ageMs(at: string | null | undefined, now: number): number | null {
  if (!at) return null;
  const parsed = Date.parse(at);
  return Number.isNaN(parsed) ? null : now - parsed;
}

/** A deck reads elapsed, not clocks. */
export function relativeAge(at: string | null | undefined, now: number): string {
  const ms = ageMs(at, now);
  if (ms === null) return "never";
  if (ms < 60_000) return "just now";
  const minutes = Math.floor(ms / 60_000);
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.floor(minutes / 60);
  return `${hours} h ${String(minutes % 60).padStart(2, "0")} ago`;
}

export interface FleetWarning {
  level: "not-draining" | "stranded" | "parked" | "unreadable";
  text: string;
}

/**
 * A warning, never a block: an offline device and a lost one look alike from here. A read that
 * never answered must not look like a clean fleet.
 */
export function fleetWarning(
  devices: readonly DeviceStatus[] | undefined,
  now: number,
  readFailed = false,
): FleetWarning | null {
  const found = worstFleetState(devices, now);
  if (found) return found;
  if (readFailed)
    return {
      level: "unreadable",
      text:
        "Device queue state could not be read — verify devices by hand before finalising.",
    };
  return null;
}

function worstFleetState(
  devices: readonly DeviceStatus[] | undefined,
  now: number,
): FleetWarning | null {
  if (!devices || devices.length === 0) return null;
  // Ahead of stranded: this device is reachable, so it can still be fixed before publishing.
  const notDraining = devices.filter((d) => deviceState(d, now) === "not-draining");
  if (notDraining.length > 0) {
    const captures = notDraining.reduce((sum, d) => sum + d.pendingCount, 0);
    return {
      level: "not-draining",
      text:
        `${plural(notDraining.length, "device")} ${notDraining.length === 1 ? "is" : "are"} still reporting but` +
        ` nothing is leaving ${notDraining.length === 1 ? "it" : "them"} —` +
        ` ${plural(captures, "capture")} that ${was(captures)} not reached the server.` +
        " A signed-out device looks exactly like this. Finalising now publishes without them.",
    };
  }
  const stranded = devices.filter((d) => deviceState(d, now) === "stranded");
  if (stranded.length > 0) {
    const captures = stranded.reduce((sum, d) => sum + d.pendingCount, 0);
    return {
      level: "stranded",
      text:
        `${plural(stranded.length, "device")} last reported more than` +
        ` ${Math.round(DEVICE_STALE_AFTER_MS / 60_000)} minutes ago with work still waiting` +
        ` — ${plural(captures, "capture")} that ${was(captures)} not reached the server.` +
        " Finalising now publishes without them.",
    };
  }
  const parked = devices.reduce((sum, d) => sum + d.parkedCount, 0);
  if (parked > 0) {
    const devicesWithParked = devices.filter((d) => d.parkedCount > 0).length;
    return {
      level: "parked",
      text:
        `${plural(parked, "capture")} on ${plural(devicesWithParked, "device")}` +
        ` ${parked === 1 ? "was" : "were"} refused and still ${needs(parked)} a decision.` +
        " Refused captures never sync on their own.",
    };
  }
  return null;
}

/** The audit's record that the organiser finalised past the warning; undefined keeps the default. */
export function finaliseReason(
  devices: readonly DeviceStatus[] | undefined,
  now: number,
  readFailed = false,
): string | undefined {
  const undrained = (devices ?? []).filter((d) => {
    const state = deviceState(d, now);
    return state === "not-draining" || state === "stranded";
  });
  if (undrained.length > 0) {
    const captures = undrained.reduce((sum, d) => sum + d.pendingCount, 0);
    return (
      `Finalised with ${plural(undrained.length, "device")} not drained` +
      ` (${plural(captures, "capture")} not yet on the server).`
    );
  }
  if (readFailed) return "Finalised while device queue state could not be read.";
  return undefined;
}

function plural(n: number, noun: string): string {
  return `${n} ${n === 1 ? noun : `${noun}s`}`;
}

function was(n: number): string {
  return n === 1 ? "has" : "have";
}

function needs(n: number): string {
  return n === 1 ? "needs" : "need";
}
