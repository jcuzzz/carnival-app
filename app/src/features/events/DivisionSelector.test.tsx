// @vitest-environment jsdom
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it } from "vitest";
import type { CarnivalEvent } from "../../api/types";
import type { OutboxItem } from "../../db/db";
import { DivisionSelector } from "./DivisionSelector";
import { divisionChipLabel } from "./divisions";
import { queuedProgressByEvent } from "./progress";

/**
 * The selector on screen (ADR-0018 §2, ADR-0042 §2): one closed button per
 * axis, always reading the division on screen, and every item inside a real
 * link — in-page switching is rejected (ADR-0014 §3). The controls are a
 * labelled GROUP, not a landmark, and the menus are portalled, so items are
 * queried off `screen` rather than within the group.
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
    entryCount: 6,
    resultCount: 4,
    officials: [],
    ...over,
  } as CarnivalEvent;
}

const division = (id: string, age: string, category: string, tier = "Competitive") =>
  event({
    id,
    ageGroup: age,
    category: category as CarnivalEvent["category"],
    tier: tier as CarnivalEvent["tier"],
    entryCount: 4,
    resultCount: 1,
  });

/** Both categories over three ages — and the boys have no 14. */
const dense = [
  division("f12", "12", "F"),
  division("m12", "12", "M"),
  division("f13", "13", "F"),
  division("m13", "13", "M"),
  division("f14", "14", "F"),
];

/** One picker open at a time (ADR-0042 §3), so each test opens exactly one. */
const open = (name: string) => fireEvent.click(screen.getByRole("button", { name }));
const item = (name: string | RegExp) => screen.getByRole("menuitem", { name });
const href = (el: Element | null) => el?.getAttribute("href");
const statics = () =>
  [...document.querySelectorAll(".picker-trigger.static")].map((el) => el.textContent);

beforeEach(cleanup);

