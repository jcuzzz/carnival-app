import { describe, expect, it } from "vitest";
import type { CarnivalEvent } from "../../api/types";
import {
  divisionSelectorModel,
  type DivisionAxis,
  type DivisionSelectorModel,
} from "./divisionSelectorModel";

/**
 * The selector's derivation (ADR-0018 §2, owner refinement 2026-08-14,
 * ADR-0042 §2): one picker per axis, drawn from the divisions that EXIST, and
 * every item resolves to a division to navigate to — the tapped value, holding
 * the page's value on the other axes, and the nearest age where that
 * combination has none.
 */

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
    status: "Open",
    entryCount: 0,
    resultCount: 0,
    officials: [],
    ...over,
  } as CarnivalEvent;
}

const division = (id: string, age: string, category: string | null, tier = "Competitive") =>
  event({
    id,
    ageGroup: age,
    category: category as CarnivalEvent["category"],
    tier: tier as CarnivalEvent["tier"],
  });

/** Five divisions across both categories. The boys swim no 14. */
const dense = [
  division("f12", "12", "F"),
  division("m12", "12", "M"),
  division("f13", "13", "F"),
  division("m13", "13", "M"),
  division("f14", "14", "F"),
];

const byId = (id: string) => dense.find((d) => d.id === id)!;

function picker(model: DivisionSelectorModel, axis: DivisionAxis) {
  return model.pickers.find((p) => p.axis === axis);
}

/** One picker as `value → the id its item opens`. */
function targets(model: DivisionSelectorModel, axis: DivisionAxis): Record<string, string> {
  return Object.fromEntries((picker(model, axis)?.items ?? []).map((c) => [c.key, c.event.id]));
}

