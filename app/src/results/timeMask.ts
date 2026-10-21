/**
 * Digits before a dot read positionally, like a stopwatch (`123` -> 1:23.00); `.` then enters
 * hundredths. Seconds >= 60 are refused, never carried. The server re-parses and is authoritative.
 */

const MAX_MINUTE_DIGITS = 2;
const MAX_SECOND_DIGITS = 2;
const MAX_WHOLE_DIGITS = 4; // positional minutes+seconds: up to 99:59
const FRACTION_DIGITS = 2;

export type TimeFieldMode = "empty" | "time" | "status";

export interface TimeFieldState {
  mode: TimeFieldMode;
  display: string;
  ghost: string;
  /** Only when a valid time. */
  timeValue: string | null;
  statusCode: "DNS" | "DNF" | "DQ" | null;
  valid: boolean;
  error: string | null;
}

interface Typed {
  /** Digits before any ":" — positional, or the minutes once a ":" is typed. */
  whole: string;
  /** Null until a ":" is typed; the literal seconds after it once it is. */
  colonSeconds: string | null;
  /** Null until a "." is typed; "" for a trailing dot with nothing after it. */
  fraction: string | null;
}

function splitTyped(input: string): Typed {
  let whole = "";
  let colonSeconds: string | null = null;
  let fraction: string | null = null;
  for (const ch of input) {
    if (ch >= "0" && ch <= "9") {
      if (fraction !== null) {
        if (fraction.length < FRACTION_DIGITS) fraction += ch;
      } else if (colonSeconds !== null) {
        if (colonSeconds.length < MAX_SECOND_DIGITS) colonSeconds += ch;
      } else if (whole.length < MAX_WHOLE_DIGITS) {
        whole += ch;
      }
      continue;
    }
    if (ch === ":" && colonSeconds === null && fraction === null) {
      whole = whole.slice(0, MAX_MINUTE_DIGITS);
      colonSeconds = "";
      continue;
    }
    if (ch === "." && fraction === null) fraction = "";
  }
  return { whole, colonSeconds, fraction };
}

const EMPTY: TimeFieldState = {
  mode: "empty",
  display: "",
  ghost: "",
  timeValue: null,
  statusCode: null,
  valid: false,
  error: null,
};

/** Pure: the component holds the raw input and passes each change straight back through here. */
export function readTimeField(input: string): TimeFieldState {
  if (/[a-z]/i.test(input)) {
    const letters = input.replace(/[^a-z]/gi, "").toUpperCase().slice(0, 3);
    const statusCode =
      letters === "DNS" || letters === "DNF" || letters === "DQ" ? letters : null;
    return {
      mode: "status",
      display: letters,
      ghost: "",
      timeValue: null,
      statusCode,
      valid: statusCode !== null,
      error: statusCode === null ? "Type DNS, DNF or DQ." : null,
    };
  }

  const { whole, colonSeconds, fraction } = splitTyped(input);
  const secondDigits = colonSeconds ?? whole.slice(-MAX_SECOND_DIGITS);
  const minuteDigits = colonSeconds === null ? whole.slice(0, -MAX_SECOND_DIGITS) : whole;
  const minutes = minuteDigits.replace(/^0+/, "");
  const typedTime =
    colonSeconds === null
      ? (minutes === "" ? "" : `${minutes}:`) + secondDigits
      : `${whole}:${colonSeconds}`;
  const display = typedTime + (fraction === null ? "" : `.${fraction}`);
  if (display === "") return EMPTY;

  const ghost =
    fraction === null
      ? `.${"0".repeat(FRACTION_DIGITS)}`
      : "0".repeat(FRACTION_DIGITS - fraction.length);
  const blocked = (error: string | null): TimeFieldState => ({
    mode: "time",
    display,
    ghost,
    timeValue: null,
    statusCode: null,
    valid: false,
    error,
  });

  // A lone ":" or ".", or seconds still arriving: blocked quietly, nothing has gone wrong.
  if (secondDigits.length < (colonSeconds === null ? 1 : MAX_SECOND_DIGITS)) return blocked(null);

  const secondsNumber = Number(secondDigits);
  if (secondsNumber >= 60) return blocked("Seconds must be under 60.");

  const minutesNumber = minutes === "" ? 0 : Number(minutes);
  const hundredths = (fraction ?? "").padEnd(FRACTION_DIGITS, "0");
  const total = minutesNumber * 60 + secondsNumber + Number(hundredths) / 100;
  if (total <= 0) return blocked(null);

  const timeValue =
    minutesNumber === 0
      ? `${secondsNumber}.${hundredths}`
      : `${minutesNumber}:${String(secondsNumber).padStart(2, "0")}.${hundredths}`;
  return { mode: "time", display, ghost, timeValue, statusCode: null, valid: true, error: null };
}