describe("DivisionSelector", () => {
  it("is a group, not a landmark, and names the division on the closed buttons", () => {
    render(<DivisionSelector definitionId="def-free" events={dense} current={dense[2]!} />);
    expect(screen.queryByRole("navigation")).toBeNull();
    expect(
      screen.getAllByRole("button").map((b) => b.textContent?.replace(/\s+/g, " ").trim()),
    ).toEqual(["Category Girls", "Age group 13"]);
    expect(screen.getByRole("group", { name: "Divisions" })).toBeTruthy();
  });

  it("names an axis with only one value instead of offering a menu", () => {
    const girls = [
      event({ id: "f13" }),
      event({ id: "f14", ageGroup: "14", entryCount: 4, resultCount: 1 }),
    ];
    render(<DivisionSelector definitionId="def-free" events={girls} current={girls[0]!} />);
    expect(statics()).toEqual(["Category Girls"]);

    open("Age group 13");
    expect(href(item(/^14 · Girls/))).toBe("#/carnivals/c-1/events/f14");
  });

  it("names the divisions on the category picker where the age axis holds one value", () => {
    // The naming axis is the finest one with a CHOICE on it: an age that cannot
    // be switched renders static, and a static face carries no count, no badge
    // and no `aria-current` — so the category picker has to take all three.
    const oneAge = [division("f13", "13", "F"), division("m13", "13", "M")];
    render(<DivisionSelector definitionId="def-free" events={oneAge} current={oneAge[0]!} />);
    expect(statics()).toEqual(["Age group 13"]);

    open("Category Girls");
    expect(href(item(/^13 · Boys/))).toBe("#/carnivals/c-1/events/m13");
    expect(item(/^13 · Boys/).textContent).toContain("1/4");
    const currents = document.querySelectorAll("[aria-current='page']");
    expect(currents).toHaveLength(1);
    expect(currents[0]!.textContent).toContain("13 · Girls");
  });

  it("makes every item a link, the coarse picker included", () => {
    render(<DivisionSelector definitionId="def-free" events={dense} current={dense[2]!} />);
    // Tapping Boys while the page is 13 · Girls goes straight to 13 · Boys: no
    // toggle state, no status line, nothing that changes without moving the page.
    expect(screen.queryByRole("status")).toBeNull();
    open("Category Girls");
    expect(href(item("Boys"))).toBe("#/carnivals/c-1/events/m13");

    cleanup();
    render(<DivisionSelector definitionId="def-free" events={dense} current={dense[2]!} />);
    open("Age group 13");
    expect(href(item(/^12 · Girls/))).toBe("#/carnivals/c-1/events/f12");
  });

  it("sends a category item to the nearest existing age rather than nowhere", () => {
    // The girls swim a 14 and the boys do not; a dead end on a pool deck is
    // worse than an approximate landing, which the next page names in full.
    render(<DivisionSelector definitionId="def-free" events={dense} current={dense[4]!} />);
    open("Category Girls");
    expect(href(item("Boys"))).toBe("#/carnivals/c-1/events/m13");
  });

  it("keeps aria-current on the picker that names the division, and nowhere else", () => {
    render(<DivisionSelector definitionId="def-free" events={dense} current={dense[2]!} />);
    open("Age group 13");
    const currents = document.querySelectorAll("[aria-current='page']");
    expect(currents).toHaveLength(1);
    expect(currents[0]!.textContent).toContain("13 · Girls");

    // The category picker still shows where the page sits — as a marked current
    // item, not a link and not a second claim to be the page.
    cleanup();
    render(<DivisionSelector definitionId="def-free" events={dense} current={dense[2]!} />);
    open("Category Girls");
    const scope = item("Girls");
    expect(scope.tagName).toBe("DIV");
    expect(scope.className).toContain("current");
    expect(scope.getAttribute("aria-current")).toBeNull();
    expect(document.querySelectorAll("[aria-current='page']")).toHaveLength(0);
  });

  it("lets the TIER picker name the division when it is the only axis with a choice", () => {
    // A Social twin on a definition with one age and one category: tier is the
    // finest axis holding a choice, so it takes the whole division label and the
    // page's single `aria-current` — and the two static faces take neither.
    const corner = [division("f13", "13", "F"), division("s13", "13", "F", "Social")];
    render(<DivisionSelector definitionId="def-free" events={corner} current={corner[1]!} />);
    expect(statics()).toEqual(["Category Girls", "Age group 13"]);
    expect(document.querySelectorAll("[aria-current='page']")).toHaveLength(0);

    open("Tier Social");
    const currents = document.querySelectorAll("[aria-current='page']");
    expect(currents).toHaveLength(1);
    expect(currents[0]!.textContent).toContain(divisionChipLabel(corner[1]!));
  });

  it("renders a tier picker only where both tiers exist, and never twice alike", () => {
    render(<DivisionSelector definitionId="def-free" events={dense} current={dense[2]!} />);
    expect(screen.queryByRole("button", { name: /^Tier/ })).toBeNull();

    cleanup();
    const twins = [
      division("f12", "12", "F"),
      division("f13", "13", "F"),
      division("f14", "14", "F"),
      division("s13", "13", "F", "Social"),
      division("s14", "14", "F", "Social"),
    ];
    render(<DivisionSelector definitionId="def-free" events={twins} current={twins[3]!} />);
    open("Tier Social");
    expect(href(item("Competitive"))).toBe("#/carnivals/c-1/events/f13");

    // ADR-0014 §5: the twin must not read identically — the wrong-tier entry is
    // accepted, and a Social result can never become a record. So the suffix is
    // on the face of every age item in the Social scope.
    cleanup();
    render(<DivisionSelector definitionId="def-free" events={twins} current={twins[3]!} />);
    open("Age group 13");
    expect(screen.getAllByRole("menuitem", { name: /Social/ })).toHaveLength(2);
  });

  it("carries each division's own progress on the items that name one", () => {
    const queued = queuedProgressByEvent([
      {
        id: "item-1",
        kind: "result.submit",
        payload: { carnivalEventId: "f14", entryId: "en-1" },
        actorSubject: "olivia",
        capturedAt: "2026-08-13T04:00:00Z",
        status: "queued",
        attempts: 0,
      } satisfies OutboxItem,
    ]);
    render(
      <DivisionSelector
        definitionId="def-free"
        events={dense}
        current={dense[2]!}
        queued={queued}
      />,
    );
    open("Age group 13");
    // 1 recorded on the server + 1 still on this device, over 4 entries.
    expect(item(/^14 · Girls/).textContent).toContain("2/4");

    // A category item stands for no single division, so it carries no count.
    cleanup();
    render(
      <DivisionSelector
        definitionId="def-free"
        events={dense}
        current={dense[2]!}
        queued={queued}
      />,
    );
    open("Category Girls");
    expect(item("Boys").textContent).toBe("Boys");
  });

  it("badges assignment on division-naming items only, never on a scope", () => {
    // "Boys yours" is not a thing an official can act on, and it would rename
    // the item for anything matching on its accessible name.
    const assigned = {
      ...dense[4]!,
      officials: [{ subjectId: "olivia", subjectDisplayName: "Olivia Official" }],
    };
    const events = [...dense.slice(0, 4), assigned];
    render(
      <DivisionSelector
        definitionId="def-free"
        events={events}
        current={dense[0]!}
        subject="olivia"
      />,
    );
    open("Age group 12");
    expect(item(/^14 · Girls/).textContent).toContain("yours");
    expect(item(/^13 · Girls/).textContent).not.toContain("yours");

    cleanup();
    render(
      <DivisionSelector
        definitionId="def-free"
        events={events}
        current={dense[0]!}
        subject="olivia"
      />,
    );
    open("Category Girls");
    expect(item("Boys")).toBeTruthy();
  });

  it("still names the division where there is nothing to switch to", () => {
    // ADR-0042 §1 deleted the meta line that used to say which division this
    // is, so the selector may never render nothing — not on a single-division
    // definition, and not with a sibling list no venue uplink could fetch.
    const one = event({ id: "f13" });
    render(<DivisionSelector definitionId="def-free" events={[one]} current={one} />);
    expect(statics()).toEqual(["Category Girls", "Age group 13"]);
    expect(screen.queryAllByRole("button")).toHaveLength(0);

    cleanup();
    render(<DivisionSelector definitionId="def-free" events={undefined} current={one} />);
    expect(statics()).toEqual(["Category Girls", "Age group 13"]);
    expect(screen.getByRole("group", { name: "Divisions" })).toBeTruthy();

    cleanup();
    const allAges = event({ id: "open", ageGroup: "OPEN" });
    render(<DivisionSelector definitionId="def-free" events={undefined} current={allAges} />);
    expect(statics()).toEqual(["Category Girls", "Age group All ages"]);
  });
});
