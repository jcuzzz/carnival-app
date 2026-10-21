import { describe, expect, it } from "vitest";
import type { CarnivalEvent, EventDefinition } from "../../api/types";
import { firstUndoneStep, setupSteps, type SetupFacts } from "./setupSteps";

/**
 * The checklist's done states are derived from reads that can each be missing
 * (ADR-0054 §1). What must never happen: a step claiming a fact off a read that
 * has not landed.
 */

const known: SetupFacts = {
  carnivalId: "c-1",
  status: "Open",
  participation: [{ houseCode: "BANKSIA", competitors: 40, entered: 0, recorded: 0 }],
  canImport: false,
  profile: { ageGroups: ["12"], categories: ["M"], tiers: ["Competitive"] },
  events: [{ id: "e-1", status: "Open" } as CarnivalEvent],
  schedule: { carnivalId: "c-1", streams: [] },
};

function step(facts: SetupFacts, id: string) {
  return setupSteps(facts).find((s) => s.id === id)!;
}

describe("setupSteps — the Open row", () => {
  it("says how many divisions are still Scheduled", () => {
    const facts = {
      ...known,
      events: [{ id: "e-1", status: "Scheduled" } as CarnivalEvent, known.events![0]!],
    };
    expect(step(facts, "open").detail).toBe("Open · 1 division still Scheduled");
  });

  it("says every division is open only when the list says so", () => {
    expect(step(known, "open").detail).toBe("Open · every division is open");
  });

  it("claims nothing about divisions while the event list is unread", () => {
    const detail = step({ ...known, events: undefined }, "open").detail;
    expect(detail).toBe("Open");
    expect(detail).not.toContain("division");
  });
});

/** ADR-0057 §2: a roster the organiser has not acted on still ticks the step. */
describe("setupSteps — the roster default", () => {
  const onDefault: SetupFacts = {
    ...known,
    profile: null,
    profileDefault: {
      ageGroups: ["12", "13", "14"],
      categories: ["M", "F"],
      tiers: ["Competitive"],
    },
  };

  it("reads done, names the roster's own ages, and offers a change", () => {
    const divisions = step(onDefault, "divisions");
    expect(divisions.done).toBe(true);
    expect(divisions.detail).toBe("Ages from the roster: 12–14 · Boys, Girls · Competitive");
    expect(divisions.action).toBe("Change");
    expect(firstUndoneStep(setupSteps(onDefault))).toBeUndefined();
  });

  it("leaves a saved profile reading as the profile, not as the roster", () => {
    const divisions = step({ ...onDefault, profile: known.profile }, "divisions");
    expect(divisions.detail).toBe("1 age · 1 category · 1 tier");
    expect(divisions.action).toBe("Add divisions");
  });

  it("stays required with neither a profile nor a roster", () => {
    const facts: SetupFacts = { ...known, profile: null, profileDefault: null, events: [] };
    expect(step(facts, "divisions").done).toBe(false);
    expect(step(facts, "divisions").action).toBe("Add divisions");
    expect(firstUndoneStep(setupSteps(facts))?.id).toBe("divisions");
  });
});

describe("setupSteps — unknown is not undone", () => {
  it("leaves a step whose read failed out of the pointer", () => {
    const facts: SetupFacts = { ...known, participation: null, profile: undefined, events: [] };
    expect(step(facts, "roster").done).toBeNull();
    expect(step(facts, "divisions").done).toBeNull();
    expect(firstUndoneStep(setupSteps(facts))?.id).toBe("events");
  });

  it("never points at the optional program step", () => {
    const facts: SetupFacts = { ...known, schedule: { carnivalId: "c-1", streams: [] } };
    expect(step(facts, "program").optional).toBe(true);
    expect(firstUndoneStep(setupSteps(facts))).toBeUndefined();
  });
});

/** ADR-0059 §3: the Events row counts both lists the setup screen draws. */
describe("setupSteps — the Events row", () => {
  function definition(id: string, sport = "Swimming"): EventDefinition {
    return {
      id,
      code: id.toUpperCase(),
      name: id,
      sport,
      isActive: true,
      usage: { carnivals: 0, divisions: 0, schoolRecords: 0, results: 0 },
    } as EventDefinition;
  }

  const catalogue = [definition("def-1"), definition("def-2"), definition("def-3", "Athletics")];
  const here = [{ id: "e-1", eventDefinitionId: "def-1", status: "Open" } as CarnivalEvent];

  it("reads what is in this carnival against what is left to add", () => {
    const facts: SetupFacts = {
      ...known,
      events: here,
      definitions: catalogue,
      sport: "Swimming",
    };
    expect(step(facts, "events").detail).toBe("1 in this carnival · 1 available to add");
  });

  it("counts a retired definition on neither side", () => {
    const facts: SetupFacts = {
      ...known,
      events: here,
      definitions: [...catalogue, { ...definition("def-old"), isActive: false }],
      sport: "Swimming",
    };
    expect(step(facts, "events").detail).toBe("1 in this carnival · 1 available to add");
  });

  it("falls back to the division count while the catalogue is unread", () => {
    const facts: SetupFacts = { ...known, events: here, definitions: undefined };
    expect(step(facts, "events").detail).toBe("1 division across 1 event");
  });

  it("claims neither count while the event list is unread", () => {
    const facts: SetupFacts = { ...known, events: undefined, definitions: catalogue };
    expect(step(facts, "events").done).toBeNull();
    expect(step(facts, "events").detail).toBe(
      "The event catalogue and which divisions of it run here",
    );
  });
});

/** ADR-0060 §4: a roster imported for another year is loud here, never blocking. */
describe("setupSteps — the roster's year", () => {
  const dated: SetupFacts = { ...known, carnivalDate: "2027-02-25" };

  it("warns, naming both years and the import, while the step stays done", () => {
    const roster = step({ ...dated, rosterYear: 2026 }, "roster");
    expect(roster.done).toBe(true);
    expect(roster.warning).toBe(
      "The roster’s age groups are for 2026, but this carnival is in 2027. " +
        "Import the roster again for 2027 under School → Roster import.",
    );
    expect(firstUndoneStep(setupSteps({ ...dated, rosterYear: 2026 }))).toBeUndefined();
  });

  it("says nothing when the years agree", () => {
    expect(step({ ...dated, rosterYear: 2027 }, "roster").warning).toBeUndefined();
  });

  it("says nothing with no roster, or before the settings read lands", () => {
    expect(step({ ...dated, rosterYear: null }, "roster").warning).toBeUndefined();
    expect(step({ ...dated, rosterYear: undefined }, "roster").warning).toBeUndefined();
  });
});
