import { describe, expect, it } from "vitest";
import type { ResultRow } from "../../api/types";
import { groupResults, heatLabel } from "./resultsBook";

function row(over: Partial<ResultRow>): ResultRow {
  return {
    carnivalEventId: "ev-1",
    eventCode: "SW-50-FREE",
    eventName: "Freestyle 50m",
    division: "13 Boys",
    tier: "Competitive",
    heat: 1,
    place: 1,
    displayName: "Aiden Brook",
    houseCode: "BANKSIA",
    value: "31.20",
    measuredValue: 31.2,
    status: "Ok",
    jumps: null,
    failsAtBest: null,
    totalFails: null,
    ...over,
  };
}

describe("groupResults", () => {
  it("folds the server's flat rows into event and heat blocks, in the order sent", () => {
    const blocks = groupResults([
      row({ heat: 1, place: 1, displayName: "Aiden Brook" }),
      row({ heat: 1, place: 2, displayName: "Blake Chen" }),
      row({ heat: 2, place: 1, displayName: "Cora Diaz" }),
      row({ carnivalEventId: "ev-2", eventCode: "SW-NOVELTY", tier: "Social", heat: null, place: null }),
    ]);

    expect(blocks.map((b) => b.carnivalEventId)).toEqual(["ev-1", "ev-2"]);
    expect(blocks[0]!.heats.map((h) => h.heat)).toEqual([1, 2]);
    // Two heats, two firsts (ADR-0015 §1) — and the second one is NOT merged
    // into the first block just because it repeats place 1.
    expect(blocks[0]!.heats[0]!.rows.map((r) => r.displayName)).toEqual([
      "Aiden Brook",
      "Blake Chen",
    ]);
    expect(blocks[0]!.heats[1]!.rows[0]!.displayName).toBe("Cora Diaz");
    expect(blocks[1]!.tier).toBe("Social");
  });

  it("keeps an unheated field apart from heat 1 rather than folding it in", () => {
    // ADR-0015 §1: null is its own field, which is why it places separately.
    const blocks = groupResults([
      row({ heat: null, place: 1, displayName: "Unmarshalled" }),
      row({ heat: 1, place: 1, displayName: "Heated" }),
    ]);

    expect(blocks[0]!.heats.map((h) => h.heat)).toEqual([null, 1]);
    expect(blocks[0]!.heats.map((h) => h.key)).toEqual(["ev-1:unheated", "ev-1:1"]);
  });

  it("prints a field division split across flights as ONE table", () => {
    // ADR-0053 §6 amendment: the server ranks a non-Time division as one field,
    // so the book must not split it into two uncaptioned tables that each show
    // a 1st. The rows arrive in place order, flights interleaved.
    const blocks = groupResults(
      [
        row({ carnivalEventId: "ev-9", eventCode: "AT-LJ", heat: 2, place: 1, displayName: "Cora Diaz" }),
        row({ carnivalEventId: "ev-9", eventCode: "AT-LJ", heat: 1, place: 2, displayName: "Aiden Brook" }),
      ],
      new Map([["ev-9", "Distance"]]),
    );

    expect(blocks[0]!.heats).toHaveLength(1);
    expect(blocks[0]!.heats[0]!.heat).toBeNull();
    expect(blocks[0]!.heats[0]!.rows.map((r) => r.displayName)).toEqual([
      "Cora Diaz",
      "Aiden Brook",
    ]);
    // No caption either, so nothing on the page claims a flight it is not.
    expect(heatLabel(blocks[0]!.heats[0]!.heat, "Distance")).toBeNull();
  });

  it("names a heat, including the one nobody was marshalled into", () => {
    expect(heatLabel(3)).toBe("Heat 3");
    expect(heatLabel(null)).toBe("No heat");
  });

  it("names no heat at all on a field event (ADR-0053 §6)", () => {
    // The deck says nothing about heats there, and the book prints the same.
    expect(heatLabel(1, "Distance")).toBeNull();
    expect(heatLabel(null, "Distance")).toBeNull();
    expect(heatLabel(1, "Time")).toBe("Heat 1");
  });

  it("has nothing to group when nothing was recorded", () => {
    expect(groupResults([])).toEqual([]);
  });
});
