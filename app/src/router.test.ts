// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import appSource from "./App.tsx?raw";
import { currentPath, currentQuery, matchPath, navigate } from "./router";

describe("matchPath", () => {
  it("captures params from a matching pattern", () => {
    expect(matchPath("/carnivals/:carnivalId/events/:eventId", "/carnivals/abc/events/def")).toEqual(
      { carnivalId: "abc", eventId: "def" },
    );
  });

  it("matches static segments exactly", () => {
    expect(matchPath("/carnivals/:id", "/carnivals/123")).toEqual({ id: "123" });
    expect(matchPath("/carnivals/:id", "/imports/123")).toBeNull();
  });

  it("keeps the setup namespace clear of the capture route (ADR-0022)", () => {
    // Same segment count as the capture screen, which is keyed by DIVISION id —
    // a literal "setup" is the only thing keeping the two apart.
    expect(
      matchPath("/carnivals/:carnivalId/events/:eventId", "/carnivals/c-1/setup/events"),
    ).toBeNull();
    expect(
      matchPath("/carnivals/:carnivalId/setup/events", "/carnivals/c-1/events/ev-1"),
    ).toBeNull();
  });

  /**
   * Ordering, not pattern shape, is the whole guard here: both patterns match
   * `.../setup/events/new`, so App must test the literal first or the add screen
   * is unreachable and "new" is read as a definition id.
   */
  it("keeps the literal `new` route ahead of the definition route (ADR-0022 Stage C)", () => {
    const path = "/carnivals/c-1/setup/events/new";
    expect(matchPath("/carnivals/:carnivalId/setup/events/new", path)).toEqual({
      carnivalId: "c-1",
    });
    expect(matchPath("/carnivals/:carnivalId/setup/events/:definitionId", path)).toEqual({
      carnivalId: "c-1",
      definitionId: "new",
    });

    expect(appSource.indexOf('"/carnivals/:carnivalId/setup/events/new"')).toBeLessThan(
      appSource.indexOf('"/carnivals/:carnivalId/setup/events/:definitionId"'),
    );
  });

  it("requires equal segment counts", () => {
    expect(matchPath("/carnivals/:id", "/carnivals")).toBeNull();
    expect(matchPath("/carnivals/:id", "/carnivals/1/events")).toBeNull();
  });

  it("decodes URI-encoded params", () => {
    expect(matchPath("/x/:name", "/x/a%20b")).toEqual({ name: "a b" });
  });
});

/**
 * The hash route's query half: a modifier ON a route, so the PATH must keep
 * matching as it always did while the query rides alongside.
 */
describe("the query half of a hash route", () => {
  /** jsdom's location is writable through the hash, which is all this needs. */
  function at(hash: string) {
    location.hash = hash;
  }

  it("splits the path from the query", () => {
    at("#/carnivals/c-1/events/ev-1?sort=heat,lane");
    expect(currentPath()).toBe("/carnivals/c-1/events/ev-1");
    expect(currentQuery()).toBe("sort=heat,lane");
  });

  it("leaves an ordinary route untouched", () => {
    at("#/carnivals/c-1");
    expect(currentPath()).toBe("/carnivals/c-1");
    expect(currentQuery()).toBe("");
  });

  it("keeps the route matchable with a query on it", () => {
    at("#/carnivals/c-1/events/ev-1?sort=heat");
    expect(matchPath("/carnivals/:carnivalId/events/:eventId", currentPath())).toEqual({
      carnivalId: "c-1",
      eventId: "ev-1",
    });
  });
});

describe("navigate", () => {
  it("pushes a history entry by default", () => {
    location.hash = "#/carnivals/c-1/events/ev-1";
    const before = history.length;
    navigate("/carnivals/c-1/events/ev-1?sort=heat");
    expect(currentQuery()).toBe("sort=heat");
    expect(history.length).toBeGreaterThan(before);
  });

  it("REPLACES for a navigation that corrects the address", () => {
    // Back onto an address whose claim is no longer true would render it as if
    // it still held.
    location.hash = "#/carnivals/c-1/events/ev-1?sort=heat";
    const before = history.length;
    navigate("/carnivals/c-1/events/ev-1", { replace: true });
    expect(currentPath()).toBe("/carnivals/c-1/events/ev-1");
    expect(currentQuery()).toBe("");
    expect(history.length).toBe(before);
  });
});
