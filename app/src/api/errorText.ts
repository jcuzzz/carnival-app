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

export const SIGN_IN_FAILED_TEXT =
  "That username and password don't match, or this account can't sign in right now. Check them and try again; if it keeps happening, ask a system administrator.";

/** Signing in has no offline copy: the server checks every step. */
export const SIGN_IN_NEEDS_SERVER_TEXT =
  "Signing in needs the server, and this device can't reach it right now. Reconnect, then try again.";

/** A 429 from a password check: the server caps how many it runs at once (ADR-0065 §6). */
export const PASSWORD_CHECK_BUSY_TEXT =
  "The server is busy checking passwords. Wait a few seconds and try again.";

/** For every screen that sends a password: a 429 there means a queue, not a refusal. */
export function passwordCheckText(err: unknown, networkText: string): string {
  if (err instanceof ApiError && err.status === 429) return PASSWORD_CHECK_BUSY_TEXT;
  return errorText(err, networkText);
}

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
  // One sentence for every refusal: which part was wrong must not be readable from it.
  SIGN_IN_FAILED: SIGN_IN_FAILED_TEXT,
  CODE_INVALID:
    "That code didn't work. Codes change every 30 seconds — type the one your authenticator app shows now.",
  LOCAL_SIGN_IN_NOT_ALLOWED:
    "This account can't sign in here: only system administrators may use a local account at this school. Sign in with your school account instead.",
  SETUP_LINK_INVALID:
    "This setup link has expired or has already been used. Ask a system administrator for a new one.",
  PASSWORD_TOO_SHORT: "Choose a password of at least 15 characters. A short sentence is easy to remember.",
  PASSWORD_TOO_LONG: "That password is too long. Keep it under 256 characters.",
  PASSWORD_TOO_COMMON:
    "That password is on the list of common or leaked passwords. Choose something less predictable.",
  PASSWORD_TOO_PREDICTABLE:
    "That password is mostly the school's name, a house name or the username. Choose something less predictable.",
  CURRENT_PASSWORD_WRONG: "Your current password isn't right. Nothing was changed.",
  ACCOUNT_USERNAME_INVALID:
    "Usernames are 2 to 64 lower-case letters, digits, dots, dashes or underscores, starting with a letter or digit. Not an email address.",
  ACCOUNT_USERNAME_RESERVED: "That username is reserved. Choose another.",
  ACCOUNT_USERNAME_TAKEN: "That username is already taken. Choose another.",
  ACCOUNT_DISPLAY_NAME_INVALID: "Give a display name of up to 100 characters.",
  ACCOUNT_DISABLED: "This account is disabled. Enable it first, then reset it.",
  ACCOUNT_SELF_CHANGE:
    "You can't change your own account from here. Ask another system administrator, or use My account.",
  ACCOUNT_NOT_FOUND: "That account no longer exists. Reload the list.",
  NOT_A_LOCAL_ACCOUNT:
    "This page is for accounts that sign in with a username and password here. Your school account is managed by the school.",
  AUTHENTICATOR_ALREADY_ENROLLED: "An authenticator app is already set up for this account.",
  ROLE_LOCAL_ACCOUNT_UNKNOWN:
    "No local account has that username. Check the spelling, or add the person under School → People first.",
  CROSS_SITE_REQUEST_REFUSED:
    "The server refused a request that didn't come from this app. Open the app from its own address and try again.",
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
