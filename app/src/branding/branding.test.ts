// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { getBranding } from "../api/resources";
import { db } from "../db/db";
import {
  _resetBrandingForTests,
  applyBranding,
  getBrandingState,
  initBranding,
  inkFor,
  contrastRatio,
  loadPersistedBranding,
  monogramOf,
  normaliseBrandColour,
  refreshBranding,
} from "./branding";
import type { Branding } from "../api/types";

vi.mock("../api/resources", () => ({ getBranding: vi.fn() }));

const fetched = vi.mocked(getBranding);
const root = () => document.documentElement;
const prop = (name: string) => root().style.getPropertyValue(name);

beforeEach(async () => {
  vi.resetAllMocks();
  document.head.innerHTML = '<meta name="theme-color" content="#123456" />';
  _resetBrandingForTests();
  await db.appSettings.clear();
});

describe("applyBranding (ADR-0040 §3)", () => {
  it("paints a configured colour and takes the ink that contrasts MORE", () => {
    applyBranding({ schoolName: "Riverbend Secondary College", brandColour: "#123456" });
    expect(prop("--brand")).toBe("#123456");
    expect(prop("--brand-ink")).toBe("#ffffff");

    applyBranding({ schoolName: "Goldfields College", brandColour: "#e8c34a" });
    expect(prop("--brand")).toBe("#e8c34a");
    expect(prop("--brand-ink")).toBe("#1a2330");
  });

  it("leaves the token defaults alone when no colour is configured", () => {
    applyBranding({ schoolName: "Riverbend SC", brandColour: "#123456" });
    applyBranding({ schoolName: "Riverbend SC", brandColour: null });
    expect(prop("--brand")).toBe("");
    expect(prop("--brand-ink")).toBe("");
    expect(getBrandingState().schoolName).toBe("Riverbend SC");
  });

  it("retints the address bar only when a colour is configured", () => {
    const meta = () => document.querySelector('meta[name="theme-color"]')?.getAttribute("content");
    applyBranding({ schoolName: "Goldfields College", brandColour: "#E8C34A" });
    expect(meta()).toBe("#e8c34a");

    applyBranding({ schoolName: "Goldfields College", brandColour: null });
    expect(meta()).toBe("#e8c34a");
  });

  it("publishes the state to subscribers", () => {
    applyBranding({ schoolName: " Riverbend Secondary College ", brandColour: "#abc" });
    expect(getBrandingState()).toEqual({
      schoolName: "Riverbend Secondary College",
      brandColour: "#aabbcc",
      monogram: "RS",
    });
  });
});

describe("inkFor", () => {
  it("is white on a dark fill and dark on a light one", () => {
    expect(inkFor("#123456")).toBe("#ffffff");
    expect(inkFor("#000000")).toBe("#ffffff");
    expect(inkFor("#e8c34a")).toBe("#1a2330");
    expect(inkFor("#f2f4f7")).toBe("#1a2330");
  });
});

describe("normaliseBrandColour", () => {
  it("expands and lower-cases, and refuses anything else", () => {
    expect(normaliseBrandColour("#ABC")).toBe("#aabbcc");
    expect(normaliseBrandColour("  #1F3A5F ")).toBe("#1f3a5f");
    expect(normaliseBrandColour("navy")).toBeNull();
    expect(normaliseBrandColour("#12345")).toBeNull();
    expect(normaliseBrandColour(null)).toBeNull();
  });
});

describe("monogramOf", () => {
  it("takes the first letters of up to two words", () => {
    expect(monogramOf("Riverbend Secondary College")).toBe("RS");
    expect(monogramOf("Sports")).toBe("S");
    expect(monogramOf("st mary's")).toBe("SM");
    expect(monogramOf("   ")).toBeNull();
    expect(monogramOf(null)).toBeNull();
  });
});

describe("boot", () => {
  it("applies the persisted pair BEFORE the server answers, then the fresh one", async () => {
    await db.appSettings.put({
      key: "branding",
      value: { schoolName: "Riverbend SC", brandColour: "#123456" },
    });
    let answer: (branding: Branding) => void = () => {};
    fetched.mockReturnValue(
      new Promise<Branding>((resolve) => {
        answer = resolve;
      }),
    );

    const booted = initBranding();
    await vi.waitFor(() => expect(getBrandingState().schoolName).toBe("Riverbend SC"));
    expect(prop("--brand")).toBe("#123456");

    answer({ schoolName: "Goldfields College", brandColour: "#e8c34a" });
    await booted;

    expect(getBrandingState().monogram).toBe("GC");
    expect(prop("--brand-ink")).toBe("#1a2330");
    expect(await loadPersistedBranding()).toEqual({
      schoolName: "Goldfields College",
      brandColour: "#e8c34a",
    });
  });

  it("keeps the persisted branding silently when the fetch fails", async () => {
    await db.appSettings.put({
      key: "branding",
      value: { schoolName: "Riverbend SC", brandColour: "#123456" },
    });
    fetched.mockRejectedValue(new TypeError("Failed to fetch"));

    await expect(initBranding()).resolves.toBeUndefined();
    expect(getBrandingState()).toEqual({
      schoolName: "Riverbend SC",
      brandColour: "#123456",
      monogram: "RS",
    });
    expect(prop("--brand")).toBe("#123456");
  });

  it("persists the server's answer on a first, unbranded start", async () => {
    fetched.mockResolvedValue({ schoolName: "Riverbend SC", brandColour: null });
    await initBranding();
    expect(await loadPersistedBranding()).toEqual({
      schoolName: "Riverbend SC",
      brandColour: null,
    });
  });
});

describe("contrastRatio", () => {
  it("agrees with the server's WCAG maths on both inks", () => {
    expect(contrastRatio("#ffffff", "#000000")).toBeCloseTo(21, 5);
    expect(contrastRatio("#123456", "#ffffff")).toBeCloseTo(12.72, 1);
    expect(contrastRatio("#e8c34a", "#1a2330")).toBeCloseTo(9.3, 1);
  });
});

describe("refreshBranding (ADR-0045 §6)", () => {
  it("repaints AND re-persists after the school's settings changed", async () => {
    fetched.mockResolvedValue({ schoolName: "Riverbend SC", brandColour: "#123456" });
    await initBranding();

    fetched.mockResolvedValue({ schoolName: "Riverbend College", brandColour: "#0b3d2c" });
    await refreshBranding();

    expect(prop("--brand")).toBe("#0b3d2c");
    expect(getBrandingState().schoolName).toBe("Riverbend College");
    expect(await loadPersistedBranding()).toEqual({
      schoolName: "Riverbend College",
      brandColour: "#0b3d2c",
    });
  });

  it("leaves the painted branding alone when the refetch fails", async () => {
    fetched.mockResolvedValue({ schoolName: "Riverbend SC", brandColour: "#123456" });
    await initBranding();

    fetched.mockRejectedValue(new TypeError("Failed to fetch"));
    await expect(refreshBranding()).resolves.toBeUndefined();
    expect(prop("--brand")).toBe("#123456");
  });
});
