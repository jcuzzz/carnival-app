// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import type { EligibleStudent } from "../../api/types";
import { renderWithQuery } from "../../test/queryWrapper";
import { AddTeam } from "./AddTeam";

/**
 * The per-leg search is a search like Add-entry's and inherits its contract
 * (K11): a full page is indistinguishable from a complete result set, and "the
 * swimmer isn't in the list" is a carnival-day dead end. Same flag, same words.
 */

vi.mock("../../api/resources", () => ({
  listHouses: vi.fn(),
  searchEligibleStudentsPage: vi.fn(),
}));
vi.mock("../../db/roster", () => ({
  loadCachedRosterPage: vi.fn(),
  saveCachedRoster: vi.fn(),
  filterRoster: (students: EligibleStudent[]) => students,
}));
vi.mock("../../entries/submitTeam", () => ({ submitTeamViaOutbox: vi.fn() }));

import { listHouses, searchEligibleStudentsPage } from "../../api/resources";
import { loadCachedRosterPage, saveCachedRoster } from "../../db/roster";
import { submitTeamViaOutbox } from "../../entries/submitTeam";

const swimmer: EligibleStudent = {
  studentId: "s-1",
  displayName: "Ari Swift",
  houseCode: "BANKSIA",
  yearLevel: 9,
  ageGroup: "14",
};

beforeEach(() => {
  cleanup();
  vi.mocked(listHouses).mockReset().mockResolvedValue(["BANKSIA", "WATTLE"]);
  vi.mocked(loadCachedRosterPage).mockReset().mockResolvedValue(null);
  vi.mocked(saveCachedRoster).mockReset().mockResolvedValue(undefined);
  vi.mocked(searchEligibleStudentsPage)
    .mockReset()
    .mockResolvedValue({ students: [swimmer], truncated: false, limit: 50 });
  vi.mocked(submitTeamViaOutbox).mockReset().mockResolvedValue({} as never);
});

function renderPanel() {
  return renderWithQuery(
    <AddTeam
      carnivalId="c-1"
      eventId="ev-1"
      teamSize={4}
      legs={["Backstroke", "Breaststroke", "Butterfly", "Freestyle"]}
      ageGroup="14"
      actorSubject="marshal-1"
      disabled={false}
      onAdded={vi.fn()}
    />,
  );
}

/** Pick a house, then type into the first leg's search box. */
async function searchLegOne(term: string) {
  renderPanel();
  fireEvent.click(await screen.findByRole("button", { name: "BANKSIA" }));
  const boxes = await screen.findAllByPlaceholderText("Search BANKSIA swimmers");
  expect(boxes).toHaveLength(4);
  fireEvent.change(boxes[0], { target: { value: term } });
}

