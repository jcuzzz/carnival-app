/** 64 is the server's ceiling. */
export const MAX_LANE_COUNT = 64;

export const LANE_COUNT_INVALID_TEXT = `Lanes per venue must be a whole number between 1 and ${MAX_LANE_COUNT}.`;

/** Blank is `null` — "say nothing about the count" — and never a zero. */
export function parseLaneCount(typed: string): number | null | "invalid" {
  const trimmed = typed.trim();
  if (trimmed === "") return null;
  const count = Number(trimmed);
  if (!Number.isInteger(count) || count < 1 || count > MAX_LANE_COUNT) return "invalid";
  return count;
}
