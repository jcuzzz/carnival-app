import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { _resetReachabilityForTests } from "../api/reachability";
import type { LocalAccount, SchoolSettings } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { _resetToastsForTests, getToasts } from "../shell/toast";
import { renderWithQuery } from "../test/queryWrapper";

vi.mock("../auth/localAccounts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../auth/localAccounts")>()),
  listLocalAccounts: vi.fn(),
  createLocalAccount: vi.fn(),
  resetLocalAccount: vi.fn(),
  changeLocalAccount: vi.fn(),
}));

vi.mock("../api/resources", () => ({ getSchoolSettings: vi.fn() }));

import { getSchoolSettings } from "../api/resources";
import {
  changeLocalAccount,
  createLocalAccount,
  listLocalAccounts,
  resetLocalAccount,
} from "../auth/localAccounts";
import SchoolPeopleScreen from "./SchoolPeopleScreen";

function account(overrides: Partial<LocalAccount> = {}): LocalAccount {
  return {
    id: "a2",
    username: "helper1",
    displayName: "Helper One",
    status: "Active",
    authenticatorEnrolled: false,
    isSystemAdministrator: false,
    lockedOut: false,
    lastSignInAtUtc: "2026-11-03T01:00:00Z",
    setupLink: {
      expiresAtUtc: "2026-11-03T00:00:00Z",
      usedAtUtc: "2026-11-02T03:00:00Z",
      usedFromAddress: "10.1.2.3",
    },
    createdAtUtc: "2026-11-02T00:00:00Z",
    ...overrides,
  };
}

const ME = account({
  id: "a1",
  username: "backup-admin",
  displayName: "Backup Admin",
  isSystemAdministrator: true,
});

function rowOf(name: string): HTMLElement {
  return screen.getByText(name, { selector: ".row-title", exact: false }).closest("li")!;
}

function issued(row: LocalAccount) {
  return { account: row, setupToken: "tok-abc", expiresAtUtc: "2026-11-05T00:00:00Z" };
}

beforeEach(() => {
  cleanup();
  _resetToastsForTests();
  _resetReachabilityForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  signIn({
    subject: "local:a1",
    displayName: "Backup Admin",
    roles: ["SystemAdministrator"],
    localAccount: true,
  });
  vi.mocked(listLocalAccounts).mockReset().mockResolvedValue([ME, account()]);
  vi.mocked(getSchoolSettings).mockReset().mockResolvedValue({} as SchoolSettings);
  vi.mocked(createLocalAccount).mockReset();
  vi.mocked(resetLocalAccount).mockReset();
  vi.mocked(changeLocalAccount).mockReset();
});

afterEach(() => {
  cleanup();
  signOut();
});

