import type { NonOkResultStatus } from "../../api/outboxTransport";
import type { MeasureType } from "../../api/types";
import type { MeasureStatusCode } from "../../results/markMask";

/** The only place a status is re-spelled for display; older queued items hold capitals already. */
const STATUS_LABEL: Record<string, string | undefined> = {
  Dns: "DNS",
  Dnf: "DNF",
  Dq: "DQ",
  Nm: "NM",
};

export const statusLabel = (status: string): string => STATUS_LABEL[status] ?? status;

/** The letter-typing path (markMask.ts) yields the capitals the official typed. */
export const STATUS_FROM_TYPED: Record<MeasureStatusCode, NonOkResultStatus> = {
  DNS: "Dns",
  DNF: "Dnf",
  DQ: "Dq",
  NM: "Nm",
};

/** Non-result statuses in tap order, canonical casing. */
export const STATUS_CODES = ["Dns", "Dnf", "Dq"] as const;

/** NM on a field event only: a runner who starts and does not finish is a DNF. */
const FIELD_STATUS_CODES = [...STATUS_CODES, "Nm"] as const;

export function statusCodesFor(
  measureType: MeasureType,
  /** NM is derived from an all-foul series, never typed. */
  attempts = false,
): readonly NonOkResultStatus[] {
  return measureType === "Time" || attempts ? STATUS_CODES : FIELD_STATUS_CODES;
}
