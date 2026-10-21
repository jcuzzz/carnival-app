// @vitest-environment jsdom
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { _resetReachabilityForTests } from "../api/reachability";
import type { Carnival, CarnivalStatus } from "../api/types";
import { signIn, signOut } from "../auth/session";
import {
  _resetSelectedCarnivalForTests,
  getSelectedCarnival,
} from "../carnivals/selectedCarnival";
import { SWITCHER_ROW_LIMIT } from "../carnivals/switcherModel";
import { renderWithQuery } from "../test/queryWrapper";

/**
 * The switcher (ADR-0024 §3). What is pinned here is the bound: a school adds
 * carnivals every year for ever, and this is the screen a volunteer opens on
 * carnival morning to say where they are recording.
 */

vi.mock("../api/resources", () => ({
  listCarnivals: vi.fn(),
  createCarnival: vi.fn(),
}));

import { listCarnivals } from "../api/resources";
import CarnivalsScreen from "./CarnivalsScreen";

function carnival(
  id: string,
  date: string,
  status: CarnivalStatus = "Finalised",
  name = `Carnival ${id}`,
): Carnival {
  return {
    id,
    name,
    date,
    status,
    type: "Swimming",
    venue: "Preston Pool",
    ownerSubject: "s1",
    ruleVersion: "v1",
    version: 1,
  } as Carnival;
}

function history(years: number): Carnival[] {
  return Array.from({ length: years * 3 }, (_, i) =>
    carnival(`c-${i}`, `${1990 + Math.floor(i / 3)}-03-0${(i % 3) + 1}`),
  );
}

function rows(): HTMLElement[] {
  return screen.queryAllByRole("link").filter((el) => el.className.includes("row-link"));
}

beforeEach(() => {
  cleanup();
  _resetReachabilityForTests();
  _resetSelectedCarnivalForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  vi.mocked(listCarnivals).mockReset().mockResolvedValue([]);
  signIn({ subject: "s1", displayName: "Rae Recorder", roles: ["Recorder"] });
});

afterEach(() => {
  signOut();
});

describe("CarnivalsScreen signed out", () => {
  it("offers a sign-in on a cold landing instead of a raw 401", async () => {
    signOut();
    renderWithQuery(<CarnivalsScreen autoSelect />);

    expect(await screen.findByRole("link", { name: "Sign in" })).toBeTruthy();
    expect(screen.queryByText(/401/)).toBeNull();
    expect(vi.mocked(listCarnivals)).not.toHaveBeenCalled();
  });

  it("offers a sign-in when the server says the session has lapsed", async () => {
    vi.mocked(listCarnivals).mockRejectedValue(
      new ApiError(401, { error: "AUTHENTICATION_REQUIRED", detail: "…" }, "/api/v1/carnivals"),
    );
    renderWithQuery(<CarnivalsScreen />);

    expect(await screen.findByRole("link", { name: "Sign in" })).toBeTruthy();
    expect(screen.queryByText(/401/)).toBeNull();
  });
});

describe("CarnivalsScreen", () => {
  it("bounds the rendered rows, and still shows the live carnival first", async () => {
    const live = carnival("live", "2027-03-06", "Open", "Preston Swimming 2027");
    vi.mocked(listCarnivals).mockResolvedValue([...history(60), live]);

    renderWithQuery(<CarnivalsScreen />);
    await screen.findByText("Preston Swimming 2027");

    expect(rows()).toHaveLength(SWITCHER_ROW_LIMIT);
    expect(rows()[0]!.textContent).toContain("Preston Swimming 2027");
    expect(screen.getByText(/181 carnivals/)).toBeTruthy();
    expect(screen.getByText(/Showing the first 12 of 181/)).toBeTruthy();
  });

  it("keeps a small school's whole history on the page, with no search box", async () => {
    vi.mocked(listCarnivals).mockResolvedValue(history(3));

    renderWithQuery(<CarnivalsScreen />);
    await waitFor(() => expect(rows()).toHaveLength(9));

    expect(screen.queryByLabelText("Search carnivals")).toBeNull();
    expect(screen.queryByText(/Showing the first/)).toBeNull();
  });

  it("reaches an older carnival through the search box, still bounded", async () => {
    const old = carnival("old", "1998-08-01", "Finalised", "Athletics 1998");
    vi.mocked(listCarnivals).mockResolvedValue([...history(60), old]);

    renderWithQuery(<CarnivalsScreen />);
    await waitFor(() => expect(rows()).toHaveLength(SWITCHER_ROW_LIMIT));
    expect(screen.queryByText("Athletics 1998")).toBeNull();

    fireEvent.change(screen.getByLabelText("Search carnivals"), {
      target: { value: "athletics" },
    });

    await waitFor(() => expect(rows()).toHaveLength(1));
    expect(screen.getByText("Athletics 1998")).toBeTruthy();

    fireEvent.change(screen.getByLabelText("Search carnivals"), { target: { value: "preston" } });
    await waitFor(() => expect(rows()).toHaveLength(SWITCHER_ROW_LIMIT));
  });

  it("says so when nothing matches, rather than showing the unfiltered list", async () => {
    vi.mocked(listCarnivals).mockResolvedValue(history(60));

    renderWithQuery(<CarnivalsScreen />);
    await waitFor(() => expect(rows()).toHaveLength(SWITCHER_ROW_LIMIT));

    fireEvent.change(screen.getByLabelText("Search carnivals"), { target: { value: "zzz" } });

    await waitFor(() => expect(rows()).toHaveLength(0));
    expect(screen.getByText("No carnivals match that search.")).toBeTruthy();
  });

  it("auto-selects the one Open carnival even when it is past the bound", async () => {
    // ADR-0024 §1, owner ruling 2026-08-20: the pick reads the whole list, so
    // the render bound must not be able to change which carnival it finds.
    const live = carnival("live", "1991-03-06", "Open", "Preston Swimming");
    vi.mocked(listCarnivals).mockResolvedValue([...history(60), live]);

    renderWithQuery(<CarnivalsScreen autoSelect />);

    await waitFor(() => expect(getSelectedCarnival()?.id).toBe("live"));
  });

  it("does not auto-select when two carnivals are Open", async () => {
    vi.mocked(listCarnivals).mockResolvedValue([
      carnival("a", "2027-03-06", "Open"),
      carnival("b", "2027-03-07", "Open"),
      ...history(60),
    ]);

    renderWithQuery(<CarnivalsScreen autoSelect />);
    await waitFor(() => expect(rows()).toHaveLength(SWITCHER_ROW_LIMIT));

    expect(getSelectedCarnival()).toBeNull();
  });
});
