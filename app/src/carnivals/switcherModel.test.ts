import { describe, expect, it } from "vitest";
import type { Carnival, CarnivalStatus } from "../api/types";
import { SWITCHER_ROW_LIMIT, switcherList } from "./switcherModel";

function carnival(
  id: string,
  date: string,
  status: CarnivalStatus = "Finalised",
  name = `Carnival ${id}`,
): Carnival {
  return {
    id,
    name,
    date,
    status,
    type: "Swimming",
    venue: "Preston Pool",
    ownerSubject: "s1",
    ruleVersion: "v1",
    version: 1,
  } as Carnival;
}

function many(count: number): Carnival[] {
  return Array.from({ length: count }, (_, i) =>
    carnival(`c-${i}`, `20${String(10 + (i % 80)).padStart(2, "0")}-03-06`),
  );
}

describe("switcherList", () => {
  it("never renders more than the limit, however many carnivals exist", () => {
    expect(switcherList(many(500)).rows).toHaveLength(SWITCHER_ROW_LIMIT);
    expect(switcherList(many(500)).truncated).toBe(true);
    expect(switcherList(many(500)).total).toBe(500);
  });

  it("puts the Open carnival first even when a Draft one is dated later", () => {
    const list = [
      carnival("draft", "2027-11-02", "Draft", "Athletics 2027"),
      carnival("open", "2027-03-06", "Open", "Swimming 2027"),
      carnival("old", "2026-03-07", "Finalised", "Swimming 2026"),
    ];

    expect(switcherList(list).rows.map((c) => c.id)).toEqual(["open", "draft", "old"]);
  });

  it("keeps the live carnival visible when the list is far past the limit", () => {
    const list = [...many(300), carnival("live", "2019-03-06", "Open", "Swimming 2027")];

    expect(switcherList(list).rows[0]!.id).toBe("live");
  });

  it("filters on name, year, venue and status, and stays bounded while filtered", () => {
    const list = [
      ...many(300),
      carnival("athletics", "2024-08-01", "Finalised", "Athletics 2024"),
    ];

    expect(switcherList(list, "athletics").rows.map((c) => c.id)).toEqual(["athletics"]);
    expect(switcherList(list, "ATHLETICS 2024").matched).toBe(1);
    expect(switcherList(list, "preston").rows).toHaveLength(SWITCHER_ROW_LIMIT);
    expect(switcherList(list, "finalised").rows).toHaveLength(SWITCHER_ROW_LIMIT);
  });

  it("reports no matches rather than falling back to the unfiltered list", () => {
    const view = switcherList(many(20), "cross-country");

    expect(view.matched).toBe(0);
    expect(view.rows).toEqual([]);
    expect(view.total).toBe(20);
  });

  it("does not reorder or mutate the caller's array", () => {
    const list = [carnival("a", "2020-01-01"), carnival("b", "2027-01-01", "Open")];
    switcherList(list);

    expect(list.map((c) => c.id)).toEqual(["a", "b"]);
  });
});
