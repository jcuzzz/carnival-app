// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { _resetReachabilityForTests } from "../api/reachability";
import type {
  Carnival,
  ImportBatchSummary,
  ReconciliationReport,
  ValidationDocument,
  ValidationReport,
} from "../api/types";
import { signIn, signOut } from "../auth/session";
import { _resetHouseMarksForTests, applyHouseMarks } from "../branding/houseMarks";
import { renderWithQuery } from "../test/queryWrapper";

vi.mock("../api/resources", () => ({
  getCarnival: vi.fn(),
  listImportBatches: vi.fn(),
  getImportDefaults: vi.fn(),
  getImportValidation: vi.fn(),
  getImportReconciliation: vi.fn(),
  stageImport: vi.fn(),
  approveImport: vi.fn(),
  promoteImport: vi.fn(),
}));

import {
  approveImport,
  getCarnival,
  getImportDefaults,
  getImportReconciliation,
  getImportValidation,
  listImportBatches,
  promoteImport,
  stageImport,
} from "../api/resources";
import ImportReviewScreen from "./ImportReviewScreen";

const BATCH = "0190f3a0-0000-7000-8000-000000000001";
const CARNIVAL = "0190f3a0-0000-7000-8000-0000000000c1";

function carnival(date = "2026-02-20"): Carnival {
  return {
    id: CARNIVAL,
    name: "Swimming Carnival",
    date,
    ownerSubject: "s1",
    ruleVersion: "1",
    status: "Draft",
    type: "Swimming",
    venue: null,
    version: 1,
  };
}

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
    importedBySubject: "local:0190f3a0-0000-7000-8000-0000000000a1",
    importedByDisplayName: "Jo Admin",
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
  vi.mocked(getCarnival).mockReset().mockResolvedValue(carnival());
  vi.mocked(approveImport).mockReset().mockResolvedValue(undefined as never);
  vi.mocked(promoteImport).mockReset().mockResolvedValue(undefined as never);
  vi.mocked(stageImport).mockReset().mockResolvedValue({
    id: BATCH,
    status: "Validated",
    competitionYear: 2027,
    structuralErrors: [],
    summary: { new: 1, changed: 0, unchanged: 0, inactive: 0, rejected: 0, unmatched: 0 },
  });
  signIn({ subject: "s1", displayName: "Sys Admin", roles: ["SystemAdministrator"] });
});

afterEach(() => {
  signOut();
  vi.unstubAllEnvs();
  _resetHouseMarksForTests();
});

