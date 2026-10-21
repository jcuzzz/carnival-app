// @vitest-environment jsdom
import { cleanup, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { _resetReachabilityForTests } from "../api/reachability";
import type { Carnival } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { renderHookWithQuery } from "../test/queryWrapper";
import {
  _resetSelectedCarnivalForTests,
  adoptCarnival,
  getSelectedCarnival,
  selectCarnival,
} from "./selectedCarnival";

/**
 * The two halves of ADR-0024 §§2/9 that shipped unbuilt: a deep-linked
 * carnival's cache is filled in from ONE place, and a selection whose carnival
 * is gone is dropped — but only on proof of absence, never on a dead uplink.
 */

vi.mock("../api/resources", () => ({ getCarnival: vi.fn() }));

import { getCarnival } from "../api/resources";
import { useCarnivalContext } from "./useCarnivalContext";

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
  cleanup();
  _resetReachabilityForTests();
  _resetSelectedCarnivalForTests();
  localStorage.clear();
  vi.mocked(getCarnival).mockReset().mockResolvedValue(carnival());
  signIn({ subject: "s1", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
});

afterEach(() => signOut());

describe("the carnival context (ADR-0024 §§1–2)", () => {
  it("names a deep-linked carnival the chip could not name — on ANY screen", async () => {
    // The capture screen is what a printed program link points at, and it does
    // not fetch the carnival itself.
    adoptCarnival("c-1");
    expect(getSelectedCarnival()).toEqual({ id: "c-1", name: null, date: null, status: null });

    renderHookWithQuery(() => useCarnivalContext());

    await waitFor(() => expect(getSelectedCarnival()?.name).toBe("Preston Swimming"));
    expect(getSelectedCarnival()?.status).toBe("Open");
  });

  it("drops a selection whose carnival is gone, so / stops landing on a dead id", async () => {
    selectCarnival(carnival());
    vi.mocked(getCarnival).mockRejectedValue(new ApiError(404, undefined, "/carnivals/c-1"));

    renderHookWithQuery(() => useCarnivalContext());

    await waitFor(() => expect(getSelectedCarnival()).toBeNull());
  });

  it("KEEPS the selection when the uplink is dead — an error is not absence (ADR-0012)", async () => {
    selectCarnival(carnival());
    vi.mocked(getCarnival).mockRejectedValue(new TypeError("Failed to fetch"));

    renderHookWithQuery(() => useCarnivalContext());

    // Give the query time to settle into its error state before asserting.
    await waitFor(() => expect(vi.mocked(getCarnival)).toHaveBeenCalled());
    await new Promise((r) => setTimeout(r, 20));
    expect(getSelectedCarnival()?.id).toBe("c-1");
  });

  it("KEEPS the selection on a 401 — a lapsed session is not a deleted carnival", async () => {
    selectCarnival(carnival());
    vi.mocked(getCarnival).mockRejectedValue(new ApiError(401, undefined, "/carnivals/c-1"));

    renderHookWithQuery(() => useCarnivalContext());

    await waitFor(() => expect(vi.mocked(getCarnival)).toHaveBeenCalled());
    await new Promise((r) => setTimeout(r, 20));
    expect(getSelectedCarnival()?.id).toBe("c-1");
  });

  it("asks for nothing when no carnival is selected", async () => {
    renderHookWithQuery(() => useCarnivalContext());
    await new Promise((r) => setTimeout(r, 20));
    expect(vi.mocked(getCarnival)).not.toHaveBeenCalled();
  });

  it("asks for nothing before sign-in — every read would be a 401", async () => {
    signOut();
    selectCarnival(carnival());
    renderHookWithQuery(() => useCarnivalContext());
    await new Promise((r) => setTimeout(r, 20));
    expect(vi.mocked(getCarnival)).not.toHaveBeenCalled();
  });
});
