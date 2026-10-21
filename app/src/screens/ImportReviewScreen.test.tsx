// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { _resetReachabilityForTests } from "../api/reachability";
import type {
  ImportBatchSummary,
  ReconciliationReport,
  ValidationDocument,
  ValidationReport,
} from "../api/types";
import { signIn, signOut } from "../auth/session";
import { renderWithQuery } from "../test/queryWrapper";

vi.mock("../api/resources", () => ({
  listImportBatches: vi.fn(),
  getImportDefaults: vi.fn(),
  getImportValidation: vi.fn(),
  getImportReconciliation: vi.fn(),
  stageImport: vi.fn(),
  approveImport: vi.fn(),
  promoteImport: vi.fn(),
}));

import {
  getImportDefaults,
  getImportReconciliation,
  getImportValidation,
  listImportBatches,
  stageImport,
} from "../api/resources";
import ImportReviewScreen from "./ImportReviewScreen";

const BATCH = "0190f3a0-0000-7000-8000-000000000001";

function batch(): ImportBatchSummary {
  return {
    id: BATCH,
    sourceFileName: "ST_SYNTH.csv",
    status: "Validated",
    newCount: 3,
    changedCount: 0,
    inactiveCount: 0,
    rejectedCount: 0,
    unmatchedCount: 0,
    importedAtUtc: "2026-08-28T01:00:00Z",
    importedBySubject: "dev-admin",
  };
}

function validation(
  document: ValidationDocument = { structuralErrors: [], rowIssues: [] },
): ValidationReport {
  return {
    id: BATCH,
    status: "Validated",
    sourceFileName: "ST_SYNTH.csv",
    sourceFileHash: "abc",
    mappingVersion: "1",
    validation: document,
  };
}

function reconciliation(): ReconciliationReport {
  return {
    id: BATCH,
    status: "Validated",
    competitionYear: 2026,
    cutOff: "12-31",
    summary: { new: 1, changed: 0, unchanged: 0, inactive: 0, rejected: 0, unmatched: 0 },
    rows: [
      {
        rowNumber: 2,
        externalStudentId: "STU001",
        firstName: "Sam",
        preferredName: null,
        surname: "Synth",
        yearLevel: 7,
        houseCode: "WATTLE",
        homeGroup: "7A",
        enrolmentStatus: "ACTV",
        competitionAgeGroup: "12",
        competitionCategory: "M",
        disposition: "New",
        isRejected: false,
        issues: [],
      },
    ],
    unmatchedStudents: [],
  };
}

beforeEach(() => {
  cleanup();
  localStorage.clear();
  _resetReachabilityForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  vi.mocked(listImportBatches).mockReset().mockResolvedValue([batch()]);
  vi.mocked(getImportValidation).mockReset().mockResolvedValue(validation());
  vi.mocked(getImportReconciliation).mockReset().mockResolvedValue(reconciliation());
  vi.mocked(getImportDefaults).mockReset().mockResolvedValue({
    competitionYear: 2027,
    earliestYear: 2025,
    latestYear: 2027,
    cutOff: "12-31",
  });
  vi.mocked(stageImport).mockReset().mockResolvedValue({
    id: BATCH,
    status: "Validated",
    competitionYear: 2027,
    structuralErrors: [],
    summary: { new: 1, changed: 0, unchanged: 0, inactive: 0, rejected: 0, unmatched: 0 },
  });
  signIn({ subject: "s1", displayName: "Sys Admin", roles: ["SystemAdministrator"] });
});

afterEach(() => signOut());

