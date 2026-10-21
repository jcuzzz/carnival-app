// @vitest-environment jsdom
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Carnival } from "../api/types";
import {
  _resetSelectedCarnivalForTests,
  adoptCarnival,
  clearSelectedCarnival,
  getSelectedCarnival,
  selectCarnival,
  syncSelectedCarnival,
} from "./selectedCarnival";

function carnival(over: Partial<Carnival> = {}): Carnival {
  return {
    id: "c-1",
    name: "Preston Swimming",
    date: "2027-03-06",
    status: "Open",
    type: "Swimming",
    venue: "Preston Pool",
    ownerSubject: "s1",
    ruleVersion: "2027.1",
    version: 1,
    ...over,
  } as Carnival;
}

beforeEach(() => {
  localStorage.clear();
  _resetSelectedCarnivalForTests();
});

describe("the selected carnival (ADR-0024 §1)", () => {
  it("caches enough to name the carnival in the chrome with no fetch", () => {
    selectCarnival(carnival());
    expect(getSelectedCarnival()).toEqual({
      id: "c-1",
      name: "Preston Swimming",
      date: "2027-03-06",
      status: "Open",
    });
  });

  it("survives a reload — the selection is the point", () => {
    selectCarnival(carnival());
    // The module reads localStorage at import; assert the write is what a
    // fresh load would find.
    expect(JSON.parse(localStorage.getItem("sports.carnival")!)).toMatchObject({ id: "c-1" });
  });

  it("clears", () => {
    selectCarnival(carnival());
    clearSelectedCarnival();
    expect(getSelectedCarnival()).toBeNull();
    expect(localStorage.getItem("sports.carnival")).toBeNull();
  });
});

describe("adopting a deep link (ADR-0024 §9)", () => {
  it("switches to the carnival the URL names, with the cache not yet known", () => {
    selectCarnival(carnival());
    adoptCarnival("c-2");
    expect(getSelectedCarnival()).toEqual({ id: "c-2", name: null, date: null, status: null });
  });

  it("is a NO-OP for the carnival already selected", () => {
    // Otherwise every render of that carnival's screens would blank the cached
    // name back to null and the chip would flicker.
    selectCarnival(carnival());
    adoptCarnival("c-1");
    expect(getSelectedCarnival()?.name).toBe("Preston Swimming");
  });

  it("fills the cache in once a screen holds the real payload", () => {
    adoptCarnival("c-1");
    syncSelectedCarnival(carnival());
    expect(getSelectedCarnival()?.name).toBe("Preston Swimming");
  });

  it("ignores a payload for a carnival that is no longer selected", () => {
    selectCarnival(carnival());
    adoptCarnival("c-2");
    syncSelectedCarnival(carnival({ id: "c-1" }));
    expect(getSelectedCarnival()?.id).toBe("c-2");
  });

  it("picks up a status change — the landing rule reads it", () => {
    selectCarnival(carnival());
    syncSelectedCarnival(carnival({ status: "Finalised" }));
    expect(getSelectedCarnival()?.status).toBe("Finalised");
  });
});

describe("a storage layer that refuses", () => {
  it("keeps the selection for this tab rather than throwing out of a render", () => {
    const setItem = vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new DOMException("QuotaExceededError");
    });
    try {
      expect(() => selectCarnival(carnival())).not.toThrow();
      expect(getSelectedCarnival()?.id).toBe("c-1");
    } finally {
      setItem.mockRestore();
    }
  });
});
