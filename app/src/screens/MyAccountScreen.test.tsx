import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { PASSWORD_CHECK_BUSY_TEXT } from "../api/errorText";
import { _resetReachabilityForTests } from "../api/reachability";
import type { MyAccount } from "../api/types";
import { signIn, signOut } from "../auth/session";
import { _resetToastsForTests, getToasts } from "../shell/toast";
import { renderWithQuery } from "../test/queryWrapper";

vi.mock("../auth/localAccounts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../auth/localAccounts")>()),
  getMyAccount: vi.fn(),
  changePassword: vi.fn(),
  startAuthenticator: vi.fn(),
  confirmAuthenticator: vi.fn(),
}));

import {
  changePassword,
  confirmAuthenticator,
  getMyAccount,
  startAuthenticator,
} from "../auth/localAccounts";
import MyAccountScreen from "./MyAccountScreen";

const NEW_PASSWORD = "a brand new long passphrase";

function mine(overrides: Partial<MyAccount> = {}): MyAccount {
  return {
    username: "helper1",
    displayName: "Helper One",
    authenticatorEnrolled: false,
    recoveryCodesLeft: 0,
    requireAuthenticator: false,
    ...overrides,
  };
}

function type(label: string, value: string): void {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

beforeEach(() => {
  cleanup();
  _resetToastsForTests();
  _resetReachabilityForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  signIn({ subject: "local:a2", displayName: "Helper One", roles: ["Recorder"], localAccount: true });
  vi.mocked(getMyAccount).mockReset().mockResolvedValue(mine());
  vi.mocked(changePassword).mockReset();
  vi.mocked(startAuthenticator).mockReset();
  vi.mocked(confirmAuthenticator).mockReset();
});

afterEach(() => {
  cleanup();
  signOut();
});

describe("MyAccountScreen", () => {
  it("is for local accounts only: a provider account is told the school manages it", async () => {
    signIn({ subject: "oid-1", displayName: "Jordan Teacher", roles: ["Recorder"] });
    renderWithQuery(<MyAccountScreen />);

    expect(await screen.findByText(/managed by the school/)).toBeTruthy();
    expect(getMyAccount).not.toHaveBeenCalled();
  });

  it("reminds an account without an authenticator, and enrols one", async () => {
    vi.mocked(startAuthenticator).mockResolvedValue({
      secret: "JBSWY3DPEHPK3PXP",
      otpauthUri: "otpauth://totp/School:helper1?secret=JBSWY3DPEHPK3PXP&issuer=School",
    });
    vi.mocked(confirmAuthenticator).mockResolvedValue({ recoveryCodes: ["cccc-3333"] });
    renderWithQuery(<MyAccountScreen />);

    expect(await screen.findByText(/has no authenticator app/)).toBeTruthy();
    const start = screen.getByRole("button", { name: "Set up an authenticator app" });
    expect((start as HTMLButtonElement).disabled).toBe(true);
    type("Your password, to confirm it's you", "my current passphrase");
    fireEvent.click(start);
    await screen.findByLabelText(/6-digit code/);
    fireEvent.change(screen.getByLabelText(/6-digit code/), { target: { value: "123 456" } });
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

    expect((await screen.findByRole("list", { name: "Recovery codes" })).textContent).toContain(
      "cccc-3333",
    );
    expect(startAuthenticator).toHaveBeenCalledWith("my current passphrase");
    expect(confirmAuthenticator).toHaveBeenCalledWith("123456");
    const stored = JSON.stringify([{ ...localStorage }, { ...sessionStorage }]);
    for (const secret of ["my current passphrase", "JBSWY3DPEHPK3PXP", "cccc-3333"])
      expect(stored).not.toContain(secret);
    fireEvent.click(screen.getByRole("button", { name: /I've saved them/ }));
    expect(screen.queryByRole("list", { name: "Recovery codes" })).toBeNull();
  });

  it("refuses to start enrolment on a wrong current password", async () => {
    vi.mocked(startAuthenticator).mockRejectedValue(
      new ApiError(422, { error: "CURRENT_PASSWORD_WRONG", detail: "x" }, "/api/v1/account/authenticator"),
    );
    renderWithQuery(<MyAccountScreen />);

    await screen.findByText(/has no authenticator app/);
    type("Your password, to confirm it's you", "not it");
    fireEvent.click(screen.getByRole("button", { name: "Set up an authenticator app" }));

    expect((await screen.findByRole("alert")).textContent).toMatch(/current password isn't right/);
    expect(screen.queryByRole("img", { name: /QR code/ })).toBeNull();
  });

  it("says the server is busy on a 429 when changing the password", async () => {
    vi.mocked(changePassword).mockRejectedValue(
      new ApiError(429, { error: "TOO_MANY_REQUESTS", detail: "x" }, "/api/v1/account/password"),
    );
    renderWithQuery(<MyAccountScreen />);

    await screen.findByText("Helper One");
    type("Current password", "the old one");
    type("New password", NEW_PASSWORD);
    type("Type it again", NEW_PASSWORD);
    fireEvent.click(screen.getByRole("button", { name: "Change password" }));

    expect((await screen.findByRole("alert")).textContent).toBe(PASSWORD_CHECK_BUSY_TEXT);
  });

  it("shows the recovery codes left once an authenticator is set up", async () => {
    vi.mocked(getMyAccount).mockResolvedValue(mine({ authenticatorEnrolled: true, recoveryCodesLeft: 8 }));
    renderWithQuery(<MyAccountScreen />);

    expect(await screen.findByText(/recovery codes left/)).toBeTruthy();
    expect(screen.getByText("8")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Set up an authenticator app" })).toBeNull();
  });

  it("changes the password, and puts a wrong current password on its field", async () => {
    vi.mocked(changePassword)
      .mockRejectedValueOnce(
        new ApiError(422, { error: "CURRENT_PASSWORD_WRONG", detail: "x" }, "/api/v1/account/password"),
      )
      .mockResolvedValueOnce(undefined);
    renderWithQuery(<MyAccountScreen />);

    await screen.findByText("Helper One");
    type("Current password", "the old one, mistyped");
    type("New password", NEW_PASSWORD);
    type("Type it again", NEW_PASSWORD);
    fireEvent.click(screen.getByRole("button", { name: "Change password" }));
    expect((await screen.findByRole("alert")).textContent).toMatch(/current password isn't right/);

    type("Current password", "the old one, typed right");
    fireEvent.click(screen.getByRole("button", { name: "Change password" }));
    await waitFor(() => expect(getToasts().map((t) => t.message)).toContain(
      "Password changed. Other devices signed in as you are signed out.",
    ));
    expect(changePassword).toHaveBeenLastCalledWith("the old one, typed right", NEW_PASSWORD);
    expect((screen.getByLabelText("Current password") as HTMLInputElement).value).toBe("");
  });
});
