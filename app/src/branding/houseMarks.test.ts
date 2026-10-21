// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import { getHouseMarks } from "../api/resources";
import { signIn, signOut } from "../auth/session";
import { db } from "../db/db";
import {
  _resetHouseMarksForTests,
  applyHouseMarks,
  getHouseMarksState,
  houseColourVar,
  initHouseMarks,
  loadPersistedHouseMarks,
  refreshHouseMarks,
} from "./houseMarks";

vi.mock("../api/resources", () => ({ getHouseMarks: vi.fn() }));

const fetched = vi.mocked(getHouseMarks);
const prop = (name: string) => document.documentElement.style.getPropertyValue(name);

beforeEach(async () => {
  vi.resetAllMocks();
  signOut();
  _resetHouseMarksForTests();
  await db.appSettings.clear();
});

describe("applyHouseMarks (ADR-0044 §3)", () => {
  it("mints one custom property per coloured house, and none for the rest", () => {
    applyHouseMarks([
      { code: "BANKSIA", name: "Banksia", colour: "#3346c0" },
      { code: "MOUNTAIN ASH", name: "Mountain Ash", colour: "#973023" },
      { code: "WATTLE", name: "Wattle", colour: null },
    ]);

    expect(prop("--house-banksia")).toBe("#3346c0");
    expect(prop("--house-mountain-ash")).toBe("#973023");
    expect(prop("--house-wattle")).toBe("");
  });

  it("CLEARS a property when the house loses its colour", () => {
    applyHouseMarks([{ code: "WATTLE", name: "Wattle", colour: "#2e9e52" }]);
    expect(prop("--house-wattle")).toBe("#2e9e52");

    applyHouseMarks([{ code: "WATTLE", name: "Wattle", colour: null }]);
    expect(prop("--house-wattle")).toBe("");
    expect(houseColourVar("WATTLE")).toBeNull();
  });

  it("refuses a value that is not a colour rather than painting it", () => {
    applyHouseMarks([{ code: "WATTLE", name: "Wattle", colour: "green" }]);
    expect(prop("--house-wattle")).toBe("");
  });

  it("keeps two houses apart when their codes slug the same", () => {
    applyHouseMarks([
      { code: "MOUNTAIN ASH", name: "Mountain Ash", colour: "#973023" },
      { code: "Mountain-Ash", name: "Mountain-Ash House", colour: "#2e9e52" },
    ]);

    expect(prop("--house-mountain-ash")).toBe("#973023");
    expect(prop("--house-mountain-ash-2")).toBe("#2e9e52");
    // Matched by CODE first, so the second house is not handed the first's hue.
    expect(houseColourVar("MOUNTAIN ASH")).toBe("var(--house-mountain-ash)");
    expect(houseColourVar("Mountain-Ash")).toBe("var(--house-mountain-ash-2)");
  });

  it("names a house whose code slugs to nothing by its position", () => {
    applyHouseMarks([
      { code: "WATTLE", name: "Wattle", colour: "#2e9e52" },
      { code: "///", name: "Unnamed", colour: "#3346c0" },
    ]);

    expect(prop("--house-house-1")).toBe("#3346c0");
    expect(houseColourVar("///")).toBe("var(--house-house-1)");
  });
});

describe("boot", () => {
  it("paints the persisted marks first, then the server's", async () => {
    await db.appSettings.put({
      key: "houseMarks",
      value: [{ code: "WATTLE", name: "Wattle", colour: "#2e9e52" }],
    });
    signIn({ subject: "s1", displayName: "Alex Admin", roles: ["CarnivalAdministrator"] });
    fetched.mockResolvedValue([
      { code: "WATTLE", name: "Wattle", colour: "#2e9e52" },
      { code: "BANKSIA", name: "Banksia", colour: "#3346c0" },
    ]);

    await initHouseMarks();

    expect(prop("--house-banksia")).toBe("#3346c0");
    expect(getHouseMarksState()).toHaveLength(2);
    expect(await loadPersistedHouseMarks()).toHaveLength(2);
  });

  it("keeps the persisted marks silently when the read fails", async () => {
    await db.appSettings.put({
      key: "houseMarks",
      value: [{ code: "WATTLE", name: "Wattle", colour: "#2e9e52" }],
    });
    signIn({ subject: "s1", displayName: "Alex Admin", roles: ["CarnivalAdministrator"] });
    fetched.mockRejectedValue(new TypeError("Failed to fetch"));

    await expect(initHouseMarks()).resolves.toBeUndefined();
    expect(prop("--house-wattle")).toBe("#2e9e52");
  });

  it("picks the colours up when the device comes back online", async () => {
    signIn({ subject: "s1", displayName: "Alex Admin", roles: ["CarnivalAdministrator"] });
    fetched.mockRejectedValueOnce(new TypeError("Failed to fetch"));
    await initHouseMarks();
    expect(prop("--house-banksia")).toBe("");

    fetched.mockResolvedValue([{ code: "BANKSIA", name: "Banksia", colour: "#3346c0" }]);
    window.dispatchEvent(new Event("online"));
    await vi.waitFor(() => expect(prop("--house-banksia")).toBe("#3346c0"));

    // One listener, added once: a second online event must not refetch.
    const calls = fetched.mock.calls.length;
    window.dispatchEvent(new Event("online"));
    await vi.waitFor(() => expect(fetched.mock.calls.length).toBe(calls));
  });

  it("waits for a session — the houses read is authorised, unlike branding", async () => {
    fetched.mockResolvedValue([{ code: "WATTLE", name: "Wattle", colour: "#2e9e52" }]);

    await initHouseMarks();
    expect(fetched).not.toHaveBeenCalled();

    signIn({ subject: "s1", displayName: "Alex Admin", roles: ["Recorder"] });
    await vi.waitFor(() => expect(prop("--house-wattle")).toBe("#2e9e52"));
  });
});

describe("refreshHouseMarks (ADR-0045 §6)", () => {
  it("refetches past the once-per-session guard, repaints and re-persists", async () => {
    signIn({ subject: "s1", displayName: "Alex Admin", roles: ["SystemAdministrator"] });
    fetched.mockResolvedValue([{ code: "WATTLE", name: "Wattle", colour: "#2e9e52" }]);
    await initHouseMarks();
    expect(prop("--house-wattle")).toBe("#2e9e52");

    fetched.mockResolvedValue([{ code: "WATTLE", name: "Waratah", colour: "#b42318" }]);
    await refreshHouseMarks();

    expect(prop("--house-wattle")).toBe("#b42318");
    expect(getHouseMarksState()[0]!.name).toBe("Waratah");
    expect(await loadPersistedHouseMarks()).toEqual([
      { code: "WATTLE", name: "Waratah", colour: "#b42318" },
    ]);
  });

  it("leaves the painted colours alone when the refetch fails", async () => {
    signIn({ subject: "s1", displayName: "Alex Admin", roles: ["SystemAdministrator"] });
    fetched.mockResolvedValue([{ code: "WATTLE", name: "Wattle", colour: "#2e9e52" }]);
    await initHouseMarks();

    fetched.mockRejectedValue(new TypeError("Failed to fetch"));
    await expect(refreshHouseMarks()).resolves.toBeUndefined();
    expect(prop("--house-wattle")).toBe("#2e9e52");
  });
});
