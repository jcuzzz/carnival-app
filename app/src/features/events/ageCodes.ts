const EN_DASH = "–";
const AGE = /^(\d+)(\+?)$/;

/** The age an "N" or "N+" code names; null for any other code. */
export function ageNumber(code: string): number | null {
  const match = AGE.exec(code);
  return match ? Number(match[1]) : null;
}

/** "18+": the oldest age, which takes every student that age or older. */
export function isOpenEnded(code: string): boolean {
  return AGE.exec(code)?.[2] === "+";
}

/** Ascending by age with "N+" right after "N"; any other code sorts last. */
export function ageCodeOrder(code: string): number {
  const age = ageNumber(code);
  if (age === null) return Number.MAX_SAFE_INTEGER;
  return age + (isOpenEnded(code) ? 0.5 : 0);
}

/** `AgeScope.Order`: by age, then any other code, alphabetically. */
export function orderCodes(codes: readonly string[]): string[] {
  const unique = [...new Set(codes.map((code) => code.trim()).filter((code) => code !== ""))];
  return unique.sort((a, b) => ageCodeOrder(a) - ageCodeOrder(b) || (a < b ? -1 : a > b ? 1 : 0));
}

/** The codes that name an age ("12", "18+"), in order. */
export function numericCodes(codes: readonly string[]): string[] {
  return orderCodes(codes).filter((code) => ageNumber(code) !== null);
}

/** The codes no age range can name ("OPEN", or a legacy "SNR"), in key order. */
export function nonNumericCodes(codes: readonly string[]): string[] {
  return orderCodes(codes).filter((code) => ageNumber(code) === null);
}

/** A carnival's ages from `from` to `to`: plain ages, then the oldest as "N+". */
export function rangeCodes(from: number, to: number): string[] {
  if (from > to) return [];
  const codes: string[] = [];
  for (let age = from; age < to; age++) codes.push(String(age));
  return [...codes, `${to}+`];
}

// A contiguous run reads as a range; a gapped one must stay a list, or it claims
// ages nobody contests. Only the last age of a run may be open-ended.
function numericSummary(codes: readonly string[], dash: string): string {
  const ordered = numericCodes(codes);
  if (ordered.length === 0) return "";
  if (ordered.length === 1) return ordered[0]!;
  const contiguous = ordered.every(
    (code, i) =>
      (i === ordered.length - 1 || !isOpenEnded(code)) &&
      (i === 0 || ageNumber(code) === ageNumber(ordered[i - 1]!)! + 1),
  );
  return contiguous ? `${ordered[0]}${dash}${ordered.at(-1)}` : ordered.join(", ");
}

/** "12–18+", or "12 to 18+" with `dash` " to "; other codes follow the range by name. */
export function codesSummary(codes: readonly string[], dash: string = EN_DASH): string {
  return [numericSummary(codes, dash), ...nonNumericCodes(codes)]
    .filter((part) => part !== "")
    .join(", ");
}