describe("ImportReviewScreen", () => {
  it("renders the batch list and, once a batch is picked, both reports", async () => {
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);
    await screen.findByText("ST_SYNTH.csv");
    await screen.findByText(/Students in this file/);
    expect(screen.getByText("No problems: every row can update the student list.")).toBeTruthy();
    expect(screen.getByText("Shown below")).toBeTruthy();
    expect(screen.queryByText("reviewing")).toBeNull();
    expect(screen.getByText("Sam Synth")).toBeTruthy();
  });

  it("names each student's house and category as the school does, not by code", async () => {
    applyHouseMarks([{ code: "WATTLE", name: "Wattle House", colour: null }]);
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);

    const row = (await screen.findByText("Sam Synth")).closest("tr")!;
    const cells = [...row.querySelectorAll("td")].map((td) => td.textContent);
    expect(cells).toContain("Wattle House");
    expect(cells).toContain("Boys");
    expect(cells).not.toContain("WATTLE");
    expect(cells).not.toContain("M");
  });

  it("renders with no batch selected", async () => {
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);
    await screen.findByText("ST_SYNTH.csv");
    expect(screen.getByRole("button", { name: "Review" })).toBeTruthy();
  });

  it("offers a sign-in to a signed-out visitor rather than the role notice", async () => {
    signOut();
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);

    expect(await screen.findByRole("link", { name: "Sign in" })).toBeTruthy();
    expect(screen.queryByText(/available to system administrators/)).toBeNull();
    expect(screen.queryByText(/Upload roster export/)).toBeNull();
    expect(listImportBatches).not.toHaveBeenCalled();
  });

  it("still names the role to a signed-in visitor without it", async () => {
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);

    expect(await screen.findByText(/available to system administrators/)).toBeTruthy();
    expect(screen.getByText(/signed in as Rae Recorder \(Recorder\)\./)).toBeTruthy();
    expect(screen.getByText("Uploading needs the System administrator role.")).toBeTruthy();
    expect(screen.queryByRole("link", { name: "Sign in" })).toBeNull();
  });

  // Roster import is a School hub row (ADR-0055 §1), so it carries the same
  // way back the other section screens do.
  it("goes back to the School hub", async () => {
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);
    await screen.findByText("ST_SYNTH.csv");
    expect(screen.getByRole("link", { name: "← School" }).getAttribute("href")).toBe("#/school");
  });

  // ADR-0060 §3: the year is on screen, and changeable, before anything is staged.
  it("pre-fills the competition year from the server and stages the file for it", async () => {
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);
    const year = await screen.findByLabelText<HTMLSelectElement>("Competition year");
    expect(year.value).toBe("2027");
    expect([...year.options].map((o) => o.value)).toEqual(["2025", "2026", "2027"]);
    expect(screen.getByText(/Ages as at 31 December 2027\./)).toBeTruthy();

    const file = new File(["STKEY"], "ST_SYNTH.csv", { type: "text/csv" });
    fireEvent.change(screen.getByLabelText(/Student list: a CSV export/), {
      target: { files: [file] },
    });
    await waitFor(() => expect(stageImport).toHaveBeenCalledWith(file, 2027));
  });

  it("stages the file for the year the organiser changes it to", async () => {
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);
    const year = await screen.findByLabelText<HTMLSelectElement>("Competition year");
    fireEvent.change(year, { target: { value: "2026" } });
    expect(screen.getByText(/Ages as at 31 December 2026\./)).toBeTruthy();

    const file = new File(["STKEY"], "ST_SYNTH.csv", { type: "text/csv" });
    fireEvent.change(screen.getByLabelText(/Student list: a CSV export/), {
      target: { files: [file] },
    });
    await waitFor(() => expect(stageImport).toHaveBeenCalledWith(file, 2026));
  });

  it("leaves the year to the server, and says so, when the defaults can't be read", async () => {
    vi.mocked(getImportDefaults).mockRejectedValue(new Error("offline"));
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);
    await screen.findByText(/the server will choose it/);
    expect(screen.queryByLabelText("Competition year")).toBeNull();

    const file = new File(["STKEY"], "ST_SYNTH.csv", { type: "text/csv" });
    fireEvent.change(screen.getByLabelText(/Student list: a CSV export/), {
      target: { files: [file] },
    });
    await waitFor(() => expect(stageImport).toHaveBeenCalledWith(file, undefined));
  });

  it("states the day the staged ages are as at", async () => {
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);
    const heading = await screen.findByText(/Students in this file — competition year 2026/);
    expect(heading.nextElementSibling?.textContent).toBe("Ages as at 31 December 2026.");
  });

  it("counts the rows left out and lists each one's problem in plain words", async () => {
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
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);

    await screen.findByText("2 rows can't be imported: 1 has no house, 1 repeats a student ID.");
    const table = screen.getByRole("table", { name: "Rows with a problem" });
    expect(within(table).getByText("Repeats a student ID")).toBeTruthy();
    expect(within(table).getByText("Has no house")).toBeTruthy();
    expect(within(table).getAllByText("Left out").map((b) => b.className)).toEqual([
      "badge bad",
      "badge bad",
    ]);
    expect(within(table).getByRole("columnheader", { name: "Student ID" })).toBeTruthy();
    expect(table.textContent).not.toMatch(/STKEY|MISSING_HOUSE|Error|Exception|appears twice/);
    expect(screen.queryByText(/No problems/)).toBeNull();
  });

  it("says Left out on every line of a refused row, and Kept only for a row with warnings alone", async () => {
    vi.mocked(getImportValidation).mockResolvedValue(
      validation({
        structuralErrors: [],
        rowIssues: [
          {
            rowNumber: 4,
            externalStudentId: "STU007",
            issues: [
              { code: "SOMETHING_ODD", message: "Odd.", severity: "Warning" },
              { code: "MISSING_HOUSE", message: "server copy", severity: "Exception" },
            ],
          },
          {
            rowNumber: 6,
            externalStudentId: "STU009",
            issues: [{ code: "SOMETHING_ODD", message: "Odd.", severity: "Warning" }],
          },
        ],
      }),
    );
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);

    const table = await screen.findByRole("table", { name: "Rows with a problem" });
    const outcomes = within(table)
      .getAllByRole("row")
      .slice(1)
      .map((tr) => {
        const { cells } = tr as HTMLTableRowElement;
        return [cells[0]?.textContent, cells[3]?.textContent];
      });
    expect(outcomes).toEqual([
      ["4", "Left out"],
      ["4", "Left out"],
      ["6", "Kept"],
    ]);
  });

  it("shows the server's sentence only for a code the app has no words for", async () => {
    vi.mocked(getImportValidation).mockResolvedValue(
      validation({
        structuralErrors: [],
        rowIssues: [
          {
            rowNumber: 4,
            externalStudentId: "STU007",
            issues: [{ code: "NEW_SERVER_CODE", message: "Something new.", severity: "Error" }],
          },
        ],
      }),
    );
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);

    const table = await screen.findByRole("table", { name: "Rows with a problem" });
    expect(table.textContent).toContain("Has another problem Something new.");
    expect(table.textContent).not.toContain("NEW_SERVER_CODE");
  });

  it("calls a refused row Left out in the filter and the table, with its problem in words", async () => {
    const report = reconciliation();
    report.summary = { ...report.summary, rejected: 1 };
    report.rows = [
      ...report.rows,
      {
        ...report.rows[0]!,
        rowNumber: 3,
        externalStudentId: "STU002",
        firstName: "Ari",
        houseCode: null,
        disposition: "Rejected",
        isRejected: true,
        issues: [{ code: "MISSING_HOUSE", message: "server copy", severity: "Exception" }],
      },
    ];
    report.summary.unmatched = 2;
    vi.mocked(getImportReconciliation).mockResolvedValue(report);
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    const { container } = renderWithQuery(<ImportReviewScreen carnivalId={null} />);

    expect(await screen.findByRole("button", { name: "Left out (1)" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: /^Rejected/ })).toBeNull();
    const table = screen.getByRole("table", {
      name: "Each row of this file, and what updating the student list does with it",
    });
    expect(within(table).getByText("Left out").className).toBe("badge bad");
    expect(within(table).getByText("has no house")).toBeTruthy();
    expect(within(table).getByRole("columnheader", { name: "Outcome" })).toBeTruthy();
    expect(
      screen.getByText(/2 students on the student list are not in this file\. Updating the list/),
    ).toBeTruthy();
    expect(container.textContent).not.toMatch(
      /MISSING_HOUSE|STKEY|Disposition|disposition|promot|staged|batch|roster rows|reconcil|report-only/i,
    );
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
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);

    const notices = await screen.findAllByText(/This upload expired.*Upload the file again\./);
    expect(notices.length).toBe(2);
    await waitFor(() =>
      expect(
        (screen.getByRole("button", { name: "Approve and update the student list" }) as HTMLButtonElement)
          .disabled,
      ).toBe(true),
    );
    expect((screen.getByRole("button", { name: "Reject" }) as HTMLButtonElement).disabled).toBe(false);
    expect(screen.queryByRole("button", { name: "Update the student list" })).toBeNull();
  });

  it("names the importer and gives each status its plain words", async () => {
    vi.mocked(listImportBatches).mockResolvedValue([
      batch(),
      {
        ...batch(),
        id: "0190f3a0-0000-7000-8000-000000000002",
        status: "Promoted",
        importedBySubject: "entra-1234",
        importedByDisplayName: null,
      },
    ]);
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);

    expect(await screen.findByText("Jo Admin")).toBeTruthy();
    expect(screen.getByRole("columnheader", { name: "Left out" })).toBeTruthy();
    expect(screen.queryByRole("columnheader", { name: "Rejected" })).toBeNull();
    // A name the server can't resolve falls back to the subject.
    expect(screen.getByText("entra-1234").className).toBe("mono");
    expect(screen.getByText("Ready to review")).toBeTruthy();
    expect(screen.getByText("Student list updated").className).toBe("badge ok");
    expect(screen.queryByText("Promoted")).toBeNull();
    expect(screen.queryByText("Validated")).toBeNull();
  });

  it("says what an earlier promoted batch did, and why its details are gone", async () => {
    vi.mocked(listImportBatches).mockResolvedValue([
      { ...batch(), status: "Promoted", newCount: 96, inactiveCount: 21, rejectedCount: 4 },
    ]);
    vi.mocked(getImportValidation).mockResolvedValue({
      ...validation(),
      status: "Promoted",
      validation: null,
    });
    vi.mocked(getImportReconciliation).mockRejectedValue(
      new ApiError(
        409,
        { error: "IMPORT_ROWS_PURGED", detail: "server copy" },
        "/api/v1/imports/x/reconciliation",
      ),
    );
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    const { container } = renderWithQuery(<ImportReviewScreen carnivalId={null} />);

    await screen.findByText(
      "Student list updated: 96 students added, 21 students marked as no longer enrolled, " +
        "4 rows left out.",
    );
    expect(screen.getByText(/so that no second copy of the list is kept/)).toBeTruthy();
    expect(container.textContent).not.toMatch(/ADR|purged|durable|server copy/);
    expect(screen.queryByRole("button", { name: /update the student list/i })).toBeNull();
  });

  it("says in plain words that a file with layout problems can't update the list", async () => {
    vi.mocked(stageImport).mockResolvedValue({
      id: BATCH,
      status: "ValidationFailed",
      competitionYear: 2027,
      structuralErrors: ["Missing column: STKEY", "Missing column: HOUSE"],
      summary: { new: 0, changed: 0, unchanged: 0, inactive: 0, rejected: 0, unmatched: 0 },
    });
    vi.mocked(getImportValidation).mockResolvedValue(
      validation({ structuralErrors: ["Missing column: STKEY", "Missing column: HOUSE"], rowIssues: [] }),
    );
    const { container } = renderWithQuery(<ImportReviewScreen carnivalId={null} />);
    await screen.findByLabelText<HTMLSelectElement>("Competition year");

    fireEvent.change(screen.getByLabelText(/Student list: a CSV export/), {
      target: { files: [new File(["STKEY"], "ST_SYNTH.csv", { type: "text/csv" })] },
    });

    await screen.findByText(
      "This file can't update the student list: 2 problems with its layout.",
    );
    await screen.findByText("This file can't update the student list. Problems with its layout:");
    expect(screen.getByText("Missing column: STKEY")).toBeTruthy();
    expect(container.textContent).not.toMatch(/structural|promote/i);
  });

  it("sends a refused upload to School settings when the school has no houses", async () => {
    vi.mocked(stageImport).mockRejectedValue(
      new ApiError(409, { error: "SCHOOL_HOUSES_REQUIRED", detail: "server copy" }, "/api/v1/imports"),
    );
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);
    await screen.findByLabelText<HTMLSelectElement>("Competition year");

    fireEvent.change(screen.getByLabelText(/Student list: a CSV export/), {
      target: { files: [new File(["STKEY"], "ST_SYNTH.csv", { type: "text/csv" })] },
    });

    const link = await screen.findByRole("link", { name: "Add the houses in School settings" });
    expect(link.getAttribute("href")).toBe("#/school/settings");
    expect(screen.getByText(/Add the school's houses first/)).toBeTruthy();
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.queryByText("server copy")).toBeNull();
  });

  it("says a rejected batch's report was discarded", async () => {
    vi.mocked(getImportValidation).mockResolvedValue({
      ...validation(),
      status: "Rejected",
      validation: null,
    });
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);

    await screen.findByText(/This upload was rejected\. Its row-by-row details/);
    expect(screen.queryByText(/This upload expired/)).toBeNull();
  });

  // ADR-0066 §4: launched from a carnival, the year is that carnival's.
  it("pre-fills the carnival's year when the server allows it", async () => {
    renderWithQuery(<ImportReviewScreen carnivalId={CARNIVAL} />);
    const year = await screen.findByLabelText<HTMLSelectElement>("Competition year");
    expect(year.value).toBe("2026");
    expect(getCarnival).toHaveBeenCalledWith(CARNIVAL);
    expect(screen.queryByText(/This carnival is in/)).toBeNull();
    expect(screen.getByRole("link", { name: "← Manage" }).getAttribute("href")).toBe(
      `#/carnivals/${CARNIVAL}/manage`,
    );

    const file = new File(["STKEY"], "ST_SYNTH.csv", { type: "text/csv" });
    fireEvent.change(screen.getByLabelText(/Student list: a CSV export/), {
      target: { files: [file] },
    });
    await waitFor(() => expect(stageImport).toHaveBeenCalledWith(file, 2026));
  });

  it("keeps the server's year, and says why, when the carnival's is out of range", async () => {
    vi.mocked(getCarnival).mockResolvedValue(carnival("2030-03-01"));
    renderWithQuery(<ImportReviewScreen carnivalId={CARNIVAL} />);
    await screen.findByText(
      "This carnival is in 2030; a roster can be imported for 2025–2027 only.",
    );
    expect(screen.getByLabelText<HTMLSelectElement>("Competition year").value).toBe("2027");
  });

  it("names the batch by its file and upload time, never its id", async () => {
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    const { container } = renderWithQuery(<ImportReviewScreen carnivalId={null} />);
    await screen.findByText(/^ST_SYNTH\.csv, uploaded /);
    expect(container.textContent).not.toContain(BATCH);
  });

  // ADR-0066 §8: one press, still two calls, so the batch passes through Approved.
  it("approves then promotes in one press, and offers the way back to the carnival", async () => {
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen carnivalId={CARNIVAL} />);
    const press = await screen.findByRole("button", { name: "Approve and update the student list" });
    await waitFor(() => expect((press as HTMLButtonElement).disabled).toBe(false));
    expect(screen.queryByRole("button", { name: "Approve" })).toBeNull();
    fireEvent.click(press);

    const back = await screen.findByRole("link", { name: "Back to Swimming Carnival" });
    expect(back.getAttribute("href")).toBe(`#/carnivals/${CARNIVAL}/manage`);
    expect(screen.getByText("Student list updated: 3 students added.").className).toBe(
      "alert success",
    );
    expect(back.className).toBe("button primary-link");
    expect(approveImport).toHaveBeenCalledWith(BATCH, true);
    expect(promoteImport).toHaveBeenCalledWith(BATCH);
    expect(vi.mocked(approveImport).mock.invocationCallOrder[0]).toBeLessThan(
      vi.mocked(promoteImport).mock.invocationCallOrder[0]!,
    );
  });

  it("offers no way back to a carnival when not launched from one", async () => {
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);
    const press = await screen.findByRole("button", { name: "Approve and update the student list" });
    await waitFor(() => expect((press as HTMLButtonElement).disabled).toBe(false));
    fireEvent.click(press);

    await waitFor(() => expect(promoteImport).toHaveBeenCalledWith(BATCH));
    await waitFor(() =>
      expect(screen.queryByRole("button", { name: "Approve and update the student list" })).toBeNull(),
    );
    expect(screen.queryByRole("link", { name: /^Back to/ })).toBeNull();
    expect(getCarnival).not.toHaveBeenCalled();
  });

  it("leaves an approved batch with Promote on offer when the promote fails", async () => {
    vi.mocked(promoteImport).mockRejectedValueOnce(new Error("Database unavailable."));
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen carnivalId={CARNIVAL} />);
    const press = await screen.findByRole("button", { name: "Approve and update the student list" });
    await waitFor(() => expect((press as HTMLButtonElement).disabled).toBe(false));
    vi.mocked(getImportValidation).mockResolvedValue({ ...validation(), status: "Approved" });
    fireEvent.click(press);

    expect((await screen.findByRole("alert")).textContent).toBe(
      "This upload is approved, but the student list was not updated. Database unavailable. " +
        "Press Update the student list to try again.",
    );
    const promote = await screen.findByRole("button", { name: "Update the student list" });
    expect(screen.queryByRole("link", { name: /^Back to/ })).toBeNull();

    fireEvent.click(promote);
    await screen.findByRole("link", { name: "Back to Swimming Carnival" });
    expect(promoteImport).toHaveBeenCalledTimes(2);
    expect(approveImport).toHaveBeenCalledTimes(1);
  });

  it("offers only Promote at once after a failed promote, before the re-read lands", async () => {
    vi.mocked(promoteImport).mockRejectedValueOnce(new Error("Database unavailable."));
    localStorage.setItem("carnival.lastImportBatchId", BATCH);
    renderWithQuery(<ImportReviewScreen carnivalId={null} />);
    const press = await screen.findByRole("button", { name: "Approve and update the student list" });
    await waitFor(() => expect((press as HTMLButtonElement).disabled).toBe(false));
    vi.mocked(getImportValidation).mockReturnValue(new Promise(() => {}));
    vi.mocked(getImportReconciliation).mockReturnValue(new Promise(() => {}));
    fireEvent.click(press);

    const promote = await screen.findByRole("button", { name: "Update the student list" });
    expect(screen.queryByRole("button", { name: "Approve and update the student list" })).toBeNull();
    fireEvent.click(promote);
    await waitFor(() => expect(promoteImport).toHaveBeenCalledTimes(2));
    expect(approveImport).toHaveBeenCalledTimes(1);
  });

  it("falls back to the server's year when the carnival can't be read", async () => {
    vi.mocked(getCarnival).mockRejectedValue(new Error("offline"));
    renderWithQuery(<ImportReviewScreen carnivalId={CARNIVAL} />);
    const year = await screen.findByLabelText<HTMLSelectElement>("Competition year");
    expect(year.value).toBe("2027");
    expect(screen.queryByText(/This carnival is in/)).toBeNull();
  });

  describe("the synthetic-data line", () => {
    const line = "Synthetic or de-identified data only in this build.";

    it("shows in a development build", async () => {
      renderWithQuery(<ImportReviewScreen carnivalId={null} />);
      expect(await screen.findByText(line)).toBeTruthy();
    });

    it("shows in a demo build", async () => {
      vi.stubEnv("DEV", false);
      vi.stubEnv("VITE_DEMO_MODE", "true");
      renderWithQuery(<ImportReviewScreen carnivalId={null} />);
      expect(await screen.findByText(line)).toBeTruthy();
    });

    it("is absent from a school build", async () => {
      vi.stubEnv("DEV", false);
      vi.stubEnv("VITE_DEMO_MODE", "false");
      renderWithQuery(<ImportReviewScreen carnivalId={null} />);
      await screen.findByLabelText(/Student list: a CSV export/);
      expect(screen.queryByText(line)).toBeNull();
    });
  });
});
