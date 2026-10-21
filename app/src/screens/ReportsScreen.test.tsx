// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { _resetReachabilityForTests, reportUnreachable } from "../api/reachability";
import type {
  Carnival,
  CarnivalSettings,
  ResultsReport,
  SchoolRecord,
  Standings,
} from "../api/types";
import { signIn, signOut } from "../auth/session";
import { renderWithQuery } from "../test/queryWrapper";

/**
 * Reports and controlled exports (ADR-0004 tier 1). What is pinned: both
 * reports render from the SERVER's rows (nothing is ranked or ordered here),
 * the CSV buttons fetch a real file and hand it to the browser, and offline
 * they refuse honestly rather than pretending an export is held on the device.
 */

vi.mock("../api/resources", () => ({
  downloadReportCsv: vi.fn(),
  getCarnival: vi.fn(),
  getCarnivalSettings: vi.fn(),
  getRecords: vi.fn(),
  getResultsReport: vi.fn(),
  getStandings: vi.fn(),
  listCarnivalEvents: vi.fn(),
}));

import {
  downloadReportCsv,
  getCarnival,
  getCarnivalSettings,
  getRecords,
  getResultsReport,
  getStandings,
  listCarnivalEvents,
} from "../api/resources";
import ReportsScreen from "./ReportsScreen";

const carnival = {
  id: "c-1",
  name: "Preston Swimming",
  date: "2027-03-06",
  status: "Open",
  type: "Swimming",
  venue: "Preston Pool",
  ownerSubject: "s1",
  ruleVersion: "2027.1",
  version: 1,
} as Carnival;

const settings: CarnivalSettings = {
  divisionsDefault: null,
  rosterAgeGroups: [],
  rosterYear: null,
  scoring: {
    placePoints: [4, 3, 2],
    participationFloor: 1,
    dnfPoints: 1,
    dnsPoints: 0,
    dqPoints: 0,
    socialPerEntryPoints: 1,
    version: "v1",
  },
  adjustmentCategories: ["Novelty 1"],
  laneCount: null,
  autoFillLanes: false,
  divisions: null,
};

const standings: Standings = {
  carnivalId: "c-1",
  ruleVersion: "v1",
  provisional: true,
  eventsCounted: 2,
  resultsCounted: 5,
  weightedEvents: [],
  hasSocialEvents: false,
  houses: [
    {
      houseCode: "BANKSIA", rank: 1, total: 9,
      placementPoints: 4, participationPoints: 0, bonusPoints: 5,
    },
    {
      houseCode: "WATTLE", rank: 2, total: 3,
      placementPoints: 3, participationPoints: 0, bonusPoints: 0,
    },
  ],
};

const report: ResultsReport = {
  carnivalId: "c-1",
  carnivalName: "Preston Swimming",
  carnivalDate: "2027-03-06",
  provisional: true,
  rows: [
    {
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
    },
    {
      carnivalEventId: "ev-1",
      eventCode: "SW-50-FREE",
      eventName: "Freestyle 50m",
      division: "13 Boys",
      tier: "Competitive",
      heat: 2,
      place: 1,
      displayName: "Cora Diaz",
      houseCode: "MOUNTAIN ASH",
      value: "35.00",
      measuredValue: 35.0,
      status: "Ok",
      jumps: null,
      failsAtBest: null,
      totalFails: null,
    },
    {
      carnivalEventId: "ev-2",
      eventCode: "SW-NOVELTY",
      eventName: "Novelty splash",
      division: "13 Boys",
      tier: "Social",
      heat: null,
      place: null,
      displayName: "Emery Frost",
      houseCode: "GREVILLEA",
      value: "40.00",
      measuredValue: 40.0,
      status: "Ok",
      jumps: null,
      failsAtBest: null,
      totalFails: null,
    },
  ],
};

