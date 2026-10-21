import type { Carnival } from "../api/types";

/**
 * The server lists every carnival the school has ever run. Four years of three a year keeps
 * last year's results reachable without typing while the page stays a page.
 */
export const SWITCHER_ROW_LIMIT = 12;

export interface SwitcherList {
  rows: Carnival[];
  total: number;
  matched: number;
  truncated: boolean;
}

function haystack(carnival: Carnival): string {
  return [carnival.name, carnival.type, carnival.venue ?? "", carnival.date, carnival.status]
    .join(" ")
    .toLowerCase();
}

function matches(carnival: Carnival, terms: string[]): boolean {
  const text = haystack(carnival);
  return terms.every((term) => text.includes(term));
}

/** Open first: a live carnival outranks a Draft one dated later in the year. */
function byLiveThenRecent(a: Carnival, b: Carnival): number {
  const open = Number(b.status === "Open") - Number(a.status === "Open");
  if (open !== 0) return open;
  return b.date.localeCompare(a.date);
}

export function switcherList(carnivals: Carnival[], search = ""): SwitcherList {
  const terms = search.toLowerCase().split(/\s+/).filter(Boolean);
  const found = terms.length === 0 ? carnivals : carnivals.filter((c) => matches(c, terms));
  const ordered = [...found].sort(byLiveThenRecent);
  return {
    rows: ordered.slice(0, SWITCHER_ROW_LIMIT),
    total: carnivals.length,
    matched: ordered.length,
    truncated: ordered.length > SWITCHER_ROW_LIMIT,
  };
}