describe("SchoolPeopleScreen", () => {
  it("lists each account with its last sign-in and where its setup link was used", async () => {
    renderWithQuery(<SchoolPeopleScreen />);

    await screen.findByText("Helper One");
    const row = rowOf("Helper One");
    expect(within(row).getByText(/No authenticator app/)).toBeTruthy();
    expect(within(row).getByText(/Last signed in/)).toBeTruthy();
    expect(within(row).getByText(/Setup link used .* from 10\.1\.2\.3/)).toBeTruthy();
    expect(within(rowOf("Backup Admin")).getByText(/System administrator/)).toBeTruthy();
  });

  it("marks an account still awaiting setup, and one locked out", async () => {
    vi.mocked(listLocalAccounts).mockResolvedValue([
      account({ status: "AwaitingSetup", lastSignInAtUtc: null, setupLink: null }),
      account({ id: "a3", username: "helper2", displayName: "Helper Two", lockedOut: true }),
    ]);
    renderWithQuery(<SchoolPeopleScreen />);

    await screen.findByText("Helper One");
    expect(within(rowOf("Helper One")).getByText("awaiting setup")).toBeTruthy();
    expect(within(rowOf("Helper One")).getByText(/Never signed in/)).toBeTruthy();
    expect(within(rowOf("Helper Two")).getByText("locked out")).toBeTruthy();
  });

  it("offers no action on the administrator's own account", async () => {
    renderWithQuery(<SchoolPeopleScreen />);

    await screen.findByText("Helper One");
    const mine = rowOf("Backup Admin");
    expect(within(mine).getByText(/\(you\)/)).toBeTruthy();
    expect(within(mine).queryByRole("button")).toBeNull();
    expect(within(rowOf("Helper One")).getByRole("button", { name: "Reset Helper One" })).toBeTruthy();
  });

  it("adds a person and shows the setup link and its QR code once", async () => {
    vi.mocked(createLocalAccount).mockResolvedValue(
      issued(account({ id: "a9", username: "jsmith", displayName: "Jo Smith" })),
    );
    renderWithQuery(<SchoolPeopleScreen />);

    await screen.findByText("Helper One");
    fireEvent.change(screen.getByLabelText("Username"), { target: { value: " jsmith " } });
    fireEvent.change(screen.getByLabelText("Display name"), { target: { value: "Jo Smith" } });
    fireEvent.click(screen.getByRole("button", { name: "Add person" }));

    const panel = await screen.findByRole("region", { name: "Setup link for Jo Smith" });
    expect(createLocalAccount).toHaveBeenCalledWith("jsmith", "Jo Smith");
    expect((within(panel).getByLabelText("Setup link") as HTMLInputElement).value).toBe(
      `${location.origin}/#/setup/tok-abc`,
    );
    expect(within(panel).getByRole("img", { name: /QR code of the setup link/ })).toBeTruthy();
    expect(within(panel).getByRole("button", { name: "Copy link" })).toBeTruthy();

    fireEvent.click(within(panel).getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("region", { name: /Setup link for/ })).toBeNull();
  });

  it("shows the server's refusal of a username", async () => {
    vi.mocked(createLocalAccount).mockRejectedValue(
      new ApiError(409, { error: "ACCOUNT_USERNAME_TAKEN", detail: "x" }, "/api/v1/admin/local-accounts"),
    );
    renderWithQuery(<SchoolPeopleScreen />);

    await screen.findByText("Helper One");
    fireEvent.change(screen.getByLabelText("Username"), { target: { value: "helper1" } });
    fireEvent.change(screen.getByLabelText("Display name"), { target: { value: "Again" } });
    fireEvent.click(screen.getByRole("button", { name: "Add person" }));

    expect((await screen.findByRole("alert")).textContent).toMatch(/already taken/);
  });

  it("resets behind an armed confirm and hands over a new link", async () => {
    vi.mocked(resetLocalAccount).mockResolvedValue(issued(account()));
    renderWithQuery(<SchoolPeopleScreen />);

    await screen.findByText("Helper One");
    fireEvent.click(screen.getByRole("button", { name: "Reset Helper One" }));
    expect(resetLocalAccount).not.toHaveBeenCalled();
    expect(screen.getByText(/signs them out everywhere/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Confirm resetting Helper One" }));

    expect(await screen.findByRole("region", { name: "Setup link for Helper One" })).toBeTruthy();
    expect(resetLocalAccount).toHaveBeenCalledWith("a2");
  });

  it("disables behind an armed confirm, and enables and clears a lockout in one tap", async () => {
    vi.mocked(changeLocalAccount).mockResolvedValue(account());
    vi.mocked(listLocalAccounts).mockResolvedValue([
      account(),
      account({ id: "a3", displayName: "Helper Two", status: "Disabled", lockedOut: true }),
    ]);
    renderWithQuery(<SchoolPeopleScreen />);

    await screen.findByText("Helper One");
    fireEvent.click(screen.getByRole("button", { name: "Disable Helper One" }));
    fireEvent.click(screen.getByRole("button", { name: "Confirm disabling Helper One" }));
    await waitFor(() => expect(changeLocalAccount).toHaveBeenCalledWith("a2", "disable"));

    expect(within(rowOf("Helper Two")).queryByRole("button", { name: /^Reset/ })).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: "Enable Helper Two" }));
    await waitFor(() => expect(changeLocalAccount).toHaveBeenCalledWith("a3", "enable"));
    fireEvent.click(screen.getByRole("button", { name: "Clear lockout for Helper Two" }));
    await waitFor(() => expect(changeLocalAccount).toHaveBeenCalledWith("a3", "clear-lockout"));
    expect(getToasts().map((t) => t.message)).toContain("Lockout cleared for Helper Two");
  });

  it("says beside a provider that only administrators may use a local account", async () => {
    vi.mocked(getSchoolSettings).mockResolvedValue({
      providerConfigured: true,
      localAccounts: "SystemAdministrators",
    } as unknown as SchoolSettings);
    renderWithQuery(<SchoolPeopleScreen />);

    expect(await screen.findByText(/only system administrators may use a local account/)).toBeTruthy();
  });

  it("names the missing role to anyone else", async () => {
    signIn({ subject: "s2", displayName: "Rae Recorder", roles: ["Recorder"] });
    renderWithQuery(<SchoolPeopleScreen />);

    expect(await screen.findByText(/needs the system administrator role/)).toBeTruthy();
    expect(listLocalAccounts).not.toHaveBeenCalled();
  });
});