/** The standing book for the carnival's sport — set here at an earlier carnival. */
const records: SchoolRecord[] = [
  {
    id: "rec-1",
    eventDefinitionId: "def-1",
    eventCode: "SW-50-FREE",
    eventName: "Freestyle 50m",
    ageGroup: "13",
    category: "M",
    division: "13 Boys",
    value: 30.1,
    formattedValue: "30.10",
    higherBetter: false,
    holderDisplayName: "Prior Holder",
    setOn: "2026-03-07",
    carnivalName: "Preston Swimming 2026",
    voided: false,
    voidedAtUtc: null,
    voidedBySubject: null,
    voidReason: null,
  },
];

beforeEach(() => {
  cleanup();
  _resetReachabilityForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  vi.mocked(getCarnival).mockReset().mockResolvedValue(carnival);
  vi.mocked(getCarnivalSettings).mockReset().mockResolvedValue(settings);
  vi.mocked(getStandings).mockReset().mockResolvedValue(standings);
  vi.mocked(getResultsReport).mockReset().mockResolvedValue(report);
  vi.mocked(getRecords).mockReset().mockResolvedValue(records);
  // ADR-0053 §6: the book asks the division list for each event's measure.
  vi.mocked(listCarnivalEvents).mockReset().mockResolvedValue([]);
  vi.mocked(downloadReportCsv).mockReset();
  signIn({ subject: "s1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
});

afterEach(() => {
  signOut();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("ReportsScreen", () => {
  it("renders both reports from the server's own rows", async () => {
    const { container } = renderWithQuery(<ReportsScreen carnivalId="c-1" />);

    // The leaderboard, bonus component and all.
    await waitFor(() =>
      expect(container.textContent).toContain("4 placement · 0 participation · +5 bonus"),
    );
    expect(screen.getByRole("heading", { name: /House standings/ })).toBeTruthy();

    // The book, grouped event → heat, with the two heat winners the server
    // placed. Nothing here re-ranks: both rows say 1st.
    expect(screen.getByRole("heading", { name: /Freestyle 50m · 13 Boys/ })).toBeTruthy();
    expect(screen.getByText("Heat 1")).toBeTruthy();
    expect(screen.getByText("Heat 2")).toBeTruthy();
    expect(screen.getByText("Aiden Brook")).toBeTruthy();
    expect(screen.getByText("Cora Diaz")).toBeTruthy();
    // A Social division is included and marked; it carries no place at all.
    const social = screen.getByRole("heading", { name: /Novelty splash/ });
    expect(social.textContent).toContain("Social");
    expect(screen.getByText("No heat")).toBeTruthy();
  });

  it("offers Print — the PDF export is the browser's own dialog", async () => {
    const print = vi.fn();
    Object.defineProperty(window, "print", { value: print, configurable: true });
    renderWithQuery(<ReportsScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByRole("button", { name: /Print/ }));
    expect(print).toHaveBeenCalled();
  });

  it("fetches a CSV and hands it to the browser under the server's own filename", async () => {
    const file = {
      blob: new Blob(["Rank,House\r\n"], { type: "text/csv" }),
      filename: "standings-preston-swimming-2027-03-06.csv",
    };
    vi.mocked(downloadReportCsv).mockResolvedValue(file);
    const createObjectURL = vi.fn(() => "blob:report");
    const revokeObjectURL = vi.fn();
    Object.defineProperty(URL, "createObjectURL", { value: createObjectURL, configurable: true });
    Object.defineProperty(URL, "revokeObjectURL", { value: revokeObjectURL, configurable: true });
    const click = vi
      .spyOn(HTMLAnchorElement.prototype, "click")
      .mockImplementation(function (this: HTMLAnchorElement) {
        // The download attribute is what makes this a save rather than a
        // navigation, so it is asserted at the moment of the click.
        expect(this.download).toBe(file.filename);
      });

    // shouldAdvanceTime keeps waitFor/react-query on a real-time clock; the
    // deferred revoke is far enough out that only runAllTimers fires it.
    vi.useFakeTimers({ shouldAdvanceTime: true });

    renderWithQuery(<ReportsScreen carnivalId="c-1" />);
    fireEvent.click(await screen.findByRole("button", { name: "Download standings CSV" }));

    await waitFor(() => expect(click).toHaveBeenCalled());
    expect(vi.mocked(downloadReportCsv)).toHaveBeenCalledWith("c-1", "standings");
    expect(createObjectURL).toHaveBeenCalledWith(file.blob);
    // NOT released synchronously: WebKit and Firefox read the blob after the
    // click returns, and revoking there cancels the download outright.
    expect(revokeObjectURL).not.toHaveBeenCalled();

    // But released — a report screen left open all afternoon must not leak one
    // blob per export.
    vi.runAllTimers();
    expect(revokeObjectURL).toHaveBeenCalledWith("blob:report");
  });

  it("says plainly that a failed export produced nothing", async () => {
    vi.mocked(downloadReportCsv).mockRejectedValue(new TypeError("Failed to fetch"));
    renderWithQuery(<ReportsScreen carnivalId="c-1" />);

    fireEvent.click(await screen.findByRole("button", { name: "Download results CSV" }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toMatch(/no file was produced/);
    // NOT the outbox reassurance: nothing was saved anywhere (K7).
    expect(alert.textContent).not.toMatch(/saved on this device/);
  });

  it("disables the exports with honest copy once the server is unreachable", async () => {
    renderWithQuery(<ReportsScreen carnivalId="c-1" />);
    await screen.findByRole("heading", { name: /Results book/ });

    reportUnreachable();

    await waitFor(() =>
      expect(screen.getByRole("button", { name: "Download standings CSV" })).toHaveProperty(
        "disabled",
        true,
      ),
    );
    expect(screen.getByRole("button", { name: "Download results CSV" })).toHaveProperty(
      "disabled",
      true,
    );
    expect(screen.getByRole("status").textContent).toMatch(
      /a file is generated by the server, not from anything held on this device/,
    );
    // Printing what is already on screen still works — it needs no server.
    expect(screen.getByRole("button", { name: /Print/ })).toHaveProperty("disabled", false);
  });

  it("refuses a recorder, and reads nothing on their behalf", async () => {
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    renderWithQuery(<ReportsScreen carnivalId="c-1" />);

    expect((await screen.findByRole("alert")).textContent).toMatch(/carnival administrator role/);
    expect(vi.mocked(getResultsReport)).not.toHaveBeenCalled();
    expect(vi.mocked(getStandings)).not.toHaveBeenCalled();
    expect(vi.mocked(getRecords)).not.toHaveBeenCalled();
  });
});

/**
 * The record book and the district list (ADR-0028 §4/§5) — the two artefacts
 * ADR-0017 says finalisation exists to produce.
 */
describe("ReportsScreen records and the district list", () => {
  it("prints the standing book for the carnival's sport", async () => {
    renderWithQuery(<ReportsScreen carnivalId="c-1" />);

    expect(await screen.findByText("Prior Holder")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "School records" })).toBeTruthy();
    // Narrowed to this carnival's sport, not the whole school-wide book.
    expect(vi.mocked(getRecords)).toHaveBeenCalledWith("Swimming");
    expect(screen.getByText("30.10")).toBeTruthy();
  });

  it("asks for the book by the SPORT a cross-country carnival's events carry", async () => {
    vi.mocked(getCarnival).mockResolvedValue({ ...carnival, type: "CrossCountry" });
    vi.mocked(getRecords).mockResolvedValue([]);
    renderWithQuery(<ReportsScreen carnivalId="c-1" />);

    expect(await screen.findByText(/No cross country records stand yet/)).toBeTruthy();
    expect(vi.mocked(getRecords)).toHaveBeenCalledWith("Cross country");
    expect(vi.mocked(getRecords)).not.toHaveBeenCalledWith("CrossCountry");
  });

  it("reads the whole book for an Other carnival, which names no sport", async () => {
    vi.mocked(getCarnival).mockResolvedValue({ ...carnival, type: "Other" });
    vi.mocked(getRecords).mockResolvedValue([]);
    renderWithQuery(<ReportsScreen carnivalId="c-1" />);

    expect(await screen.findByText(/No records stand yet/)).toBeTruthy();
    expect(vi.mocked(getRecords)).toHaveBeenCalledWith(undefined);
  });

  it("badges the results-book row that beat the standing record", async () => {
    // 31.20 is slower than the record above, so nothing badges; a faster row does.
    vi.mocked(getRecords).mockResolvedValue([{ ...records[0]!, value: 34.0, formattedValue: "34.00" }]);
    renderWithQuery(<ReportsScreen carnivalId="c-1" />);

    await screen.findByRole("heading", { name: /Results book/ });
    await waitFor(() =>
      expect(screen.getAllByTitle(/Beats the school record/).length).toBe(1),
    );
  });

  /** One printed row against one standing record, differing in the third decimal. */
  async function bookRowAgainstRecord(measured: number, record: number) {
    vi.mocked(getResultsReport).mockResolvedValue({
      ...report,
      rows: [{ ...report.rows[0]!, measuredValue: measured, value: measured.toFixed(2) }],
    });
    vi.mocked(getRecords).mockResolvedValue([
      { ...records[0]!, value: record, formattedValue: record.toFixed(2) },
    ]);
    renderWithQuery(<ReportsScreen carnivalId="c-1" />);
    await screen.findByText("Prior Holder");
  }

  it("compares the stored number, so 34.604 does not beat a 34.602 record", async () => {
    // Both print 34.60. Re-reading the printed column badged this swim.
    await bookRowAgainstRecord(34.604, 34.602);
    expect(screen.queryByTitle(/Beats the school record/)).toBeNull();
  });

  it("and 34.596 does beat a 34.600 record, though both still print 34.60", async () => {
    await bookRowAgainstRecord(34.596, 34.6);
    await waitFor(() => expect(screen.getAllByTitle(/Beats the school record/).length).toBe(1));
  });

  it("does not badge a Social row, however fast — a standing record is Competitive", async () => {
    vi.mocked(getRecords).mockResolvedValue([
      {
        ...records[0]!,
        eventCode: "SW-NOVELTY",
        eventName: "Novelty splash",
        value: 41.0,
        formattedValue: "41.00",
      },
    ]);
    renderWithQuery(<ReportsScreen carnivalId="c-1" />);

    await screen.findByText("Prior Holder");
    expect(screen.queryByTitle(/Beats the school record/)).toBeNull();
  });

  it("says the book is empty rather than showing an empty table", async () => {
    vi.mocked(getRecords).mockResolvedValue([]);
    renderWithQuery(<ReportsScreen carnivalId="c-1" />);

    expect(await screen.findByText(/No swimming records stand yet/)).toBeTruthy();
    expect(screen.queryByTitle(/Beats the school record/)).toBeNull();
  });

  it("disables the district CSV until the carnival is finalised, and says why", async () => {
    renderWithQuery(<ReportsScreen carnivalId="c-1" />);

    expect(await screen.findByText(/Finalise the carnival to produce it/)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Download district CSV" })).toHaveProperty(
      "disabled",
      true,
    );
    // Not a section that only exists once published: the standing book is there.
    expect(screen.queryByRole("heading", { name: "Set at this carnival" })).toBeNull();
  });

  it("downloads the district CSV once the carnival is finalised", async () => {
    vi.mocked(getCarnival).mockResolvedValue({ ...carnival, status: "Finalised" });
    vi.mocked(downloadReportCsv).mockResolvedValue({
      blob: new Blob(["Event code\r\n"], { type: "text/csv" }),
      filename: "district-preston-swimming-2027-03-06.csv",
    });
    Object.defineProperty(URL, "createObjectURL", { value: vi.fn(() => "blob:d"), configurable: true });
    Object.defineProperty(URL, "revokeObjectURL", { value: vi.fn(), configurable: true });
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {});

    renderWithQuery(<ReportsScreen carnivalId="c-1" />);
    const button = await screen.findByRole("button", { name: "Download district CSV" });
    await waitFor(() => expect(button).toHaveProperty("disabled", false));
    fireEvent.click(button);

    await waitFor(() => expect(vi.mocked(downloadReportCsv)).toHaveBeenCalledWith("c-1", "district"));
    // Published, so the records this carnival set get their own section.
    expect(screen.getByRole("heading", { name: "Set at this carnival" })).toBeTruthy();
  });
});