describe("AddTeam per-leg search", () => {
  it("offers the house's eligible swimmers and says nothing about truncation", async () => {
    await searchLegOne("ari");

    await waitFor(() => expect(screen.getByRole("button", { name: /Ari Swift/ })).toBeTruthy());
    expect(screen.queryByText(/showing the first/)).toBeNull();
    expect(screen.queryByText(/may be incomplete/)).toBeNull();
  });

  it("says there are more matches than fit when the server capped the page", async () => {
    vi.mocked(searchEligibleStudentsPage).mockResolvedValue({
      students: [swimmer],
      truncated: true,
      limit: 50,
    });
    await searchLegOne("a");

    await waitFor(() =>
      expect(
        screen.getByText("More matches than fit — showing the first 50. Keep typing to narrow."),
      ).toBeTruthy(),
    );
  });

  it("treats a saved snapshot with no page metadata as capped, not complete", async () => {
    // The pre-metadata cached row reads back truncated with no limit, so the
    // fallback can say it might not be everyone but not how many it holds.
    vi.mocked(loadCachedRosterPage).mockResolvedValue({
      students: [swimmer],
      truncated: true,
      limit: null,
      fetchedAt: new Date().toISOString(),
    });
    vi.mocked(searchEligibleStudentsPage).mockRejectedValue(new TypeError("Failed to fetch"));
    await searchLegOne("a");

    await waitFor(() =>
      expect(screen.getByText("The saved roster may be incomplete — type to narrow.")).toBeTruthy(),
    );
    // Every leg's search failed, so every leg says so; the notice above is the
    // one that reports what the fallback it dropped to can vouch for.
    expect(screen.getAllByText(/Can't reach the server/)).toHaveLength(4);
  });

  it("says the same of a capped snapshot that DOES know its page size", async () => {
    // Offline the filter runs over the snapshot, so quoting the server's page
    // size and inviting more typing promises swimmers this device cannot reach.
    vi.mocked(loadCachedRosterPage).mockResolvedValue({
      students: [swimmer],
      truncated: true,
      limit: 50,
      fetchedAt: new Date().toISOString(),
    });
    vi.mocked(searchEligibleStudentsPage).mockRejectedValue(new TypeError("Failed to fetch"));
    await searchLegOne("a");

    await waitFor(() =>
      expect(screen.getByText("The saved roster may be incomplete — type to narrow.")).toBeTruthy(),
    );
    expect(screen.queryByText(/showing the first 50/)).toBeNull();
  });
});

describe("AddTeam heat context", () => {
  async function addOneLegTeam(raceId: string | null) {
    renderWithQuery(
      <AddTeam
        carnivalId="c-1"
        eventId="ev-1"
        teamSize={1}
        legs={["Freestyle"]}
        ageGroup="14"
        actorSubject="marshal-1"
        actorDisplayName="Marley Singh"
        disabled={false}
        raceId={raceId}
        onAdded={vi.fn()}
      />,
    );
    fireEvent.click(await screen.findByRole("button", { name: "BANKSIA" }));
    fireEvent.change(await screen.findByPlaceholderText("Search BANKSIA swimmers"), {
      target: { value: "ari" },
    });
    fireEvent.click(await screen.findByRole("button", { name: /Ari Swift/ }));
    fireEvent.click(screen.getByRole("button", { name: "Add BANKSIA team" }));
  }

  it("captures the relay through the outbox, naming the selected heat", async () => {
    await addOneLegTeam("r-2");

    await waitFor(() =>
      expect(vi.mocked(submitTeamViaOutbox)).toHaveBeenCalledWith(
        expect.objectContaining({
          carnivalId: "c-1",
          carnivalEventId: "ev-1",
          houseCode: "BANKSIA",
          swimmers: [swimmer],
          strokes: ["Freestyle"],
          ageGroup: "14",
          actorSubject: "marshal-1",
          actorDisplayName: "Marley Singh",
          raceId: "r-2",
        }),
      ),
    );
  });

  it("no longer waits for a heat this device has only queued (ADR-0039 A4)", async () => {
    // The capture rides the outbox now, so it sorts BEHIND this device's own
    // mint instead of overtaking it: the panel offers the add, and nothing on it
    // tells the marshal to come back later.
    await addOneLegTeam("r-queued");

    await waitFor(() => expect(vi.mocked(submitTeamViaOutbox)).toHaveBeenCalledTimes(1));
    expect(screen.queryByText(/still syncing/)).toBeNull();
  });

  it("adds nothing while this division's heat stands refused (M11)", async () => {
    // Add-entry's gate, on the relay panel: a capture queued behind a race the
    // server has turned down waits for a heat that will never exist.
    const blocked = "The server refused this heat — clear it from the Outbox first";
    renderWithQuery(
      <AddTeam
        carnivalId="c-1"
        eventId="ev-1"
        teamSize={1}
        legs={["Freestyle"]}
        ageGroup="14"
        actorSubject="marshal-1"
        disabled={false}
        raceId={null}
        blocked={blocked}
        onAdded={vi.fn()}
      />,
    );
    expect(await screen.findByText(blocked)).toBeTruthy();
    fireEvent.click(await screen.findByRole("button", { name: "BANKSIA" }));
    fireEvent.change(await screen.findByPlaceholderText("Search BANKSIA swimmers"), {
      target: { value: "ari" },
    });
    fireEvent.click(await screen.findByRole("button", { name: /Ari Swift/ }));

    const add = screen.getByRole("button", { name: "Add BANKSIA team" }) as HTMLButtonElement;
    expect(add.disabled).toBe(true);
    fireEvent.click(add);
    await waitFor(() => expect(screen.getByText(blocked)).toBeTruthy());
    expect(vi.mocked(submitTeamViaOutbox)).not.toHaveBeenCalled();
  });

  it("says the capture never left this device when the queue itself refuses", async () => {
    vi.mocked(submitTeamViaOutbox).mockRejectedValue(new Error("dexie is gone"));
    await addOneLegTeam("r-2");

    expect(
      await screen.findByText("Couldn't queue the BANKSIA team on this device — try again."),
    ).toBeTruthy();
  });
});
