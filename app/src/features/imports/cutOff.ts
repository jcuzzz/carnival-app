const MONTHS = [
  "January",
  "February",
  "March",
  "April",
  "May",
  "June",
  "July",
  "August",
  "September",
  "October",
  "November",
  "December",
];

/** The cut-off ("12-31", as the server sends it) in a year: "31 December 2027". */
export function agesAsAt(cutOff: string, year: number): string {
  const [month, day] = cutOff.split("-").map(Number);
  const name = MONTHS[month - 1];
  if (name === undefined || !day) return `${cutOff} ${year}`;
  // A 29 February cut-off falls on the 28th in a common year, as the server derives it.
  const last = new Date(Date.UTC(year, month, 0)).getUTCDate();
  return `${Math.min(day, last)} ${name} ${year}`;
}
