/**
 * "1st", "2nd", "3rd", "4th", "21st" — English ordinals for a finishing place.
 * The teens are the exception the naive rule gets wrong (11th, not 11st).
 */
export function ordinal(n: number): string {
  const suffix =
    n % 100 >= 11 && n % 100 <= 13
      ? "th"
      : n % 10 === 1
        ? "st"
        : n % 10 === 2
          ? "nd"
          : n % 10 === 3
            ? "rd"
            : "th";
  return `${n}${suffix}`;
}
