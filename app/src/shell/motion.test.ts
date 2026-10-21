/**
 * @vitest-environment jsdom
 */
import { afterEach, describe, expect, it, vi } from "vitest";
import { scrollBehavior } from "./motion";

function stubMatchMedia(matches: boolean | null) {
  if (matches === null) {
    Reflect.deleteProperty(window, "matchMedia");
    return;
  }
  vi.stubGlobal(
    "matchMedia",
    vi.fn((query: string) => ({ matches, media: query })),
  );
}

describe("scrollBehavior", () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("scrolls smoothly by default", () => {
    stubMatchMedia(false);
    expect(scrollBehavior()).toBe("smooth");
  });

  it("jumps instead of animating when reduced motion is preferred (U10)", () => {
    stubMatchMedia(true);
    expect(scrollBehavior()).toBe("auto");
  });

  it("falls back to smooth where matchMedia is unavailable", () => {
    stubMatchMedia(null);
    expect(scrollBehavior()).toBe("smooth");
  });
});
