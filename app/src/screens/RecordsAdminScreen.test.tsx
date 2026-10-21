// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { _resetReachabilityForTests } from "../api/reachability";
import type { SchoolRecord } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { renderWithQuery } from "../test/queryWrapper";

/**
 * Record custody (ADR-0035). What this screen must never do: offer to delete a
 * record, void one without a typed reason, void one in a single tap, or hide a
 * voided record once it is voided — the kept row IS the custody history.
 */

vi.mock("../api/resources", () => ({
  getRecordsForAdministration: vi.fn(),
  voidRecord: vi.fn(),
}));

import { getRecordsForAdministration, voidRecord } from "../api/resources";
import RecordsAdminScreen from "./RecordsAdminScreen";

function record(overrides: Partial<SchoolRecord> = {}): SchoolRecord {
  return {
    id: "rec-1",
    eventDefinitionId: "def-1",
    eventCode: "SW-50FR",
    eventName: "50m Freestyle",
    ageGroup: "13",
    category: "M",
    division: "13 Boys",
    value: 31.2,
    formattedValue: "31.20",
    higherBetter: false,
    holderDisplayName: "Aiden Brook",
    setOn: "2027-03-06",
    carnivalName: "Swimming Carnival 2027",
    voided: false,
    voidedAtUtc: null,
    voidedBySubject: null,
    voidReason: null,
    ...overrides,
  };
}

