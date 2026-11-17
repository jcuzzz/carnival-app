import { act, cleanup, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Carnival, RoleAssignment, SchoolSettings } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { UNCONFIGURED_SCHOOL_NAME } from "../features/school/schoolSteps";
import { renderWithQuery } from "../test/queryWrapper";

vi.mock("../api/resources", () => ({
  getSchoolSettings: vi.fn(),
  listRoleAssignments: vi.fn(),
  listCarnivals: vi.fn(),
}));

vi.mock("../auth/localAccounts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../auth/localAccounts")>()),
  getMyAccount: vi.fn(),
}));

import { getSchoolSettings, listCarnivals, listRoleAssignments } from "../api/resources";
import { getMyAccount } from "../auth/localAccounts";
import SchoolScreen from "./SchoolScreen";

const ADMIN = {
  subject: "local:1",
  displayName: "Backup Admin",
  roles: ["SystemAdministrator" as const],
  localAccount: true,
};

function named(name: string, setupConfirmed = false): SchoolSettings {
  return { name, setupConfirmed, houses: [] } as unknown as SchoolSettings;
}

const SELF: RoleAssignment = {
  upn: "backup-admin",
  bootstrap: false,
  revokedAtUtc: null,
  assignedBySubject: "host",
  role: "SystemAdministrator",
} as RoleAssignment;

beforeEach(() => {
  cleanup();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  vi.mocked(getSchoolSettings).mockReset().mockResolvedValue(named(UNCONFIGURED_SCHOOL_NAME));
  vi.mocked(listRoleAssignments).mockReset().mockResolvedValue([SELF]);
  vi.mocked(listCarnivals).mockReset().mockResolvedValue([]);
  vi.mocked(getMyAccount)
    .mockReset()
    .mockResolvedValue({
      username: "backup-admin",
      displayName: "Backup Admin",
      authenticatorEnrolled: true,
      recoveryCodesLeft: 10,
      requireAuthenticator: false,
    });
});

afterEach(() => {
  cleanup();
  signOut();
});

describe("SchoolScreen — Set up your school", () => {
  it("lists the three steps for a fresh school, pointing at the settings first", async () => {
    signIn(ADMIN);
    renderWithQuery(<SchoolScreen />);

    await screen.findByRole("heading", { name: "Set up your school", level: 2 });
    const checklist = document.querySelector("ol.row-list")!;
    await waitFor(() => expect(checklist.querySelectorAll(".badge.info")).toHaveLength(1));
    const titles = [...checklist.querySelectorAll(".row-title")].map((t) => t.textContent);
    expect(titles[0]).toMatch(/^1\. School name, colours, houses and ages.*Do this next/);
    expect(titles[1]).toMatch(/^2\. Staff accounts.*Optional/);
    expect(titles[2]).toMatch(/^3\. Your first carnival/);
    expect(
      [...checklist.querySelectorAll<HTMLAnchorElement>("a.row-link")].map((a) => a.hash),
    ).toEqual(["#/school/settings", "#/school/people", "#/carnivals"]);
    expect(screen.getByRole("link", { name: /Update the event catalogue/ })).toBeTruthy();
  });

  it("still lists the steps for a school whose name was seeded but never saved", async () => {
    vi.mocked(getSchoolSettings).mockResolvedValue(named("Riverbend College"));
    vi.mocked(listCarnivals).mockResolvedValue([{ id: "c-1" } as Carnival]);
    signIn(ADMIN);
    renderWithQuery(<SchoolScreen />);

    await screen.findByRole("heading", { name: "Set up your school", level: 2 });
    const next = document.querySelector("ol.row-list a.row-link")!;
    await waitFor(() => expect(next.textContent).toMatch(/School name, colours, houses and ages.*Do this next/));
  });

  it("shows nothing of setup once the settings are saved and a carnival exists", async () => {
    vi.mocked(getSchoolSettings).mockResolvedValue(named("Riverbend College", true));
    vi.mocked(listCarnivals).mockResolvedValue([{ id: "c-1" } as Carnival]);
    signIn(ADMIN);
    renderWithQuery(<SchoolScreen />);

    await screen.findByRole("link", { name: /School settings/ });
    await waitFor(() => expect(listCarnivals).toHaveBeenCalled());
    // Nothing appears to wait for, so let every read land first.
    await act(() => new Promise((resolve) => setTimeout(resolve, 50)));
    expect(document.querySelector("ul.row-list.subsection")).toBeNull();
    expect(screen.queryByRole("heading", { name: "Set up your school" })).toBeNull();
    expect(screen.queryByText("School set up")).toBeNull();
    expect(document.querySelector("ol.row-list")).toBeNull();
  });

  it("shows nothing of it until every read has answered", async () => {
    vi.mocked(listCarnivals).mockReturnValue(new Promise(() => {}));
    signIn(ADMIN);
    renderWithQuery(<SchoolScreen />);

    await screen.findByRole("link", { name: /School settings/ });
    await waitFor(() => expect(getSchoolSettings).toHaveBeenCalled());
    expect(screen.queryByRole("heading", { name: "Set up your school" })).toBeNull();
    expect(screen.queryByText("School set up")).toBeNull();
  });

  it("keeps the list, with the step unknown, when a read fails", async () => {
    vi.mocked(listCarnivals).mockRejectedValue(new Error("offline"));
    vi.mocked(getSchoolSettings).mockResolvedValue(named("Riverbend College", true));
    signIn(ADMIN);
    renderWithQuery(<SchoolScreen />);

    await screen.findByRole("heading", { name: "Set up your school" });
    await waitFor(() => expect(document.querySelectorAll("ol .badge.ok")).toHaveLength(1));
    expect(screen.queryByText("School set up")).toBeNull();
  });

  it("is not shown to a carnival administrator, who reads none of it", async () => {
    signIn({ subject: "s2", displayName: "Ada Admin", roles: ["CarnivalAdministrator"] });
    renderWithQuery(<SchoolScreen />);

    expect(await screen.findByRole("link", { name: /Event catalogue/ })).toBeTruthy();
    expect(screen.queryByRole("heading", { name: "Set up your school" })).toBeNull();
    expect(screen.queryByText("School set up")).toBeNull();
    expect(getSchoolSettings).not.toHaveBeenCalled();
    expect(listRoleAssignments).not.toHaveBeenCalled();
  });
});
