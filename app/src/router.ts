import { useSyncExternalStore } from "react";

// Hash routing: hash URLs need no server rewrite rules on a static host.

function currentHash(): string {
  if (typeof location === "undefined") return "/";
  const hash = location.hash.replace(/^#/, "");
  return hash === "" ? "/" : hash;
}

export function currentPath(): string {
  const hash = currentHash();
  const q = hash.indexOf("?");
  return q === -1 ? hash : hash.slice(0, q);
}

export function currentQuery(): string {
  const hash = currentHash();
  const q = hash.indexOf("?");
  return q === -1 ? "" : hash.slice(q + 1);
}

/** A full-page round trip (the OIDC sign-in) must come back to the whole address. */
export function currentUrl(): string {
  if (typeof location === "undefined") return "/";
  return `${location.pathname}${location.search}${location.hash}` || "/";
}

function subscribe(listener: () => void): () => void {
  addEventListener("hashchange", listener);
  return () => removeEventListener("hashchange", listener);
}

export function usePath(): string {
  return useSyncExternalStore(subscribe, currentPath, () => "/");
}

export function useUrl(): string {
  return useSyncExternalStore(subscribe, currentUrl, () => "/");
}

/** A STRING, not a parsed object: the snapshot must compare stable by value. */
export function useRouteQuery(): string {
  return useSyncExternalStore(subscribe, currentQuery, () => "");
}

/**
 * `replace` is for a navigation that corrects the address, where going Back would resurrect
 * a page whose claim is no longer true.
 */
export function navigate(path: string, options: { replace?: boolean } = {}): void {
  if (options.replace && typeof history !== "undefined" && history.replaceState) {
    history.replaceState(history.state, "", `#${path}`);
    // replaceState fires no hashchange, so the router is told by hand.
    dispatchEvent(new HashChangeEvent("hashchange"));
    return;
  }
  location.hash = path;
}

export function href(path: string): string {
  return `#${path}`;
}

export function matchPath(pattern: string, path: string): Record<string, string> | null {
  const patternParts = pattern.split("/").filter((p) => p !== "");
  const pathParts = path.split("/").filter((p) => p !== "");
  if (patternParts.length !== pathParts.length) return null;
  const params: Record<string, string> = {};
  for (let i = 0; i < patternParts.length; i++) {
    const patternPart = patternParts[i]!;
    const pathPart = pathParts[i]!;
    if (patternPart.startsWith(":")) {
      params[patternPart.slice(1)] = decodeURIComponent(pathPart);
    } else if (patternPart !== pathPart) {
      return null;
    }
  }
  return params;
}
