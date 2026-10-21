import { describe, expect, it } from "vitest";
import type { CarnivalEvent, DivisionProfile } from "../../api/types";
import { allCellKeys, buildDivisionGrid, tickedSpecs } from "./divisionGridModel";

/**
 * The grid answers "what does this carnival contest", not "what does its
 * profile describe" (ADR-0022 §2). The two disagree the moment an age is edited
 * out of the profile, and the divisions generated from it are still run.
 */

const profile: DivisionProfile = {
  ageGroups: ["12", "13"],
  categories: ["M", "F"],
  tiers: ["Competitive", "Social"],
};

function division(
  ageGroup: string,
  category: string | null,
  tier: CarnivalEvent["tier"] = "Competitive",
  extra: Partial<CarnivalEvent> = {},
): CarnivalEvent {
  return {
    id: `${ageGroup}-${category ?? "mixed"}-${tier}`,
    carnivalId: "c-1",
    eventDefinitionId: "def-free",
    ageGroup,
    category,
    tier,
    status: "Scheduled",
    ...extra,
  } as CarnivalEvent;
}

describe("buildDivisionGrid", () => {
  it("lays the profile out as ages × category/tier, none of them ticked", () => {
    const model = buildDivisionGrid(profile, []);

    expect(model.rows.map((r) => r.label)).toEqual(["12", "13"]);
    expect(model.columns.map((c) => c.label)).toEqual([
      "Boys · Comp",
      "Boys · Social",
      "Girls · Comp",
      "Girls · Social",
    ]);
    expect(model.rows.flatMap((r) => r.cells).every((c) => c.event === null)).toBe(true);
    expect(model.configured).toBe(true);
  });

  it("drops the tier from a column label when the carnival contests only one", () => {
    const model = buildDivisionGrid({ ...profile, tiers: ["Competitive"] }, []);
    expect(model.columns.map((c) => c.label)).toEqual(["Boys", "Girls"]);
  });

  it("marks a ticked cell with its division's entry count and status", () => {
    const model = buildDivisionGrid(profile, [
      division("13", "F", "Competitive", { id: "ev-13F", entryCount: 4, status: "Closed" }),
    ]);
    const cell = model.rows[1]!.cells.find((c) => c.label === "13 · Girls · Comp")!;

    expect(cell.event?.id).toBe("ev-13F");
    expect(cell.entryCount).toBe(4);
    expect(cell.status).toBe("Closed");
    expect(cell.outsideProfile).toBe(false);
  });

  it("still shows a division whose age the profile no longer names", () => {
    const model = buildDivisionGrid(profile, [division("11", "M")]);

    const row = model.rows.find((r) => r.label === "11")!;
    expect(row.outsideProfile).toBe(true);
    expect(row.cells.find((c) => c.label === "11 · Boys · Comp")!.event).not.toBeNull();
    expect(model.outsideProfile).toBe(1);
    // The profile's own ages are untouched by the extra one.
    expect(model.rows.map((r) => r.label)).toEqual(["12", "13", "11"]);
  });

  it("still shows a division whose category the profile no longer names", () => {
    const model = buildDivisionGrid(profile, [division("12", "S")]);

    const column = model.columns.find((c) => c.label === "Open · Comp")!;
    expect(column.outsideProfile).toBe(true);
    expect(model.outsideProfile).toBe(1);
  });

  it("gives a division with no category a column, and never calls it off profile", () => {
    const model = buildDivisionGrid(profile, [division("12", null)]);

    const column = model.columns.find((c) => c.label === "All categories · Comp")!;
    expect(column.outsideProfile).toBe(false);
    const cell = model.rows[0]!.cells.find((c) => c.label === "12 · All categories · Comp")!;
    expect(cell.event).not.toBeNull();
    expect(cell.outsideProfile).toBe(false);
    expect(model.outsideProfile).toBe(0);
  });

  it("still reads a no-category division off profile on the tier the profile does name", () => {
    const model = buildDivisionGrid({ ...profile, tiers: ["Competitive"] }, [
      division("12", null, "Social"),
    ]);

    expect(model.columns.find((c) => c.label === "All categories · Social")!.outsideProfile).toBe(
      true,
    );
    expect(model.outsideProfile).toBe(1);
  });

  it("reads a missing profile as not configured, and still shows what exists", () => {
    const model = buildDivisionGrid(null, [division("13", "M")]);

    expect(model.configured).toBe(false);
    expect(model.rows.map((r) => r.label)).toEqual(["13"]);
    expect(model.outsideProfile).toBe(1);
  });

  it("reads an empty profile as not configured rather than an empty matrix", () => {
    const model = buildDivisionGrid({ ageGroups: [], categories: [], tiers: [] }, []);

    expect(model.configured).toBe(false);
    expect(model.rows).toEqual([]);
    expect(model.columns).toEqual([]);
  });

  it("labels an open age and asks the server for a null age scope", () => {
    const open: DivisionProfile = {
      ageGroups: ["OPEN"],
      categories: ["M"],
      tiers: ["Competitive"],
    };
    const model = buildDivisionGrid(open, []);

    expect(model.rows[0]!.label).toBe("All ages");
    expect(model.rows[0]!.cells[0]!.spec.ageGroup).toBeNull();
  });

  it("names each age on the cell it generates, code and all", () => {
    const model = buildDivisionGrid({ ...profile, ageGroups: ["13", "SNR"] }, []);

    expect(model.rows.map((r) => r.label)).toEqual(["13", "SNR"]);
    expect(model.rows[1]!.cells[0]!.label).toBe("SNR · Boys · Comp");
    expect(model.rows[1]!.cells[0]!.spec).toEqual({
      ageGroup: "SNR",
      category: "M",
      tier: "Competitive",
    });
  });

  it("keeps the profile's declared order and drops a repeated age", () => {
    const model = buildDivisionGrid({ ...profile, ageGroups: ["13", "12", "13"] }, []);
    expect(model.rows.map((r) => r.key)).toEqual(["13", "12"]);
  });
});

describe("allCellKeys / tickedSpecs", () => {
  it("gives the add-an-event screen the whole matrix to pre-tick", () => {
    const model = buildDivisionGrid(profile, []);
    const all = allCellKeys(model);

    expect(all.size).toBe(8);
    expect(tickedSpecs(model, all)).toHaveLength(8);
  });

  it("returns only the ticked cells, in row-major order", () => {
    const model = buildDivisionGrid(profile, []);
    const first = model.rows[0]!.cells[0]!;
    const last = model.rows[1]!.cells[3]!;

    expect(tickedSpecs(model, new Set([last.key, first.key]))).toEqual([first.spec, last.spec]);
  });
});
