import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import { PASSWORD_CHECK_BUSY_TEXT } from "../api/errorText";
import type { SetupLinkInspection } from "../api/types";
import { dropSetupToken, holdSetupToken } from "../auth/localAccounts";
import { getSession, signOut } from "../auth/session";
import { renderWithQuery } from "../test/queryWrapper";

vi.mock("../auth/localAccounts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../auth/localAccounts")>()),
  inspectSetupLink: vi.fn(),
  completeSetup: vi.fn(),
  signInLocal: vi.fn(),
  startAuthenticator: vi.fn(),
  confirmAuthenticator: vi.fn(),
}));

vi.mock("../api/syncSetup", () => ({ resumeAfterSignIn: vi.fn() }));

import {
  completeSetup,
  confirmAuthenticator,
  inspectSetupLink,
  setupToken,
  signInLocal,
  startAuthenticator,
} from "../auth/localAccounts";
import SetupScreen from "./SetupScreen";

const TOKEN = "Xk3-token";
const PASSWORD = "correct horse battery staple";
const ME = { subject: "local:1", displayName: "Backup Admin", roles: ["SystemAdministrator"] };
const ENROLMENT = {
  secret: "JBSWY3DPEHPK3PXP",
  otpauthUri: "otpauth://totp/School:backup-admin?secret=JBSWY3DPEHPK3PXP&issuer=School",
};

function link(overrides: Partial<SetupLinkInspection> = {}): SetupLinkInspection {
  return {
    kind: "firstAdministrator",
    username: null,
    displayName: null,
    expiresAtUtc: "2026-11-05T09:00:00Z",
    requireAuthenticator: false,
    ...overrides,
  };
}

