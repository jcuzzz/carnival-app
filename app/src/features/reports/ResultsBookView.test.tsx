// @vitest-environment jsdom
import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import type { ResultRow, ResultsReport, SchoolRecord } from "../../api/types";
import { ResultsBookView } from "./ResultsBookView";

/**
 * The printed card (ADR-0056 §4): the deck's grid on paper, with the countback
 * rule under it so a placing can be read off the page rather than taken on
 * trust. Every value is the server's — place, best and both fail counts.
 */

function row(over: Partial<ResultRow>): ResultRow {
  return {
    carnivalEventId: "ev-1",
    eventCode: "AT-HIGH-JUMP",
    eventName: "High Jump",
    division: "14 Boys",
    tier: "Competitive",
    heat: null,
    place: 1,
    displayName: "Blake Costa",
    houseCode: "MOUNTAIN ASH",
    value: "1.35",
    measuredValue: 1.35,
    status: "Ok",
    jumps: null,
    failsAtBest: null,
    totalFails: null,
    ...over,
  };
}

const report = (rows: ResultRow[]): ResultsReport => ({
  carnivalId: "c-1",
  carnivalName: "Preston Athletics",
  carnivalDate: "2027-03-06",
  provisional: true,
  rows,
});

const measures = new Map([["ev-1", "Height" as const]]);

describe("the results book on a laddered high jump", () => {
  const rows = [
    row({
      place: 1,
      jumps: [
        { height: 1.25, marks: "-" },
        { height: 1.3, marks: "O" },
        { height: 1.35, marks: "O" },
        { height: 1.4, marks: "XXX" },
      ],
      failsAtBest: 0,
      totalFails: 3,
    }),
    row({
      place: 2,
      displayName: "Finley Larsen",
      houseCode: "WATTLE",
      jumps: [
        { height: 1.25, marks: "O" },
        { height: 1.3, marks: "XXO" },
      ],
      value: "1.30",
      measuredValue: 1.3,
      failsAtBest: 2,
      totalFails: 2,
    }),
    row({
      place: null,
      displayName: "Dakota Papadopoulos",
      houseCode: "BANKSIA",
      status: "Nm",
      value: null,
      measuredValue: null,
      jumps: [{ height: 1.25, marks: "XXX" }],
      failsAtBest: null,
      totalFails: 3,
    }),
  ];

  it("prints one column per height any row jumped at, ascending", () => {
    const { container } = render(
      <ResultsBookView report={report(rows)} measureByEvent={measures} />,
    );
    const heads = [...container.querySelectorAll("thead th")].map((th) => th.textContent);
    expect(heads).toEqual([
      "Place",
      "Name",
      "House",
      "1.25",
      "1.30",
      "1.35",
      "1.40",
      "Best",
      "Fails @ best",
      "Total",
    ]);
  });

  it("puts each card's marks in its own cells, and NM where there is no best", () => {
    const { container } = render(
      <ResultsBookView report={report(rows)} measureByEvent={measures} />,
    );
    const cells = [...container.querySelectorAll("tbody tr")].map((tr) =>
      [...tr.querySelectorAll("td")].map((td) => td.textContent),
    );
    expect(cells[0]).toEqual(["1", "Blake Costa", "MOUNTAIN ASH", "–", "O", "O", "XXX", "1.35", "0", "3"]);
    expect(cells[1]).toEqual(["2", "Finley Larsen", "WATTLE", "O", "XXO", "", "", "1.30", "2", "2"]);
    expect(cells[2]).toEqual([
      "—",
      "Dakota Papadopoulos",
      "BANKSIA",
      "XXX",
      "",
      "",
      "",
      "NM",
      "",
      "3",
    ]);
  });

  /** ADR-0028 §5: the card prints the same badge the plain result list does. */
  it("badges a card that beat the standing record, beside its best", () => {
    const standing: SchoolRecord = {
      id: "rec-1",
      eventDefinitionId: "def-1",
      eventCode: "AT-HIGH-JUMP",
      eventName: "High Jump",
      ageGroup: "14",
      category: "M",
      division: "14 Boys",
      value: 1.3,
      formattedValue: "1.30",
      higherBetter: true,
      holderDisplayName: "Prior Holder",
      setOn: "2026-03-06",
      carnivalName: "Preston Athletics",
      voided: false,
      voidedAtUtc: null,
      voidedBySubject: null,
      voidReason: null,
    };
    render(
      <ResultsBookView report={report(rows)} records={[standing]} measureByEvent={measures} />,
    );
    // 1.35 beats it; 1.30 only equals it, and equalling is not beating.
    expect(screen.getByTitle("Beats the school record: 1.30, Prior Holder")).toBeTruthy();
    expect(screen.getAllByText("record")).toHaveLength(1);
  });

  it("prints the countback rule, so the placing is readable off the page", () => {
    render(<ResultsBookView report={report(rows)} measureByEvent={measures} />);
    expect(
      screen.getByText(/Places by best height, then fewest fails at that height/),
    ).toBeTruthy();
  });

  it("states two attempts per height beside the rule, and nothing at three", () => {
    const { unmount } = render(
      <ResultsBookView report={report(rows)} measureByEvent={measures} />,
    );
    expect(screen.queryByText(/attempts per height/)).toBeNull();
    unmount();
    render(
      <ResultsBookView
        report={report(rows.map((r) => ({ ...r, attemptsPerHeight: 2 })))}
        measureByEvent={measures}
      />,
    );
    expect(screen.getByText(/2 attempts per height: out when both fail at one height/)).toBeTruthy();
  });

  it("leaves a block with no card as the plain result list", () => {
    const { container } = render(
      <ResultsBookView
        report={report([row({ jumps: null, eventCode: "SW-50-FREE", value: "31.20" })])}
      />,
    );
    const heads = [...container.querySelectorAll("thead th")].map((th) => th.textContent);
    expect(heads).toEqual(["Place", "Name", "House", "Result"]);
  });
});
