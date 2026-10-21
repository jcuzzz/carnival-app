export const DEVICE_REPORT_INTERVAL_MS = 60_000;

export interface DeviceReportDecision {
  /** A report needs an uplink; a failed send stays silent. */
  online: boolean;
  /** A report is per carnival. */
  carnivalId: string | null;
  pending: number;
  parked: number;
  /** The pass that just finished moved something; the desk wants that at once. */
  changed: boolean;
  lastReportAt: number | null;
  now: number;
}

/** Asked before reading the outbox: counting queues every 15 s pass to send nothing is waste. */
export function mayReport(
  input: Omit<DeviceReportDecision, "pending" | "parked">,
): boolean {
  // The server keys the row by carnival, and guessing one would be a lie.
  if (input.carnivalId === null) return false;
  if (!input.online) return false;
  if (input.changed) return true;
  // First contact is what makes the device known: there is no registration step.
  if (input.lastReportAt === null) return true;
  return input.now - input.lastReportAt >= DEVICE_REPORT_INTERVAL_MS;
}

export function shouldReport(input: DeviceReportDecision): boolean {
  if (!mayReport(input)) return false;
  if (input.changed || input.lastReportAt === null) return true;
  // Beyond that, only a device with something outstanding keeps talking.
  return input.pending > 0 || input.parked > 0;
}
