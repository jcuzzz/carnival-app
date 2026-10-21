import { useEffect, useState } from "react";
import { errorText } from "../../api/errorText";
import type { ApiState } from "../../api/useApiQuery";
import type { DeviceFleet, DeviceStatus } from "../../api/types";
import { Freshness } from "../../shell/Freshness";
import {
  deviceState,
  relativeAge,
  type DeviceState,
} from "../../devices/deviceFleet";

const READ_ERROR_TEXT = "Can't reach the server, so the device list couldn't be loaded.";

export const STATE_LABEL: Record<DeviceState, string> = {
  ok: "reporting",
  stale: "quiet",
  stranded: "not reporting",
  "not-draining": "not draining",
};

const STATE_BADGE: Record<DeviceState, string> = {
  ok: "ok",
  stale: "muted",
  stranded: "bad",
  "not-draining": "bad",
};

/** A device crosses the staleness window with no read to announce it, so the screen holds a
 * clock. */
export const DEVICE_TICK_MS = 60_000;

export function useNow(intervalMs: number): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), intervalMs);
    return () => clearInterval(timer);
  }, [intervalMs]);
  return now;
}

export function DevicesPanel({ devices, now }: { devices: ApiState<DeviceFleet>; now: number }) {
  const rows = devices.data?.devices ?? [];
  return (
    <section className="subsection" aria-labelledby="devices-heading">
      <h2 id="devices-heading">Devices</h2>
      <p className="row-sub">
        What each device last said about its own queue — counts are as of that moment, not now.
        <Freshness updatedAt={devices.updatedAt} isRefetching={devices.isRefetching} />
      </p>
      {devices.error && (
        <div className="alert error" role="alert">
          {errorText(devices.error, READ_ERROR_TEXT)}
        </div>
      )}
      {devices.loading && rows.length === 0 && <p>Loading…</p>}
      {/* A failed read is NOT an empty fleet. The empty-state copy is a positive
          claim about the server's answer, and there was no answer. */}
      {devices.error && rows.length === 0 && (
        <p className="row-sub">
          This list could not be read, so nothing about the fleet is known here — not that it is
          clear. Check the devices themselves before finalising.
        </p>
      )}
      {!devices.loading && !devices.error && rows.length === 0 && (
        <p className="row-sub">
          No device has reported a queue for this carnival yet. A device becomes visible here the
          first time it syncs, so nothing listed is not the same as nothing outstanding.
        </p>
      )}
      {rows.length > 0 && (
        <ul className="row-list card">
          {rows.map((device) => (
            <DeviceRow key={device.deviceId} device={device} now={now} />
          ))}
        </ul>
      )}
    </section>
  );
}

function DeviceRow({ device, now }: { device: DeviceStatus; now: number }) {
  const state = deviceState(device, now);
  const outstanding = device.pendingCount + device.parkedCount;
  return (
    <li className={`device-row ${state}`}>
      <span className="row-main">
        <span className="row-title">
          {device.lastDisplayName ?? device.lastSubject}
          <span className={`badge ${STATE_BADGE[state]} device-state`}>{STATE_LABEL[state]}</span>
        </span>
        <span className="row-sub">
          {/* The tail: it tells two iPads apart, and ids minted together differ at the end. */}
          device <span className="mono">{device.deviceId.slice(-8)}</span> · last heard{" "}
          {relativeAge(device.lastSeenUtc, now)}
          {device.appVersion ? ` · build ${device.appVersion}` : ""}
        </span>
        <span className="row-sub">
          {outstanding === 0
            ? "nothing waiting"
            : describeQueue(device, now)}
        </span>
        {state === "not-draining" && (
          <span className="row-sub">
            reporting but not draining — device may be signed out
          </span>
        )}
      </span>
    </li>
  );
}

function describeQueue(device: DeviceStatus, now: number): string {
  const parts: string[] = [];
  if (device.pendingCount > 0) {
    const oldest = relativeAge(device.oldestPendingCapturedAtUtc, now);
    parts.push(
      `${device.pendingCount} waiting to sync` +
        (oldest === "never" ? "" : ` · oldest captured ${oldest}`),
    );
  }
  if (device.parkedCount > 0) parts.push(`${device.parkedCount} refused, needing a decision`);
  return parts.join(" · ");
}
