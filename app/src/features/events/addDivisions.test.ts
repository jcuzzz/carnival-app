import { beforeEach, describe, expect, it, vi } from "vitest";
import type { CarnivalSettings, DivisionProfile } from "../../api/types";
import { ensureProfile, fullMatrixSpecs, rosterDefaultToSave } from "./addDivisions";

/**
 * The write both presses share (ADR-0059 §2): the roster default becomes the
 * carnival's profile, or nothing is written at all.
 */

vi.mock("../../api/resources", () => ({
  getCarnivalSettings: vi.fn(),
  updateCarnivalSettings: vi.fn(),
}));

import { getCarnivalSettings, updateCarnivalSettings } from "../../api/resources";

const rosterDefault: DivisionProfile = {
  ageGroups: ["12", "13"],
  categories: ["M", "F"],
  tiers: ["Competitive"],
};

function settings(divisions: DivisionProfile | null): CarnivalSettings {
  return { divisions, divisionsDefault: rosterDefault } as CarnivalSettings;
}

beforeEach(() => {
  vi.mocked(getCarnivalSettings).mockReset().mockResolvedValue(settings(null));
  vi.mocked(updateCarnivalSettings).mockReset().mockResolvedValue(settings(rosterDefault));
});

describe("ensureProfile", () => {
  it("writes nothing where the carnival already has its own profile", async () => {
    expect(await ensureProfile("c-1", null, () => undefined, "added")).toEqual({ kind: "ready" });
    expect(getCarnivalSettings).not.toHaveBeenCalled();
    expect(updateCarnivalSettings).not.toHaveBeenCalled();
  });

  it("saves the default, and says the cache is stale", async () => {
    const changed = vi.fn();
    expect(await ensureProfile("c-1", rosterDefault, changed, "added")).toEqual({ kind: "ready" });
    expect(vi.mocked(updateCarnivalSettings).mock.calls[0]).toEqual([
      "c-1",
      { divisions: rosterDefault },
    ]);
    expect(changed).toHaveBeenCalled();
  });

  it("refuses when ages nobody saw were saved since the screen loaded", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue(
      settings({ ageGroups: ["16"], categories: ["M"], tiers: ["Competitive"] }),
    );
    const changed = vi.fn();
    expect(await ensureProfile("c-1", rosterDefault, changed, "added")).toEqual({ kind: "raced" });
    expect(updateCarnivalSettings).not.toHaveBeenCalled();
    expect(changed).toHaveBeenCalled();
  });

  // A second row pressed while the first is still adding: the profile now
  // stored is the one this press was about to write, so it may go on.
  it("goes ahead when the stored ages are the ones it was about to save", async () => {
    vi.mocked(getCarnivalSettings).mockResolvedValue(settings({ ...rosterDefault }));
    expect(await ensureProfile("c-1", rosterDefault, () => undefined, "added")).toEqual({
      kind: "ready",
    });
    expect(updateCarnivalSettings).not.toHaveBeenCalled();
  });

  // The sentence is the caller's: the setup list adds a row, Create creates one.
  it("reports a refused save in the words of the press that made it", async () => {
    vi.mocked(updateCarnivalSettings).mockRejectedValue(new Error("nope"));
    const added = await ensureProfile("c-1", rosterDefault, () => undefined, "added");
    expect(added.kind).toBe("failed");
    expect(added.kind === "failed" && added.text).toContain(
      "The ages couldn’t be saved, so nothing was added",
    );
    const created = await ensureProfile("c-1", rosterDefault, () => undefined, "created");
    expect(created.kind === "failed" && created.text).toContain("nothing was created");
  });
});

describe("fullMatrixSpecs", () => {
  it("is every division the profile contests, row-major", () => {
    expect(fullMatrixSpecs(rosterDefault)).toEqual([
      { ageGroup: "12", category: "M", tier: "Competitive" },
      { ageGroup: "12", category: "F", tier: "Competitive" },
      { ageGroup: "13", category: "M", tier: "Competitive" },
      { ageGroup: "13", category: "F", tier: "Competitive" },
    ]);
  });

  // The same specs the new-event grid sends with every box ticked: an OPEN-only
  // profile is one row, and the age the server reads is null.
  it("carries an open division as the null age the server expects", () => {
    expect(fullMatrixSpecs({ ageGroups: ["OPEN"], categories: ["S"], tiers: ["Social"] })).toEqual([
      { ageGroup: null, category: "S", tier: "Social" },
    ]);
  });

  it("is empty — so the press is disabled — where the profile is not configured", () => {
    expect(fullMatrixSpecs(null)).toEqual([]);
    expect(fullMatrixSpecs({ ageGroups: ["12"], categories: [], tiers: ["Competitive"] })).toEqual(
      [],
    );
  });
});

describe("rosterDefaultToSave", () => {
  it("is the default only while nothing is stored", () => {
    expect(rosterDefaultToSave(null, rosterDefault)).toEqual(rosterDefault);
    expect(rosterDefaultToSave(rosterDefault, rosterDefault)).toBeNull();
    expect(rosterDefaultToSave(null, null)).toBeNull();
  });
});
