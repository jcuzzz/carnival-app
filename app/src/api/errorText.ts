import { ApiError } from "./client";

/** fetch() rejects with a TypeError, worded per browser, when nothing reached the server. */
export const NETWORK_ERROR_TEXT = "Can't reach the server — your work is saved on this device.";

/** For screens that write directly: no outbox is behind them, so nothing was saved anywhere. */
export const DIRECT_WRITE_NETWORK_ERROR_TEXT =
  "Can't reach the server — nothing was saved. This screen keeps no offline copy, so the change needs making again once you're back online.";

/** The editor and the server both say this, so the sentence lives here. */
export const AUTO_FILL_NEEDS_LANE_COUNT_TEXT = "Set the lane count first.";

export const IMPORT_BATCH_EXPIRED_TEXT =
  "This batch expired: a batch that is not promoted within 7 days of upload has its staged rows discarded. Upload the file again.";

/** Overrides `detail` entirely for codes whose server sentence is written for a log, not a pool. */
const CODE_TEXT: Readonly<Record<string, string>> = {
  REQUEST_INVALID:
    "The server couldn't read this submission, so nothing was recorded. Your capture is still held on this device — report it to the carnival organiser; sending it again won't help.",
  REQUEST_BODY_TOO_LARGE:
    "This submission was too large for the server to accept, so nothing was recorded. Your capture is still held on this device — report it to the carnival organiser: it's the server's size limit that needs changing, not anything you entered.",
  RESULT_FINALISED:
    "The carnival was finalised before this change reached the server, so the recorded result still stands. What you typed is held on this device — show it to the carnival organiser, who can decide whether the published result needs amending.",
  AUTO_FILL_LANES_REQUIRES_LANE_COUNT: AUTO_FILL_NEEDS_LANE_COUNT_TEXT,
  IMPORT_BATCH_EXPIRED: IMPORT_BATCH_EXPIRED_TEXT,
};

/** Undefined for most codes, and then `detail` is shown; never returns the code itself. */
export function codeText(code: string | undefined): string | undefined {
  return code === undefined ? undefined : CODE_TEXT[code];
}

export function errorText(err: unknown, networkText: string = NETWORK_ERROR_TEXT): string {
  if (err instanceof ApiError) return codeText(err.code) ?? err.message;
  if (err instanceof TypeError) return networkText;
  return err instanceof Error ? err.message : String(err);
}
