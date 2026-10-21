import { href } from "../../router";
import { deviceState, type DeviceState } from "../../devices/deviceFleet";
import { STATE_LABEL } from "../devices/DevicesPanel";
import type { DeviceFleet } from "../../api/types";

/** The states keep the device panel's words, so one fleet is not described two ways. */
export function DevicesRow({
  carnivalId,
  fleet,
  now,
}: {
  carnivalId: string;
  fleet: DeviceFleet;
  now: number;
}) {
  const devices = fleet.devices;
  const count = (state: DeviceState) =>
    devices.filter((device) => deviceState(device, now) === state).length;
  const stranded = count("stranded");
  const notDraining = count("not-draining");
  const stale = count("stale");
  const pending = devices.reduce((sum, d) => sum + d.pendingCount, 0);
  const parked = devices.reduce((sum, d) => sum + d.parkedCount, 0);

  return (
    <div className="card dash-devices">
      <strong>Devices</strong>
      <span className="badge ok mono">{count("ok")} reporting</span>
      {stranded > 0 && (
        <span className="badge bad mono">
          {stranded} {STATE_LABEL.stranded}
        </span>
      )}
      {notDraining > 0 && (
        <span className="badge bad mono">
          {notDraining} {STATE_LABEL["not-draining"]}
        </span>
      )}
      {stale > 0 && (
        <span className="badge warn mono">
          {stale} {STATE_LABEL.stale}
        </span>
      )}
      <span className={`badge ${pending > 0 ? "warn" : "muted"} mono`}>{pending} queued</span>
      {parked > 0 && <span className="badge bad mono">{parked} refused</span>}
      <a className="dash-devices-link" href={href(`/carnivals/${carnivalId}/manage`)}>
        Device list
      </a>
    </div>
  );
}
