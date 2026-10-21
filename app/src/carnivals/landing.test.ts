import { describe, expect, it } from "vitest";
import { activeTab, landingPath, landingTab, tabPath } from "./landing";

describe("the status-shaped landing (ADR-0024 §8)", () => {
  it("sends an administrator on a Draft carnival to Manage — its deck is empty", () => {
    expect(landingTab("Draft", true)).toBe("manage");
  });

  it("keeps a non-administrator off Manage, which the role gate would refuse", () => {
    expect(landingTab("Draft", false)).toBe("deck");
  });

  it("sends an Open carnival to the deck, for everyone", () => {
    expect(landingTab("Open", true)).toBe("deck");
    expect(landingTab("Open", false)).toBe("deck");
  });

  it("sends a finished carnival to the dashboard — there is nothing left to capture", () => {
    expect(landingTab("Closed", false)).toBe("dashboard");
    expect(landingTab("Finalised", true)).toBe("dashboard");
  });

  it("falls back to the deck when the cached status is not known yet", () => {
    // The gap between adopting a deep link and its carnival query resolving.
    expect(landingTab(null, true)).toBe("deck");
  });

  it("builds the paths the tabs use", () => {
    expect(tabPath("c-1", "deck")).toBe("/carnivals/c-1");
    expect(tabPath("c-1", "dashboard")).toBe("/carnivals/c-1/dashboard");
    expect(landingPath("c-1", "Draft", true)).toBe("/carnivals/c-1/manage");
  });
});

describe("which tab owns a path", () => {
  it("claims the deck for capture, which is reached from it", () => {
    expect(activeTab("/carnivals/c-1", "c-1")).toBe("deck");
    expect(activeTab("/carnivals/c-1/events/ev-1", "c-1")).toBe("deck");
  });

  it("claims the dashboard for the printable program it links to", () => {
    expect(activeTab("/carnivals/c-1/dashboard", "c-1")).toBe("dashboard");
    expect(activeTab("/carnivals/c-1/program", "c-1")).toBe("dashboard");
  });

  it("claims Manage for the organiser surfaces reached from it", () => {
    expect(activeTab("/carnivals/c-1/manage", "c-1")).toBe("manage");
    expect(activeTab("/carnivals/c-1/schedule", "c-1")).toBe("manage");
    expect(activeTab("/carnivals/c-1/setup/events", "c-1")).toBe("manage");
    expect(activeTab("/carnivals/c-1/setup/divisions", "c-1")).toBe("manage");
  });

  it("highlights nothing on a school-level screen, or in another carnival", () => {
    expect(activeTab("/import", "c-1")).toBeNull();
    expect(activeTab("/outbox", "c-1")).toBeNull();
    expect(activeTab("/carnivals", "c-1")).toBeNull();
    // A deep link adopts the OTHER carnival, so the chip changes and this
    // never renders — but a stale id must not light a tab regardless.
    expect(activeTab("/carnivals/c-2/dashboard", "c-1")).toBeNull();
  });

  it("does not mistake an id with the same prefix for the selected carnival", () => {
    expect(activeTab("/carnivals/c-11/dashboard", "c-1")).toBeNull();
  });
});
