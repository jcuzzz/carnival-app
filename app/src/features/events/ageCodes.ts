const EN_DASH = "–";

/** The pickers' fallback when no roster is promoted. */
export const FALLBACK_AGE_CODES: string[] = Array.from({ length: 17 }, (_, i) => `${i + 5}`);

function numeric(code: string): number | null {
  return /^\d+$/.test(code) ? Number(code) : null;
}

/** `AgeScope.Order`: numeric ascending, then any other code, alphabetically. */
export function orderCodes(codes: readonly string[]): string[] {
  const unique = [...new Set(codes.map((code) => code.trim()).filter((code) => code !== ""))];
  return unique.sort((a, b) => {
    const [na, nb] = [numeric(a), numeric(b)];
    if (na !== null && nb !== null) return na - nb;
    if (na !== null) return -1;
    if (nb !== null) return 1;
    return a < b ? -1 : a > b ? 1 : 0;
  });
}

/** The numeric codes among these, ascending — the from/to selects' options. */
export function numericCodes(codes: readonly string[]): string[] {
  return orderCodes(codes).filter((code) => numeric(code) !== null);
}

/** The codes a from/to range cannot name (SNR and the like), in key order. */
export function nonNumericCodes(codes: readonly string[]): string[] {
  return orderCodes(codes).filter((code) => numeric(code) === null);
}

/** One code per age from `from` to `to` inclusive; empty if either is not a number. */
export function codeRange(codes: readonly string[], from: string, to: string): string[] {
  const [lo, hi] = [numeric(from), numeric(to)];
  if (lo === null || hi === null) return [];
  return numericCodes(codes).filter((code) => numeric(code)! >= lo && numeric(code)! <= hi);
}

// A contiguous run reads as a range; a gapped one must stay a list, or it claims
// ages nobody contests.
function numericSummary(codes: readonly string[]): string {
  const ordered = numericCodes(codes);
  if (ordered.length === 0) return "";
  if (ordered.length === 1) return ordered[0]!;
  const numbers = ordered.map((code) => numeric(code)!);
  const contiguous = numbers.every((n, i) => i === 0 || n === numbers[i - 1]! + 1);
  return contiguous ? `${ordered[0]}${EN_DASH}${ordered.at(-1)}` : ordered.join(", ");
}

/** "12–18, SNR": other codes follow the range, so seniors are named rather than swallowed. */
export function codesSummary(codes: readonly string[]): string {
  return [numericSummary(codes), ...nonNumericCodes(codes)]
    .filter((part) => part !== "")
    .join(", ");
}
