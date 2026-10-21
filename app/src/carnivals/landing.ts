import type { CarnivalStatus } from "../api/types";

export type CarnivalTab = "deck" | "dashboard" | "manage";

/**
 * A Draft carnival's deck is an empty grid and a finished one has nothing left to capture.
 * `canManage` only keeps the Draft rule off a tab the role gate would refuse.
 */
export function landingTab(status: CarnivalStatus | null, canManage: boolean): CarnivalTab {
  if (status === "Draft") return canManage ? "manage" : "deck";
  if (status === "Closed" || status === "Finalised") return "dashboard";
  return "deck";
}

export function tabPath(carnivalId: string, tab: CarnivalTab): string {
  return tab === "deck" ? `/carnivals/${carnivalId}` : `/carnivals/${carnivalId}/${tab}`;
}

export function landingPath(
  carnivalId: string,
  status: CarnivalStatus | null,
  canManage: boolean,
): string {
  return tabPath(carnivalId, landingTab(status, canManage));
}

export function activeTab(path: string, carnivalId: string): CarnivalTab | null {
  const base = `/carnivals/${carnivalId}`;
  if (path === base) return "deck";
  if (!path.startsWith(`${base}/`)) return null;
  // `matchPath` ignores a trailing slash, so `/carnivals/:id/` renders the deck
  // — the nav has to agree, or that address highlights nothing.
  const rest = path.slice(base.length + 1).replace(/\/$/, "");
  if (rest === "") return "deck";
  if (rest === "dashboard" || rest === "program") return "dashboard";
  if (rest === "manage" || rest === "schedule" || rest.startsWith("setup/")) return "manage";
  if (rest.startsWith("events/")) return "deck";
  return null;
}