beforeEach(() => {
  cleanup();
  _resetReachabilityForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  signIn({ subject: "admin-1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
  vi.mocked(getRecordsForAdministration).mockReset().mockResolvedValue([record()]);
  vi.mocked(voidRecord).mockReset().mockResolvedValue(record({ voided: true }));
});

afterEach(() => {
  signOut();
});

describe("RecordsAdminScreen", () => {
  it("lists the book by event, with holder, value, carnival and date", async () => {
    renderWithQuery(<RecordsAdminScreen />);

    expect(await screen.findByText(/SW-50FR/)).toBeTruthy();
    expect(screen.getByText(/13 Boys · 31\.20 · Aiden Brook/)).toBeTruthy();
    expect(screen.getByText(/Swimming Carnival 2027 · 2027-03-06/)).toBeTruthy();
  });

  it("says what voiding does and does not do to the results book", async () => {
    renderWithQuery(<RecordsAdminScreen />);

    const sentence = await screen.findByText(/results\s*book still shows the swim/);
    expect(sentence.textContent).toContain("what changes is the record book");
  });

  it("offers no way to delete or edit a record", async () => {
    renderWithQuery(<RecordsAdminScreen />);
    await screen.findByText(/SW-50FR/);

    const labels = screen.getAllByRole("button").map((b) => b.textContent ?? "");
    expect(labels.some((label) => /delete/i.test(label))).toBe(false);
    expect(labels.some((label) => /edit/i.test(label))).toBe(false);
  });

  it("takes two steps and a typed reason to void", async () => {
    renderWithQuery(<RecordsAdminScreen />);

    // Step one arms the strip and writes nothing.
    fireEvent.click(await screen.findByRole("button", { name: "Void…" }));
    expect(voidRecord).not.toHaveBeenCalled();

    const confirm = screen.getByRole("button", { name: "Confirm voiding the 13 Boys record" });
    expect(confirm.className).toContain("armed");
    // Armed but not usable: the reason is what the second step is FOR.
    expect((confirm as HTMLButtonElement).disabled).toBe(true);

    fireEvent.change(screen.getByLabelText(/Reason for voiding/), {
      target: { value: "Timekeeper's sheet misread." },
    });
    expect((confirm as HTMLButtonElement).disabled).toBe(false);
    fireEvent.click(confirm);

    await waitFor(() =>
      expect(voidRecord).toHaveBeenCalledWith("rec-1", "Timekeeper's sheet misread."),
    );
  });

  it("says the void cannot be undone before it is confirmed", async () => {
    renderWithQuery(<RecordsAdminScreen />);
    fireEvent.click(await screen.findByRole("button", { name: "Void…" }));

    expect(screen.getByRole("alert").textContent).toContain("cannot be un-voided");
    expect(screen.getByRole("alert").textContent).toContain("result itself is unchanged");
  });

  it("cancels back to safe without writing anything", async () => {
    renderWithQuery(<RecordsAdminScreen />);
    fireEvent.click(await screen.findByRole("button", { name: "Void…" }));
    fireEvent.change(screen.getByLabelText(/Reason for voiding/), {
      target: { value: "Changed my mind." },
    });
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(voidRecord).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Void…" })).toBeTruthy();
  });

  it("keeps a voided record on screen, struck, with its reason and who voided it", async () => {
    // 22:15 UTC is the NEXT morning in Melbourne: slicing the ISO string files a
    // breakfast-time void under the previous day, which is not the day anyone
    // would go looking for it under.
    const voidedAtUtc = "2027-03-09T22:15:22.000Z";
    vi.mocked(getRecordsForAdministration).mockResolvedValue([
      record({
        voided: true,
        voidedAtUtc,
        voidedBySubject: "ada.admin",
        voidReason: "Timekeeper's sheet misread.",
      }),
    ]);
    renderWithQuery(<RecordsAdminScreen />);

    const title = await screen.findByText(/13 Boys · 31\.20 · Aiden Brook/);
    expect(title.className).toContain("voided");
    const custody = screen.getByText(/^Voided .* by ada\.admin/);
    expect(custody.textContent).toContain(new Date(voidedAtUtc).toLocaleDateString());
    expect(custody.textContent).not.toContain("2027-03-09");
    expect(custody.textContent).toContain("Timekeeper's sheet misread.");
    // Already gone from the book: there is nothing left to void.
    expect(screen.queryByRole("button", { name: "Void…" })).toBeNull();
  });

  it("refuses plainly offline rather than queueing the void", async () => {
    Object.defineProperty(navigator, "onLine", { value: false, configurable: true });
    renderWithQuery(<RecordsAdminScreen />);

    const notice = await screen.findByRole("status");
    expect(notice.textContent).toContain("keeps no offline copy");
    const button = await screen.findByRole("button", { name: "Void…" });
    expect((button as HTMLButtonElement).disabled).toBe(true);
  });

  it("shows the server's refusal and leaves the row standing", async () => {
    vi.mocked(voidRecord).mockRejectedValue(
      new ApiError(
        409,
        { error: "SCHOOL_RECORD_ALREADY_VOIDED", detail: "That record is already voided." },
        "/api/v1/records/rec-1/void",
      ),
    );
    renderWithQuery(<RecordsAdminScreen />);

    fireEvent.click(await screen.findByRole("button", { name: "Void…" }));
    fireEvent.change(screen.getByLabelText(/Reason for voiding/), {
      target: { value: "Mistimed." },
    });
    fireEvent.click(screen.getByRole("button", { name: "Confirm voiding the 13 Boys record" }));

    expect(await screen.findByText(/already voided/)).toBeTruthy();
  });

  it("needs the administrator role, and reads nothing without it", async () => {
    signIn({ subject: "view-1", displayName: "Vic Viewer", roles: ["Viewer"] });
    renderWithQuery(<RecordsAdminScreen />);

    expect(screen.getByRole("alert").textContent).toContain("carnival administrator role");
    expect(getRecordsForAdministration).not.toHaveBeenCalled();
  });

  it("offers a sign-in to a signed-out visitor rather than naming a role", async () => {
    signOut();
    renderWithQuery(<RecordsAdminScreen />);

    expect(await screen.findByRole("link", { name: "Sign in" })).toBeTruthy();
    expect(screen.queryByText(/carnival administrator role/)).toBeNull();
    expect(getRecordsForAdministration).not.toHaveBeenCalled();
  });

  it("says the book is empty rather than showing nothing at all", async () => {
    vi.mocked(getRecordsForAdministration).mockResolvedValue([]);
    renderWithQuery(<RecordsAdminScreen />);

    expect(await screen.findByText(/first finalised carnival seeds the book/)).toBeTruthy();
  });
});