describe("ImportReviewScreen", () => {
  it("renders the batch list and, once a batch is picked, both reports", async () => {
    localStorage.setItem("sports.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen />);
    await screen.findByText("ST_SYNTH.csv");
    await screen.findByText(/Reconciliation preview/);
    expect(screen.getByText("No validation issues.")).toBeTruthy();
    expect(screen.getByText("Sam Synth")).toBeTruthy();
  });

  it("renders with no batch selected", async () => {
    renderWithQuery(<ImportReviewScreen />);
    await screen.findByText("ST_SYNTH.csv");
    expect(screen.getByRole("button", { name: "Review" })).toBeTruthy();
  });

  it("offers a sign-in to a signed-out visitor rather than the role notice", async () => {
    signOut();
    renderWithQuery(<ImportReviewScreen />);

    expect(await screen.findByRole("link", { name: "Sign in" })).toBeTruthy();
    expect(screen.queryByText(/available to system administrators/)).toBeNull();
    expect(screen.queryByText(/Upload roster export/)).toBeNull();
    expect(listImportBatches).not.toHaveBeenCalled();
  });

  it("still names the role to a signed-in visitor without it", async () => {
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    renderWithQuery(<ImportReviewScreen />);

    expect(await screen.findByText(/available to system administrators/)).toBeTruthy();
    expect(screen.queryByRole("link", { name: "Sign in" })).toBeNull();
  });

  // Roster import is a School hub row (ADR-0055 §1), so it carries the same
  // way back the other section screens do.
  it("goes back to the School hub", async () => {
    renderWithQuery(<ImportReviewScreen />);
    await screen.findByText("ST_SYNTH.csv");
    expect(screen.getByRole("link", { name: "← School" }).getAttribute("href")).toBe("#/school");
  });

  // ADR-0060 §3: the year is on screen, and changeable, before anything is staged.
  it("pre-fills the competition year from the server and stages the file for it", async () => {
    renderWithQuery(<ImportReviewScreen />);
    const year = await screen.findByLabelText<HTMLSelectElement>("Competition year");
    expect(year.value).toBe("2027");
    expect([...year.options].map((o) => o.value)).toEqual(["2025", "2026", "2027"]);
    expect(screen.getByText(/Ages as at 31 December 2027\./)).toBeTruthy();

    const file = new File(["STKEY"], "ST_SYNTH.csv", { type: "text/csv" });
    fireEvent.change(screen.getByLabelText(/CASES21 student export/), {
      target: { files: [file] },
    });
    await waitFor(() => expect(stageImport).toHaveBeenCalledWith(file, 2027));
  });

  it("stages the file for the year the organiser changes it to", async () => {
    renderWithQuery(<ImportReviewScreen />);
    const year = await screen.findByLabelText<HTMLSelectElement>("Competition year");
    fireEvent.change(year, { target: { value: "2026" } });
    expect(screen.getByText(/Ages as at 31 December 2026\./)).toBeTruthy();

    const file = new File(["STKEY"], "ST_SYNTH.csv", { type: "text/csv" });
    fireEvent.change(screen.getByLabelText(/CASES21 student export/), {
      target: { files: [file] },
    });
    await waitFor(() => expect(stageImport).toHaveBeenCalledWith(file, 2026));
  });

  it("leaves the year to the server, and says so, when the defaults can't be read", async () => {
    vi.mocked(getImportDefaults).mockRejectedValue(new Error("offline"));
    renderWithQuery(<ImportReviewScreen />);
    await screen.findByText(/the server will choose it/);
    expect(screen.queryByLabelText("Competition year")).toBeNull();

    const file = new File(["STKEY"], "ST_SYNTH.csv", { type: "text/csv" });
    fireEvent.change(screen.getByLabelText(/CASES21 student export/), {
      target: { files: [file] },
    });
    await waitFor(() => expect(stageImport).toHaveBeenCalledWith(file, undefined));
  });

  it("states the day the staged ages are as at", async () => {
    localStorage.setItem("sports.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen />);
    const heading = await screen.findByText(/Reconciliation preview — competition year 2026/);
    expect(heading.nextElementSibling?.textContent).toBe("Ages as at 31 December 2026.");
  });

  it("counts the exception rows and lists every issue with its severity", async () => {
    vi.mocked(getImportValidation).mockResolvedValue(
      validation({
        structuralErrors: [],
        rowIssues: [
          {
            rowNumber: 4,
            externalStudentId: "STU007",
            issues: [
              { code: "DUPLICATE_STKEY", message: "STKEY appears twice.", severity: "Error" },
            ],
          },
          {
            rowNumber: 9,
            externalStudentId: null,
            issues: [
              { code: "MISSING_HOUSE", message: "No house on the row.", severity: "Exception" },
            ],
          },
        ],
      }),
    );
    localStorage.setItem("sports.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen />);

    await screen.findByText(/2 row\(s\) carry DUPLICATE_STKEY \/ MISSING_HOUSE exceptions/);
    expect(screen.getByText("DUPLICATE_STKEY")).toBeTruthy();
    expect(screen.getByText("MISSING_HOUSE")).toBeTruthy();
    expect(screen.getByText("Error").className).toContain("badge");
    expect(screen.getByText("Exception").className).toContain("badge");
    expect(screen.queryByText("No validation issues.")).toBeNull();
  });
  it("says an aged-out batch expired, to upload again, and offers only Reject", async () => {
    vi.mocked(getImportValidation).mockResolvedValue({ ...validation(), validation: null });
    vi.mocked(getImportReconciliation).mockRejectedValue(
      new ApiError(
        409,
        { error: "IMPORT_BATCH_EXPIRED", detail: "server copy" },
        "/api/v1/imports/x/reconciliation",
      ),
    );
    localStorage.setItem("sports.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen />);

    const notices = await screen.findAllByText(/This batch expired.*Upload the file again\./);
    expect(notices.length).toBe(2);
    await waitFor(() =>
      expect((screen.getByRole("button", { name: "Approve" }) as HTMLButtonElement).disabled).toBe(
        true,
      ),
    );
    expect((screen.getByRole("button", { name: "Reject" }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole("button", { name: "Promote" }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("says a rejected batch's report was discarded", async () => {
    vi.mocked(getImportValidation).mockResolvedValue({
      ...validation(),
      status: "Rejected",
      validation: null,
    });
    localStorage.setItem("sports.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen />);

    await screen.findByText(/discarded when this batch was rejected/);
    expect(screen.queryByText(/This batch expired/)).toBeNull();
  });
});
