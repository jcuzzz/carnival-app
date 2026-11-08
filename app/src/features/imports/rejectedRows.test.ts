import { describe, expect, it } from "vitest";
import type { RowValidation } from "../../api/types";
import { hasWordsFor, rejectedRowsSummary, rowProblems } from "./rejectedRows";

function row(
  rowNumber: number,
  ...issues: [code: string, severity: "Error" | "Exception" | "Warning"][]
): RowValidation {
  return {
    rowNumber,
    externalStudentId: `S${rowNumber}`,
    issues: issues.map(([code, severity]) => ({ code, severity, message: "server copy" })),
  };
}

describe("rejectedRowsSummary", () => {
  // The walk-through's file: the old line counted 3 and never named the birth date.
  it("counts every refused row and names every reason in plain words", () => {
    expect(
      rejectedRowsSummary([
        row(2, ["DUPLICATE_STKEY", "Error"]),
        row(3, ["MISSING_HOUSE", "Exception"]),
        row(5, ["MISSING_BIRTHDATE", "Error"]),
        row(122, ["DUPLICATE_STKEY", "Error"]),
      ]),
    ).toBe("4 rows can't be imported: 2 repeat a student ID, 1 has no house, 1 has no birth date.");
  });

  it("orders reasons by how many rows have them", () => {
    expect(
      rejectedRowsSummary([
        row(2, ["MISSING_BIRTHDATE", "Error"]),
        row(3, ["MISSING_HOUSE", "Exception"]),
        row(4, ["MISSING_HOUSE", "Exception"]),
      ]),
    ).toBe("3 rows can't be imported: 2 have no house, 1 has no birth date.");
  });

  it("speaks of one row as it", () => {
    expect(
      rejectedRowsSummary([row(7, ["MISSING_HOUSE", "Exception"], ["MISSING_GENDER", "Error"])]),
    ).toBe("1 row can't be imported: it has no house and has no gender.");
  });

  it("lets a warning through, and says nothing when no row is refused", () => {
    expect(rejectedRowsSummary([row(2, ["SOMETHING_ODD", "Warning"])])).toBeNull();
    expect(rejectedRowsSummary([])).toBeNull();
  });

  it("never shows a code it has no words for", () => {
    const text = rejectedRowsSummary([row(2, ["NEW_SERVER_CODE", "Error"]), row(3, ["OTHER_CODE", "Error"])]);
    expect(text).toBe("2 rows can't be imported: 2 have another problem.");
    expect(text).not.toContain("CODE");
  });

  it("counts a row once even when a reason repeats on it", () => {
    expect(
      rejectedRowsSummary([row(2, ["DUPLICATE_STKEY", "Error"], ["DUPLICATE_STKEY", "Error"])]),
    ).toBe("1 row can't be imported: it repeats a student ID.");
  });
});

describe("rowProblems", () => {
  it("says each distinct problem on a row in the summary's words, never its code", () => {
    expect(rowProblems([{ code: "MISSING_HOUSE" }, { code: "MISSING_BIRTHDATE" }])).toBe(
      "has no house and has no birth date",
    );
    expect(rowProblems([{ code: "DUPLICATE_STKEY" }, { code: "DUPLICATE_STKEY" }])).toBe(
      "repeats a student ID",
    );
    expect(rowProblems([{ code: "NEW_SERVER_CODE" }])).toBe("has another problem");
    expect(hasWordsFor("MISSING_HOUSE")).toBe(true);
    expect(hasWordsFor("NEW_SERVER_CODE")).toBe(false);
    expect(hasWordsFor("toString")).toBe(false);
  });
});
