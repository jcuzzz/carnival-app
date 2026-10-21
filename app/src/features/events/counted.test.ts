import { describe, expect, it } from "vitest";
import { counted } from "./counted";

describe("counted", () => {
  it("pluralises on everything but one, zero included", () => {
    expect(counted(0, "division")).toBe("0 divisions");
    expect(counted(1, "division")).toBe("1 division");
    expect(counted(4, "division")).toBe("4 divisions");
  });

  it("takes an irregular plural where the -s is wrong", () => {
    expect(counted(1, "category", "categories")).toBe("1 category");
    expect(counted(3, "category", "categories")).toBe("3 categories");
  });
});
