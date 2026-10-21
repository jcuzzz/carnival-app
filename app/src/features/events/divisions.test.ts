import { describe, expect, it } from "vitest";
import type { CarnivalEvent } from "../../api/types";
import { divisionChipLabel, siblingDivisions } from "./divisions";

function event(over: Partial<CarnivalEvent> & Pick<CarnivalEvent, "id">): CarnivalEvent {
  return {
    carnivalId: "c-1",
    eventDefinitionId: "def-free",
    definitionCode: "SW-50-FREE",
    definitionName: "Freestyle 50m",
    ageGroup: "13",
    category: "F",
    tier: "Competitive",
    scoringMode: null,
    effectiveScoringMode: "Places",
    scoresPoints: true,
    scheduleSlotId: null,
    slotOrder: null,
    status: "Open",
    version: 1,
    entryCount: 0,
    resultCount: 0,
    officials: [],
    ...over,
  } as CarnivalEvent;
}

describe("siblingDivisions", () => {
  const events = [
    event({ id: "social", ageGroup: "15", tier: "Social" }),
    event({ id: "f14", ageGroup: "14" }),
    event({ id: "m13", ageGroup: "13", category: "M" }),
    event({ id: "f13" }),
    event({ id: "backstroke", eventDefinitionId: "def-back" }),
  ];

  it("offers only the instances that exist, of this definition, up the age ladder", () => {
    expect(siblingDivisions(events, { eventDefinitionId: "def-free" }).map((e) => e.id))
      // 13 F before 13 M, then 14, then the 15 Social twin. Nothing enumerates a
      // division space: an age nobody contests has no chip.
      .toEqual(["f13", "m13", "f14", "social"]);
  });

  it("excludes other definitions", () => {
    const siblings = siblingDivisions(events, { eventDefinitionId: "def-back" });
    expect(siblings.map((e) => e.id)).toEqual(["backstroke"]);
  });
});

describe("divisionChipLabel", () => {
  it("states the age and the category, and never says sex or gender", () => {
    expect(divisionChipLabel(event({ id: "x", ageGroup: "13", category: "F" }))).toBe(
      "13 · Girls",
    );
  });

  it("states a non-numeric age as the code the roster derives", () => {
    expect(divisionChipLabel(event({ id: "x", ageGroup: "SNR", category: "M" }))).toBe(
      "SNR · Boys",
    );
  });

  it("says All ages for the open age axis", () => {
    expect(divisionChipLabel(event({ id: "x", ageGroup: "OPEN" }))).toBe("All ages · Girls");
  });

  it("distinguishes a Social twin from the Competitive event it shadows", () => {
    const competitive = event({ id: "c" });
    const social = event({ id: "s", tier: "Social" });
    expect(divisionChipLabel(competitive)).toBe("13 · Girls");
    expect(divisionChipLabel(social)).toBe("13 · Girls · Social");
    expect(divisionChipLabel(competitive)).not.toBe(divisionChipLabel(social));
  });
});