describe("divisionSelectorModel", () => {
  it("gives a sparse definition a picker per axis, single-value ones included", () => {
    // A one-item picker is not a wasted control: it is the only thing naming
    // that axis now the meta line is gone (ADR-0042 §1).
    const divisions = [division("f13", "13", "F"), division("m14", "14", "M")];
    const model = divisionSelectorModel(divisions, "def-free", { current: divisions[0]! });
    // The age axis holds one value, so it cannot be the naming one: a static
    // face carries no count, no badge and no `aria-current`.
    expect(model.pickers.map((p) => [p.axis, p.label, p.names, p.value])).toEqual([
      ["category", "Category", true, "Girls"],
      ["age", "Age group", false, "13"],
    ]);
    expect(picker(model, "category")!.items.map((c) => [c.label, c.event.id, c.current])).toEqual([
      ["13 · Girls", "f13", true],
      ["14 · Boys", "m14", false],
    ]);
    expect(picker(model, "age")!.items.map((c) => c.label)).toEqual(["13"]);
  });

  it("scopes each picker by the coarser ones, and only the finest names divisions", () => {
    const model = divisionSelectorModel(dense, "def-free", { current: byId("f13") });
    expect(model.pickers.map((p) => [p.axis, p.label, p.names])).toEqual([
      ["category", "Category", false],
      ["age", "Age group", true],
    ]);
  });

  it("keeps a picker on every axis where the Social twin narrows to one division", () => {
    // The Social corner: the twin is one girls' age, so under it there is no
    // category and no age left to choose — but both axes still have to say what
    // the page is, so both keep a picker.
    const corner = [
      division("f13", "13", "F"),
      division("f14", "14", "F"),
      division("m13", "13", "M"),
      division("m14", "14", "M"),
      event({
        id: "social",
        ageGroup: "16",
        category: "F",
        tier: "Social",
      }),
    ];
    const model = divisionSelectorModel(corner, "def-free", { current: corner[4]! });
    // Tier is the only axis left with a choice on it, so it is the one that
    // names — and its items already resolve to whole, distinct divisions.
    expect(model.pickers.map((p) => [p.axis, p.value, p.items.length, p.names])).toEqual([
      ["tier", "Social", 2, true],
      ["category", "Girls", 1, false],
      ["age", "16", 1, false],
    ]);
    expect(picker(model, "tier")!.items.map((c) => c.label)).toEqual([
      "14 · Girls",
      "16 · Girls · Social",
    ]);
    expect(targets(model, "tier")).toEqual({ Competitive: "f14", Social: "social" });
  });

  it("carries the Social suffix on the naming value where no tier picker names it", () => {
    // ADR-0014 §5: the twin must never read like its Competitive counterpart,
    // and with one tier in play there is no tier picker to say which it is.
    const social = [division("s13", "13", "F", "Social"), division("s14", "14", "F", "Social")];
    const model = divisionSelectorModel(social, "def-free", { current: social[0]! });
    expect(picker(model, "tier")).toBeUndefined();
    expect(picker(model, "age")!.value).toBe("13 · Social");
  });

  it("navigates on EVERY item — a coarse item holds the page's other axes", () => {
    const model = divisionSelectorModel(dense, "def-free", { current: byId("f13") });
    // Tapping Boys while the page is 13 · Girls opens 13 · Boys: the tap changes
    // the page, and the division it lands on is named on the item, the closed
    // button and the URL (owner refinement 2026-08-14).
    expect(targets(model, "category")).toEqual({ F: "f13", M: "m13" });
    expect(targets(model, "age")).toEqual({ "12": "f12", "13": "f13", "14": "f14" });
  });

  it("lands on the nearest existing age where the tapped scope has none", () => {
    // The girls swim a 14; the boys do not. A dead end on a pool deck is worse
    // than an approximate landing.
    const model = divisionSelectorModel(dense, "def-free", { current: byId("f14") });
    expect(targets(model, "category")).toEqual({ F: "f14", M: "m13" });
  });

  it("takes the SAME age scope before it measures a distance to anything", () => {
    // `ageScopeOrder` is lossy on purpose: it ranks every non-numeric code
    // alike. Distance arithmetic alone therefore calls two different ages
    // equidistant and can skip the exact one that exists.
    const named = [
      division("jm", "Junior", "M"),
      division("jf", "Junior", "F"),
      division("sm", "Senior", "M"),
      division("sf", "Senior", "F"),
      division("om", "OPEN", "M"),
    ];
    const senior = divisionSelectorModel(named, "def-free", { current: named[2]! });
    expect(targets(senior, "category")).toEqual({ F: "sf", M: "sm" });

    // The same collision between two non-numeric codes, which rank alike.
    const codes = [
      division("mj", "JNR", "M"),
      division("fj", "JNR", "F"),
      division("ms", "SNR", "M"),
      division("fs", "SNR", "F"),
      division("m17", "17", "M"),
    ];
    const model = divisionSelectorModel(codes, "def-free", { current: codes[2]! });
    expect(targets(model, "category")).toEqual({ F: "fs", M: "ms" });
  });

  it("relaxes a held axis rather than offering an item that opens nothing", () => {
    // The tier item on a definition whose Social twin is a girls' division:
    // tapping Social from 13 · Boys crosses category unasked. Deliberate — an
    // approximate landing names itself, and the alternative is a dead end
    // (ADR-0018 §2, owner refinement 2026-08-14).
    const lopsided = [
      division("m12", "12", "M"),
      division("m13", "13", "M"),
      division("m14", "14", "M"),
      division("f13", "13", "F"),
      event({
        id: "social",
        ageGroup: "16",
        category: "F",
        tier: "Social",
      }),
    ];
    const model = divisionSelectorModel(lopsided, "def-free", { current: lopsided[1]! });
    expect(targets(model, "tier")).toEqual({ Competitive: "m13", Social: "social" });
  });

  it("breaks a nearest-age tie to the younger division", () => {
    const gap = [
      division("m12", "12", "M"),
      division("f13", "13", "F"),
      division("m14", "14", "M"),
      division("f15", "15", "F"),
      division("f16", "16", "F"),
    ];
    const model = divisionSelectorModel(gap, "def-free", { current: gap[1]! });
    // 12 and 14 are both one step from 13.
    expect(targets(model, "category")).toEqual({ F: "f13", M: "m12" });
  });

  it("enumerates only the ages that exist under the current coarser scope", () => {
    // No disabled placeholders: the picker is what is there, so every item moves.
    const model = divisionSelectorModel(dense, "def-free", { current: byId("m13") });
    expect(picker(model, "age")!.items.map((c) => c.key)).toEqual(["12", "13"]);
    expect(picker(model, "age")!.items.every((c) => c.event.category === "M")).toBe(true);
  });

  it("puts the whole division on a naming item's face, Social suffix included", () => {
    // The item face IS the accessible name, and a Competitive division and its
    // Social twin have identical eligibility (ADR-0014 §5).
    const twins = [
      division("f12", "12", "F"),
      division("f13", "13", "F"),
      division("f14", "14", "F"),
      division("s13", "13", "F", "Social"),
      division("s14", "14", "F", "Social"),
    ];
    const competitive = divisionSelectorModel(twins, "def-free", { current: twins[1]! });
    expect(competitive.pickers.map((p) => p.axis)).toEqual(["tier", "category", "age"]);
    expect(
      picker(competitive, "tier")!.items.map((c) => [c.label, c.event.id, c.current]),
    ).toEqual([
      ["Competitive", "f13", true],
      ["Social", "s13", false],
    ]);
    expect(picker(competitive, "age")!.items.map((c) => c.label)).toEqual([
      "12 · Girls",
      "13 · Girls",
      "14 · Girls",
    ]);

    // The twin's page must not offer the same ages: the wrong-tier entry is
    // accepted, and a Social result can never become a record.
    const social = divisionSelectorModel(twins, "def-free", { current: twins[3]! });
    expect(picker(social, "age")!.items.map((c) => c.label)).toEqual([
      "13 · Girls · Social",
      "14 · Girls · Social",
    ]);
    // The 12s have no Social twin, so that age is simply not in the Social
    // picker — and the tier item back to Competitive holds the age the page is on.
    expect(targets(social, "tier")).toEqual({ Competitive: "f13", Social: "s13" });
  });

  it("marks the current value in every picker, and names the division in one", () => {
    const model = divisionSelectorModel(dense, "def-free", { current: byId("m13") });
    const current = model.pickers.flatMap((p) =>
      p.items.filter((c) => c.current).map((c) => c.label),
    );
    expect(current).toEqual(["Boys", "13 · Boys"]);
    // The closed buttons read the page's own division, one axis each.
    expect(model.pickers.map((p) => p.value)).toEqual(["Boys", "13"]);
    // Exactly one picker carries the page's own name, and it is the finest.
    expect(model.pickers.filter((p) => p.names).map((p) => p.axis)).toEqual(["age"]);
  });

  it("treats a null category as a value of its own, spelled the way every surface spells it", () => {
    const mixed = [
      division("x12", "12", null),
      division("x13", "13", null),
      division("f12", "12", "F"),
      division("f13", "13", "F"),
      division("f14", "14", "F"),
    ];
    const model = divisionSelectorModel(mixed, "def-free", { current: mixed[0]! });
    // The category picker is values, so the uncategorised item says it has none —
    // "All categories" beside "Girls" would read as a filter over both.
    expect(picker(model, "category")!.items.map((c) => c.label)).toEqual(["No category", "Girls"]);
    expect(picker(model, "category")!.value).toBe("No category");
    expect(picker(model, "age")!.items.map((c) => c.label)).toEqual([
      "12 · All categories",
      "13 · All categories",
    ]);
  });

  it("orders an open age scope on the ladder and still names it", () => {
    const withOpen = [
      event({ id: "open", ageGroup: "OPEN", category: "F" }),
      division("f12", "12", "F"),
      division("f13", "13", "F"),
      division("f14", "14", "F"),
      division("m12", "12", "M"),
    ];
    const model = divisionSelectorModel(withOpen, "def-free", { current: withOpen[1]! });
    // Open leads the ladder (eventLabels.ageScopeOrder).
    expect(picker(model, "age")!.items.map((c) => c.label)).toEqual([
      "All ages · Girls",
      "12 · Girls",
      "13 · Girls",
      "14 · Girls",
    ]);
  });

  it("spells the open sentinel as a value, never as `OPEN`", () => {
    // One age value, so this face is `axisValueLabel`'s rather than a division
    // chip's — and every other surface reads an open axis as "All ages".
    const allAges = [
      event({ id: "openF", ageGroup: "OPEN", category: "F" }),
      event({ id: "openM", ageGroup: "OPEN", category: "M" }),
    ];
    const model = divisionSelectorModel(allAges, "def-free", { current: allAges[0]! });
    expect(picker(model, "age")!.value).toBe("All ages");
    expect(picker(model, "age")!.items.map((c) => c.label)).toEqual(["All ages"]);
  });

  it("marks the official's assigned divisions, and only on a division-naming item", () => {
    const assigned = {
      ...dense[4]!,
      officials: [{ subjectId: "olivia", subjectDisplayName: "Olivia" }],
    };
    const model = divisionSelectorModel([...dense.slice(0, 4), assigned], "def-free", {
      current: dense[0]!,
      subject: "olivia",
    });
    expect(picker(model, "age")!.items.map((c) => c.assigned)).toEqual([false, false, true]);
    // An assignment is a fact about a DIVISION; a category item is a value, and
    // badging it says something the official cannot act on.
    expect(picker(model, "category")!.items.every((c) => !c.assigned)).toBe(true);
  });

  it("still names the division where no axis has anything to choose", () => {
    // Divisions varying on no axis at all should be impossible — the server's
    // uniqueness key is exactly these three — but a wrong guess about a server
    // invariant must not leave the page unnamed.
    const identical = Array.from({ length: 5 }, (_, i) => division(`d${i}`, "13", "F"));
    const model = divisionSelectorModel(identical, "def-free", { current: identical[0]! });
    expect(model.pickers.map((p) => [p.axis, p.value, p.items.length])).toEqual([
      ["category", "Girls", 1],
      ["age", "13", 1],
    ]);
  });

  it("offers nothing where there is nothing to choose between", () => {
    const one = division("f13", "13", "F");
    expect(divisionSelectorModel([one], "def-free", { current: one }).divisions).toHaveLength(1);
    // No siblings read at all: no picker can be derived, and the screen falls
    // back to naming the division off the page's own event.
    const offline = divisionSelectorModel(undefined, "def-free", { current: one });
    expect(offline.divisions).toEqual([]);
    expect(offline.pickers).toEqual([]);
    // Another definition's divisions are not this definition's siblings.
    const other = event({ id: "back", eventDefinitionId: "def-back" });
    expect(
      divisionSelectorModel([one, other], "def-free", { current: one }).divisions.map((d) => d.id),
    ).toEqual(["f13"]);
  });
});