function type(label: string | RegExp, value: string): void {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

function choosePassword(): void {
  type("Password", PASSWORD);
  type("Type it again", PASSWORD);
}

beforeEach(() => {
  cleanup();
  signOut();
  holdSetupToken(TOKEN);
  vi.mocked(inspectSetupLink).mockReset().mockResolvedValue(link());
  vi.mocked(completeSetup).mockReset();
  vi.mocked(signInLocal).mockReset();
  vi.mocked(startAuthenticator).mockReset();
  vi.mocked(confirmAuthenticator).mockReset();
  location.hash = "#/setup";
});

afterEach(() => {
  cleanup();
  signOut();
  dropSetupToken();
});

describe("SetupScreen", () => {
  it("sets up the first administrator, then offers the authenticator with a skip", async () => {
    vi.mocked(completeSetup).mockResolvedValue({ status: "signedIn", me: ME });
    renderWithQuery(<SetupScreen />);

    await screen.findByText(/first system administrator/);
    expect(inspectSetupLink).toHaveBeenCalledWith(TOKEN);
    type("Username", "backup-admin");
    type("Display name", "Backup Admin");
    choosePassword();
    fireEvent.click(screen.getByRole("button", { name: "Save password" }));

    expect(
      await screen.findByRole("button", { name: "Set up an authenticator app (recommended)" }),
    ).toBeTruthy();
    expect(completeSetup).toHaveBeenCalledWith({
      token: TOKEN,
      username: "backup-admin",
      displayName: "Backup Admin",
      password: PASSWORD,
    });
    expect(getSession()?.subject).toBe("local:1");
    // Used: nothing keeps it after this.
    expect(setupToken()).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Skip for now" }));
    await waitFor(() => expect(location.hash).toBe("#/"));
  });

  it("enrols from the offer and shows the recovery codes once", async () => {
    vi.mocked(inspectSetupLink).mockResolvedValue(
      link({ kind: "account", username: "helper1", displayName: "Helper One" }),
    );
    vi.mocked(completeSetup).mockResolvedValue({ status: "signedIn", me: ME });
    vi.mocked(startAuthenticator).mockResolvedValue(ENROLMENT);
    vi.mocked(confirmAuthenticator).mockResolvedValue({ recoveryCodes: ["aaaa-1111"] });
    renderWithQuery(<SetupScreen />);

    expect(await screen.findByText("Helper One")).toBeTruthy();
    expect(screen.queryByLabelText("Username")).toBeNull();
    choosePassword();
    fireEvent.click(screen.getByRole("button", { name: "Save password" }));
    fireEvent.click(
      await screen.findByRole("button", { name: "Set up an authenticator app (recommended)" }),
    );
    await screen.findByLabelText(/6-digit code/);
    type(/6-digit code/, "123456");
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

    expect((await screen.findByRole("list", { name: "Recovery codes" })).textContent).toContain(
      "aaaa-1111",
    );
    expect(completeSetup).toHaveBeenCalledWith({ token: TOKEN, password: PASSWORD });
    expect(startAuthenticator).toHaveBeenCalledWith(PASSWORD);
    expect(confirmAuthenticator).toHaveBeenCalledWith("123456");
    const stored = JSON.stringify([{ ...localStorage }, { ...sessionStorage }]);
    for (const secret of [TOKEN, PASSWORD, ENROLMENT.secret, "aaaa-1111"])
      expect(stored).not.toContain(secret);
    fireEvent.click(screen.getByRole("button", { name: /I've saved them/ }));
    await waitFor(() => expect(location.hash).toBe("#/"));
  });

  it("enrols with no skip when the school requires an authenticator", async () => {
    vi.mocked(inspectSetupLink).mockResolvedValue(
      link({ kind: "account", username: "helper1", displayName: "Helper One", requireAuthenticator: true }),
    );
    vi.mocked(completeSetup).mockResolvedValue({ status: "enrolmentRequired", ...ENROLMENT });
    vi.mocked(signInLocal).mockResolvedValue({
      status: "signedIn",
      me: ME,
      recoveryCodes: ["bbbb-2222"],
    });
    renderWithQuery(<SetupScreen />);

    await screen.findByText("Helper One");
    choosePassword();
    fireEvent.click(screen.getByRole("button", { name: "Save password" }));

    expect(await screen.findByText(/requires an authenticator app/)).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Skip for now" })).toBeNull();
    type(/6-digit code/, "654321");
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

    await screen.findByRole("list", { name: "Recovery codes" });
    expect(signInLocal).toHaveBeenCalledWith({
      username: "helper1",
      password: PASSWORD,
      enrolmentCode: "654321",
    });
  });

  it("lands a password refusal on the password field", async () => {
    vi.mocked(completeSetup).mockRejectedValue(
      new ApiError(422, { error: "PASSWORD_TOO_COMMON", detail: "x" }, "/api/v1/auth/setup/complete"),
    );
    renderWithQuery(<SetupScreen />);

    await screen.findByText(/first system administrator/);
    type("Username", "backup-admin");
    type("Display name", "Backup Admin");
    choosePassword();
    fireEvent.click(screen.getByRole("button", { name: "Save password" }));

    expect((await screen.findByRole("alert")).textContent).toMatch(/common or leaked/);
    expect(screen.getByLabelText("Password").getAttribute("aria-invalid")).toBe("true");
    expect(setupToken()).toBe(TOKEN);
  });

  it("will not take a password until it is long enough and typed twice", async () => {
    renderWithQuery(<SetupScreen />);
    await screen.findByText(/first system administrator/);
    type("Username", "backup-admin");
    type("Display name", "Backup Admin");
    const save = () => screen.getByRole("button", { name: "Save password" }) as HTMLButtonElement;

    type("Password", "too short");
    expect(save().disabled).toBe(true);
    type("Password", PASSWORD);
    type("Type it again", `${PASSWORD}!`);
    expect(save().disabled).toBe(true);
    expect(screen.getByText(/don't match/)).toBeTruthy();
    type("Type it again", PASSWORD);
    expect(save().disabled).toBe(false);
  });

  it("says a used or expired link is no good", async () => {
    vi.mocked(inspectSetupLink).mockRejectedValue(
      new ApiError(410, { error: "SETUP_LINK_INVALID", detail: "x" }, "/api/v1/auth/setup/inspect"),
    );
    renderWithQuery(<SetupScreen />);

    expect((await screen.findByRole("alert")).textContent).toMatch(/expired or has already been used/);
    expect(screen.queryByLabelText("Password")).toBeNull();
  });

  it("offers a retry when the link could not be checked offline", async () => {
    vi.mocked(inspectSetupLink)
      .mockRejectedValueOnce(new TypeError("Failed to fetch"))
      .mockResolvedValueOnce(link());
    renderWithQuery(<SetupScreen />);

    expect((await screen.findByRole("alert")).textContent).toMatch(/needs the server/);
    fireEvent.click(screen.getByRole("button", { name: "Retry" }));

    expect(await screen.findByText(/first system administrator/)).toBeTruthy();
    expect(inspectSetupLink).toHaveBeenCalledTimes(2);
  });

  it("offers no retry for a link the server refused", async () => {
    vi.mocked(inspectSetupLink).mockRejectedValue(
      new ApiError(410, { error: "SETUP_LINK_INVALID", detail: "x" }, "/api/v1/auth/setup/inspect"),
    );
    renderWithQuery(<SetupScreen />);

    await screen.findByRole("alert");
    expect(screen.queryByRole("button", { name: "Retry" })).toBeNull();
  });

  it("forgets the token when the screen goes away", async () => {
    const { unmount } = renderWithQuery(<SetupScreen />);
    await screen.findByText(/first system administrator/);

    unmount();

    expect(setupToken()).toBeNull();
  });

  it("says the server is busy on a 429 rather than refusing the password", async () => {
    vi.mocked(completeSetup).mockRejectedValue(
      new ApiError(429, { error: "TOO_MANY_REQUESTS", detail: "x" }, "/api/v1/auth/setup/complete"),
    );
    renderWithQuery(<SetupScreen />);
    await screen.findByText(/first system administrator/);
    type("Username", "backup-admin");
    type("Display name", "Backup Admin");
    choosePassword();
    fireEvent.click(screen.getByRole("button", { name: "Save password" }));

    expect((await screen.findByRole("alert")).textContent).toBe(PASSWORD_CHECK_BUSY_TEXT);
  });

  it("asks for the link again after a reload, which drops the token", async () => {
    dropSetupToken();
    renderWithQuery(<SetupScreen />);

    expect((await screen.findByRole("alert")).textContent).toMatch(/Open your setup link again/);
    expect(inspectSetupLink).not.toHaveBeenCalled();
  });
});
