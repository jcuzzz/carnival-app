// @vitest-environment jsdom
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import {
  _resetHouseMarksForTests,
  applyHouseMarks,
  houseColourVar,
  houseSlug,
} from "../../branding/houseMarks";
import { HouseMark } from "./HouseMark";

/**
 * ADR-0044 §3 / ADR-0040 rule 8: the name is always rendered, the swatch only
 * where the school configured a colour. A build that dropped the name for a
 * coloured block would still look right on the reviewer's screen.
 */

beforeEach(() => {
  cleanup();
  _resetHouseMarksForTests();
});

afterEach(() => _resetHouseMarksForTests());

describe("houseSlug", () => {
  it("lower-cases and collapses everything that is not alphanumeric", () => {
    expect(houseSlug("WATTLE")).toBe("wattle");
    expect(houseSlug("MOUNTAIN ASH")).toBe("mountain-ash");
    expect(houseSlug("  St Mary's  ")).toBe("st-mary-s");
  });
});

describe("houseColourVar", () => {
  it("names the minted property for a coloured house and nothing for a bare one", () => {
    applyHouseMarks([
      { code: "MOUNTAIN ASH", name: "Mountain Ash", colour: "#973023" },
      { code: "WATTLE", name: "Wattle", colour: null },
    ]);
    expect(houseColourVar("MOUNTAIN ASH")).toBe("var(--house-mountain-ash)");
    expect(houseColourVar("WATTLE")).toBeNull();
    expect(houseColourVar("GREVILLEA")).toBeNull();
  });
});

describe("HouseMark", () => {
  it("renders the swatch beside the name when the house has a colour", () => {
    applyHouseMarks([{ code: "MOUNTAIN ASH", name: "Mountain Ash", colour: "#973023" }]);
    const { container } = render(<HouseMark code="MOUNTAIN ASH" />);

    expect(screen.getByText("Mountain Ash")).toBeTruthy();
    const swatch = container.querySelector(".house-swatch");
    expect(swatch).not.toBeNull();
    expect(swatch!.getAttribute("style")).toContain("var(--house-mountain-ash)");
    expect(swatch!.getAttribute("aria-hidden")).toBe("true");
  });

  it("renders the name ALONE when the house has no colour", () => {
    applyHouseMarks([{ code: "WATTLE", name: "Wattle", colour: null }]);
    const { container } = render(<HouseMark code="WATTLE" />);

    expect(screen.getByText("Wattle")).toBeTruthy();
    expect(container.querySelector(".house-swatch")).toBeNull();
  });

  it("falls back to the code for a house the server never described", () => {
    const { container } = render(<HouseMark code="GREVILLEA" />);

    expect(screen.getByText("GREVILLEA")).toBeTruthy();
    expect(container.querySelector(".house-swatch")).toBeNull();
  });
});
