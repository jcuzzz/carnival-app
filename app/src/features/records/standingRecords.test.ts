import { describe, expect, it } from "vitest";
import type { SchoolRecord } from "../../api/types";
import { beatsRecord, bookRowKey, divisionKey, indexRecords } from "./standingRecords";

/**
 * The record badge's whole decision (ADR-0028 §5). Two things are load-bearing:
 * equalling a record is not beating it, and the DIRECTION is the server's
 * `higherBetter`, never re-derived from the measure here.
 */

const freestyle: SchoolRecord = {
  id: "rec-1",
  eventDefinitionId: "def-1",
  eventCode: "SW-50-FREE",
  eventName: "Freestyle 50m",
  ageGroup: "13",
  category: "M",
  division: "13, 14 Boys",
  value: 31.2,
  formattedValue: "31.20",
  higherBetter: false,
  holderDisplayName: "Aiden Brook",
  setOn: "2027-03-06",
  carnivalName: "Preston Swimming",
  // The book never carries a voided row (ADR-0035), so the badge's index only
  // ever sees these at their defaults.
  voided: false,
  voidedAtUtc: null,
  voidedBySubject: null,
  voidReason: null,
};

const shotPut: SchoolRecord = {
  ...freestyle,
  eventDefinitionId: "def-2",
  eventCode: "AT-SHOT",
  eventName: "Shot put",
  value: 8.25,
  formattedValue: "8.25",
  higherBetter: true,
};

describe("indexRecords", () => {
  it("is inert on an empty book, which is the whole of the first carnival", () => {
    const empty = indexRecords([]);
    expect(empty.byDivision.size).toBe(0);
    expect(indexRecords(undefined).byBookRow.size).toBe(0);
    // And an absent record badges nothing rather than throwing.
    expect(beatsRecord(undefined, 1)).toBe(false);
  });

  it("indexes under both keys, because the two surfaces hold different ones", () => {
    const book = indexRecords([freestyle, shotPut]);
    expect(book.byDivision.get(divisionKey("def-1", "13", "M"))).toBe(freestyle);
    expect(book.byBookRow.get(bookRowKey("SW-50-FREE", "13, 14 Boys"))).toBe(freestyle);
    // A division with no category holds no record, so the key simply misses.
    expect(book.byDivision.get(divisionKey("def-1", "13", null))).toBeUndefined();
  });

  it("keeps the harder record when two divisions print the same name", () => {
    // Unreachable with the shipped template, and if it ever happens the book
    // must under-badge rather than badge against the softer of the two.
    const softer: SchoolRecord = { ...freestyle, category: "F", value: 33.0 };
    const book = indexRecords([softer, freestyle]);
    expect(book.byBookRow.get(bookRowKey("SW-50-FREE", "13, 14 Boys"))).toBe(freestyle);
    expect(indexRecords([freestyle, softer]).byBookRow.get(bookRowKey("SW-50-FREE", "13, 14 Boys")))
      .toBe(freestyle);
  });
});

describe("beatsRecord", () => {
  it("badges a strictly faster time and nothing else", () => {
    expect(beatsRecord(freestyle, 31.19)).toBe(true);
    expect(beatsRecord(freestyle, 31.21)).toBe(false);
  });

  it("does not badge an equalling swim — the earlier holder keeps the book entry", () => {
    expect(beatsRecord(freestyle, 31.2)).toBe(false);
  });

  it("inverts for a higher-is-better measure, on the server's own flag", () => {
    expect(beatsRecord(shotPut, 8.26)).toBe(true);
    expect(beatsRecord(shotPut, 8.24)).toBe(false);
    expect(beatsRecord(shotPut, 8.25)).toBe(false);
  });

  it("badges nothing for a result with no value (a DNS, DNF or DQ)", () => {
    expect(beatsRecord(freestyle, null)).toBe(false);
    expect(beatsRecord(freestyle, undefined)).toBe(false);
  });
});
