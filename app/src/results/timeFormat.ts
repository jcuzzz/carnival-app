/** Mirrors the server's ResultValueParser; the server re-parses and is authoritative. */
const MINUTES_SECONDS = /^(\d{1,3}):([0-5]\d)(?:\.(\d{1,3}))?$/;
const SECONDS_ONLY = /^(\d{1,4})(?:\.(\d{1,3}))?$/;

export interface ParsedTime {
  seconds: number;
}

export function parseSwimTime(raw: string): { ok: true; value: ParsedTime } | { ok: false; error: string } {
  const input = raw.trim();
  if (input.length === 0) return { ok: false, error: "Enter a time." };

  let seconds: number;
  const withMinutes = MINUTES_SECONDS.exec(input);
  if (withMinutes) {
    seconds =
      Number(withMinutes[1]) * 60 + Number(withMinutes[2]) + fraction(withMinutes[3]);
  } else {
    const secondsOnly = SECONDS_ONLY.exec(input);
    if (!secondsOnly) {
      return {
        ok: false,
        error: `'${raw}' is not a valid time. Use mm:ss.ff (e.g. 1:23.45) or ss.ff (e.g. 34.56).`,
      };
    }
    seconds = Number(secondsOnly[1]) + fraction(secondsOnly[2]);
  }
  if (seconds <= 0) return { ok: false, error: "A time must be greater than zero." };
  return { ok: true, value: { seconds } };
}

function fraction(digits: string | undefined): number {
  if (!digits) return 0;
  return Number(digits) / 10 ** digits.length;
}

export function formatSeconds(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  const remainder = seconds - minutes * 60;
  const secondsText = remainder.toFixed(2);
  if (minutes === 0) return secondsText;
  return `${minutes}:${remainder < 10 ? "0" : ""}${secondsText}`;
}
