import { describe, expect, it } from "vitest";
import {
  ageScopeLabel,
  ageScopeOrder,
  categoryLabel,
  categoryValueLabel,
  divisionLabel,
  facetLabel,
  parseTag,
} from "./eventLabels";

/**
 * A division is one age and one category (ADR-0058 §1), and the header states
 * both honestly — an open event must not invent an age. These are the labels an
 * official reads at arm's length on the deck, so the wording is the contract.
 */

describe("divisionLabel", () => {
  it("renders the division's own age beside its category", () => {
    expect(divisionLabel({ ageGroup: "15", category: "F" })).toBe("15 · Girls");
  });

  it("renders a non-numeric age as the code the roster derives", () => {
    expect(divisionLabel({ ageGroup: "SNR", category: "M" })).toBe("SNR · Boys");
  });

  it("names both axes even where the event is open on both", () => {
    expect(
      divisionLabel({ ageGroup: "OPEN", category: null }),
    ).toBe("All ages · All categories");
  });

  it("keeps the axes independent: open ages with a category, and vice versa", () => {
    expect(
      divisionLabel({ ageGroup: "OPEN", category: "M" }),
    ).toBe("All ages · Boys");
    expect(
      divisionLabel({ ageGroup: "13", category: null }),
    ).toBe("13 · All categories");
  });

});

describe("categoryLabel", () => {
  it("spells the three categories Boys/Girls/Open, and an unknown code as itself", () => {
    expect(categoryLabel("M")).toBe("Boys");
    expect(categoryLabel("F")).toBe("Girls");
    expect(categoryLabel("S")).toBe("Open");
    expect(categoryLabel("X")).toBe("X");
  });

  it("leaves 'Open' to the S category, so an absent axis never reads as one", () => {
    expect(categoryLabel(null)).toBe("All categories");
    expect(
      divisionLabel({ ageGroup: "OPEN", category: "S" }),
    ).toBe("All ages · Open");
  });

  // A chip and a filter option sit among Boys/Girls, where "All categories"
  // would read as "all of them" rather than as the division that has none.
  it("names the absent case differently where it is a value rather than a label", () => {
    expect(categoryValueLabel(null)).toBe("No category");
    expect(categoryValueLabel("M")).toBe("Boys");
    expect(categoryValueLabel("S")).toBe("Open");
  });
});

describe("ageScopeLabel", () => {
  it("returns null for an open age so callers can omit the age part", () => {
    expect(ageScopeLabel({ ageGroup: "OPEN" })).toBeNull();
    expect(ageScopeLabel({ ageGroup: "13" })).toBe("13");
    expect(ageScopeLabel({ ageGroup: "SNR" })).toBe("SNR");
  });
});

describe("ageScopeOrder", () => {
  it("sorts open first, then by age, with a non-numeric code last", () => {
    const codes = ["16", "SNR", "OPEN", "13"];
    expect([...codes].sort((a, b) => ageScopeOrder(a) - ageScopeOrder(b))).toEqual([
      "OPEN",
      "13",
      "16",
      "SNR",
    ]);
  });
});

describe("tag facets", () => {
  it("splits facet:value and falls back to the generic facet", () => {
    expect(parseTag("discipline:Freestyle")).toEqual({ facet: "discipline", value: "Freestyle" });
    expect(parseTag("novelty")).toEqual({ facet: "tag", value: "novelty" });
    expect(facetLabel("discipline")).toBe("Discipline");
    expect(facetLabel("tag")).toBe("Tag");
  });
});
