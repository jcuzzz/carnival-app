// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import type { EligibleStudent } from "../../api/types";
import { AddEntry } from "./AddEntry";

/**
 * The eligible list sits directly under a division header that reads "13 · Boys"
 * (ADR-0022 §1), and that header is the ONLY place the category is named: a
 * student's own category never reaches a client (ADR-0026 §2). The age group
 * stays on the row — it is not derived from gender, and in an OPEN division the
 * header does not name it while the year level cannot imply it.
 */

vi.mock("../../api/resources", () => ({ searchEligibleStudentsPage: vi.fn() }));
vi.mock("../../db/roster", () => ({
  loadCachedRosterPage: vi.fn(),
  saveCachedRoster: vi.fn(),
  filterRoster: (students: EligibleStudent[]) => students,
}));
vi.mock("../../entries/submitEntry", () => ({ submitEntryViaOutbox: vi.fn() }));

import { searchEligibleStudentsPage } from "../../api/resources";
import { loadCachedRosterPage, saveCachedRoster } from "../../db/roster";

const student: EligibleStudent = {
  studentId: "s-1",
  displayName: "Jordan Blake",
  houseCode: "KIN",
  yearLevel: 8,
  ageGroup: "13",
};

beforeEach(() => {
  cleanup();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  vi.mocked(loadCachedRosterPage).mockReset().mockResolvedValue(null);
  vi.mocked(saveCachedRoster).mockReset().mockResolvedValue(undefined);
  vi.mocked(searchEligibleStudentsPage)
    .mockReset()
    .mockResolvedValue({ students: [student], truncated: false, limit: 50 });
});

const panel = () => (
  <AddEntry
    carnivalId="c-1"
    eventId="ev-1"
    actorSubject="admin-1"
    enteredStudentIds={new Set()}
    failedStudentIds={new Set()}
    onAdded={vi.fn()}
  />
);

describe("AddEntry", () => {
  it("identifies an eligible student by house, year and age group, and nothing else", async () => {
    render(panel());

    await waitFor(() => expect(screen.getByText("Jordan Blake")).toBeTruthy());
    expect(screen.getByText(/^KIN · Yr 8 · 13$/)).toBeTruthy();
  });

  it("puts no category on the row, however it is spelled (ADR-0026 §2)", async () => {
    render(panel());

    await waitFor(() => expect(screen.getByText("Jordan Blake")).toBeTruthy());
    const row = screen.getByText("Jordan Blake").closest("li")!;
    expect(row.textContent).toBe("Jordan BlakeKIN · Yr 8 · 13Add");
    for (const spelling of ["Boys", "Girls", "Open", "M", "F", "S"]) {
      expect(within(row).queryByText(spelling)).toBeNull();
      expect(row.textContent).not.toMatch(new RegExp(`\\b${spelling}\\b`));
    }
  });
});
