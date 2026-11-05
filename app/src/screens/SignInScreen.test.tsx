import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "../api/client";
import {
  PASSWORD_CHECK_BUSY_TEXT,
  SIGN_IN_FAILED_TEXT,
  SIGN_IN_NEEDS_SERVER_TEXT,
} from "../api/errorText";
import { _resetReachabilityForTests } from "../api/reachability";
import { getSession, signIn, signOut } from "../auth/session";
import { renderWithQuery } from "../test/queryWrapper";

vi.mock("../auth/localAccounts", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../auth/localAccounts")>()),
  getAuthMethods: vi.fn(),
  identify: vi.fn(),
  signInLocal: vi.fn(),
}));

vi.mock("../api/syncSetup", () => ({ resumeAfterSignIn: vi.fn() }));

import { resumeAfterSignIn } from "../api/syncSetup";
import { getAuthMethods, identify, signInLocal } from "../auth/localAccounts";
import SignInScreen, { returnPathOf, returnUrlOf } from "./SignInScreen";

const ME = {
  subject: "local:7",
  displayName: "Backup Admin",
  roles: ["SystemAdministrator"],
  localAccount: true,
};

function refusal(status: number, error: string, detail = "server wording"): ApiError {
  return new ApiError(status, { error, detail }, "/api/v1/auth/local");
}

/** Everything a page could leave behind in web storage, as one searchable string. */
function stored(): string {
  return JSON.stringify([{ ...localStorage }, { ...sessionStorage }]);
}

function type(label: string | RegExp, value: string): void {
  fireEvent.change(screen.getByLabelText(label), { target: { value } });
}

async function toPassword(): Promise<void> {
  type("Username", "backup-admin");
  fireEvent.click(screen.getByRole("button", { name: "Continue" }));
  await screen.findByLabelText("Password");
}

beforeEach(() => {
  cleanup();
  signOut();
  _resetReachabilityForTests();
  Object.defineProperty(navigator, "onLine", { value: true, configurable: true });
  location.hash = "#/login?returnUrl=%2F%23%2Fcarnivals%2Fc-1";
  vi.mocked(getAuthMethods).mockReset().mockResolvedValue({ provider: null });
  vi.mocked(identify).mockReset().mockResolvedValue({ method: "password" });
  vi.mocked(signInLocal).mockReset();
  vi.mocked(resumeAfterSignIn).mockClear();
});

afterEach(() => {
  cleanup();
  signOut();
  location.hash = "";
});

describe("the return address", () => {
  it("takes same-origin paths only, so the box is never an open redirect", () => {
    expect(returnUrlOf("returnUrl=%2F%23%2Foutbox")).toBe("/#/outbox");
    expect(returnUrlOf("returnUrl=%2F%2Fevil.example")).toBeNull();
    expect(returnUrlOf("returnUrl=https%3A%2F%2Fevil.example")).toBeNull();
    expect(returnUrlOf("returnUrl=%2F%5Cevil.example")).toBeNull();
    expect(returnUrlOf("")).toBeNull();
  });

  it("lands on the hash route inside it, and never back on sign-in or setup", () => {
    expect(returnPathOf("/#/carnivals/c-1/events/e-1")).toBe("/carnivals/c-1/events/e-1");
    expect(returnPathOf("/#/login")).toBe("/");
    expect(returnPathOf("/#/setup")).toBe("/");
    expect(returnPathOf("/")).toBe("/");
    expect(returnPathOf(null)).toBe("/");
  });
});

describe("SignInScreen", () => {
  it("asks for a username first, then a password for a local account", async () => {
    renderWithQuery(<SignInScreen go={vi.fn()} />);

    expect(screen.queryByLabelText("Password")).toBeNull();
    await toPassword();

    expect(identify).toHaveBeenCalledWith("backup-admin", "/#/carnivals/c-1");
    expect(screen.getByText("backup-admin")).toBeTruthy();
    expect(screen.getByLabelText("Password").getAttribute("autocomplete")).toBe("current-password");
  });

  it("sends a provider's name to the provider with a full-page navigation", async () => {
    vi.mocked(getAuthMethods).mockResolvedValue({ provider: "Entra" });
    vi.mocked(identify).mockResolvedValue({
      method: "provider",
      loginUrl: "/api/v1/auth/login?returnUrl=%2F&loginHint=jo%40example.org",
    });
    const go = vi.fn();
    renderWithQuery(<SignInScreen go={go} />);

    await screen.findByLabelText("Username or school email");
    type("Username or school email", "jo@example.org");
    fireEvent.click(screen.getByRole("button", { name: "Continue" }));

    await waitFor(() =>
      expect(go).toHaveBeenCalledWith("/api/v1/auth/login?returnUrl=%2F&loginHint=jo%40example.org"),
    );
    expect(screen.queryByLabelText("Password")).toBeNull();
  });

  it("signs in, adopts the session, resumes the queue and goes back where it was sent from", async () => {
    vi.mocked(signInLocal).mockResolvedValue({ status: "signedIn", me: ME });
    renderWithQuery(<SignInScreen go={vi.fn()} />);
    await toPassword();

    type("Password", "a long enough passphrase");
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(location.hash).toBe("#/carnivals/c-1"));
    expect(signInLocal).toHaveBeenCalledWith({
      username: "backup-admin",
      password: "a long enough passphrase",
    });
    expect(getSession()).toMatchObject({ subject: "local:7", localAccount: true });
    expect(resumeAfterSignIn).toHaveBeenCalledTimes(1);
  });

  it("gives one answer for every refusal and asks for the password again", async () => {
    renderWithQuery(<SignInScreen go={vi.fn()} />);
    await toPassword();

    for (const detail of ["Wrong password.", "Locked out.", "Disabled."]) {
      vi.mocked(signInLocal).mockRejectedValueOnce(refusal(401, "SIGN_IN_FAILED", detail));
      type("Password", "a long enough passphrase");
      fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
      expect((await screen.findByRole("alert")).textContent).toBe(SIGN_IN_FAILED_TEXT);
      expect((screen.getByLabelText("Password") as HTMLInputElement).value).toBe("");
    }
    expect(getSession()).toBeNull();
  });

  it("asks for the authenticator code, keeps the step on a wrong one, and offers a recovery code", async () => {
    vi.mocked(signInLocal)
      .mockResolvedValueOnce({ status: "codeRequired" })
      .mockRejectedValueOnce(refusal(401, "CODE_INVALID"))
      .mockResolvedValueOnce({ status: "signedIn", me: ME });
    renderWithQuery(<SignInScreen go={vi.fn()} />);
    await toPassword();
    type("Password", "a long enough passphrase");
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    const field = await screen.findByLabelText(/6-digit code/);
    expect(field.getAttribute("autocomplete")).toBe("one-time-code");
    type(/6-digit code/, "123456");
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect((await screen.findByRole("alert")).textContent).toMatch(/code didn't work/);
    expect(screen.getByLabelText(/6-digit code/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Use a recovery code instead" }));
    type("One of your recovery codes", " abcd-efgh ");
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(getSession()?.subject).toBe("local:7"));
    expect(vi.mocked(signInLocal).mock.calls[1]![0]).toMatchObject({ code: "123456" });
    expect(vi.mocked(signInLocal).mock.calls[2]![0]).toEqual({
      username: "backup-admin",
      password: "a long enough passphrase",
      recoveryCode: "abcd-efgh",
    });
  });

  it("enrols an authenticator when the school requires one, then shows the recovery codes once", async () => {
    vi.mocked(signInLocal)
      .mockResolvedValueOnce({
        status: "enrolmentRequired",
        secret: "JBSWY3DPEHPK3PXP",
        otpauthUri: "otpauth://totp/School:backup-admin?secret=JBSWY3DPEHPK3PXP&issuer=School",
      })
      .mockResolvedValueOnce({ status: "signedIn", me: ME, recoveryCodes: ["aaaa-1111", "bbbb-2222"] });
    renderWithQuery(<SignInScreen go={vi.fn()} />);
    await toPassword();
    type("Password", "a long enough passphrase");
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect(await screen.findByText(/requires an authenticator app/)).toBeTruthy();
    expect(screen.getByRole("img", { name: /QR code/ })).toBeTruthy();
    expect(screen.getByText("JBSW Y3DP EHPK 3PXP")).toBeTruthy();
    // Required: there is no way round it.
    expect(screen.queryByRole("button", { name: "Skip for now" })).toBeNull();
    type(/6-digit code the app now shows/, "654321");
    fireEvent.click(screen.getByRole("button", { name: "Confirm" }));

    const list = await screen.findByRole("list", { name: "Recovery codes" });
    expect(list.textContent).toContain("aaaa-1111");
    for (const secret of ["a long enough passphrase", "JBSWY3DPEHPK3PXP", "aaaa-1111", "bbbb-2222"])
      expect(stored()).not.toContain(secret);
    expect(vi.mocked(signInLocal).mock.calls[1]![0]).toMatchObject({ enrolmentCode: "654321" });
    expect(location.hash).toBe("#/login?returnUrl=%2F%23%2Fcarnivals%2Fc-1");

    fireEvent.click(screen.getByRole("button", { name: /I've saved them/ }));
    await waitFor(() => expect(location.hash).toBe("#/carnivals/c-1"));
  });

  it("still offers the box to a device whose session has ended, and hands it over", async () => {
    // A reset or lapsed session still reads as signed in here, with its queue paused on 401.
    signIn({ subject: "local:old", displayName: "Helper One", roles: ["Recorder"] });
    vi.mocked(signInLocal).mockResolvedValue({ status: "signedIn", me: ME });
    renderWithQuery(<SignInScreen go={vi.fn()} />);

    expect(screen.getByRole("status").textContent).toMatch(/Helper One/);
    expect(screen.queryByRole("link", { name: "Sign out" })).toBeNull();
    await toPassword();
    type("Password", "a long enough passphrase");
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    await waitFor(() => expect(getSession()?.subject).toBe("local:7"));
    expect(resumeAfterSignIn).toHaveBeenCalledTimes(1);
  });

  it("goes back to the password when the account is refused at the code step", async () => {
    vi.mocked(signInLocal)
      .mockResolvedValueOnce({ status: "codeRequired" })
      .mockRejectedValueOnce(refusal(401, "SIGN_IN_FAILED"));
    renderWithQuery(<SignInScreen go={vi.fn()} />);
    await toPassword();
    type("Password", "a long enough passphrase");
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));
    await screen.findByLabelText(/6-digit code/);
    type(/6-digit code/, "123456");
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect((await screen.findByRole("alert")).textContent).toBe(SIGN_IN_FAILED_TEXT);
    expect((screen.getByLabelText("Password") as HTMLInputElement).value).toBe("");
    expect(screen.queryByLabelText(/6-digit code/)).toBeNull();
  });

  it("says the server is busy, not that the password was wrong, on a 429", async () => {
    vi.mocked(signInLocal).mockRejectedValue(refusal(429, "TOO_MANY_REQUESTS"));
    renderWithQuery(<SignInScreen go={vi.fn()} />);
    await toPassword();
    type("Password", "a long enough passphrase");
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect((await screen.findByRole("alert")).textContent).toBe(PASSWORD_CHECK_BUSY_TEXT);
    expect((screen.getByLabelText("Password") as HTMLInputElement).value).not.toBe("");
  });

  it("puts the dev loop's API base in front of the provider path", async () => {
    vi.stubEnv("VITE_API_BASE", "http://localhost:5080");
    try {
      vi.mocked(identify).mockResolvedValue({
        method: "provider",
        loginUrl: "/api/v1/auth/login?returnUrl=%2F&loginHint=jo%40example.org",
      });
      const go = vi.fn();
      renderWithQuery(<SignInScreen go={go} />);
      type("Username", "jo@example.org");
      fireEvent.click(screen.getByRole("button", { name: "Continue" }));

      await waitFor(() =>
        expect(go).toHaveBeenCalledWith(
          "http://localhost:5080/api/v1/auth/login?returnUrl=%2F&loginHint=jo%40example.org",
        ),
      );
    } finally {
      vi.unstubAllEnvs();
    }
  });

  it("follows only the API's own provider sign-in", async () => {
    vi.mocked(identify).mockResolvedValue({
      method: "provider",
      loginUrl: "https://evil.example/api/v1/auth/login?returnUrl=%2F",
    });
    const go = vi.fn();
    renderWithQuery(<SignInScreen go={go} />);
    type("Username", "jo@example.org");
    fireEvent.click(screen.getByRole("button", { name: "Continue" }));

    expect((await screen.findByRole("alert")).textContent).toMatch(/doesn't recognise/);
    expect(go).not.toHaveBeenCalled();
  });

  it("says sign-in needs the server when nothing answers", async () => {
    vi.mocked(identify).mockRejectedValue(new TypeError("Failed to fetch"));
    renderWithQuery(<SignInScreen go={vi.fn()} />);

    type("Username", "backup-admin");
    fireEvent.click(screen.getByRole("button", { name: "Continue" }));

    expect((await screen.findByRole("alert")).textContent).toBe(SIGN_IN_NEEDS_SERVER_TEXT);
  });

  it("says so up front on a device that already knows it is offline", async () => {
    Object.defineProperty(navigator, "onLine", { value: false, configurable: true });
    renderWithQuery(<SignInScreen go={vi.fn()} />);

    expect((await screen.findByRole("status")).textContent).toBe(SIGN_IN_NEEDS_SERVER_TEXT);
  });

  it("starts again when a local account may not sign in beside the provider", async () => {
    vi.mocked(signInLocal).mockRejectedValue(refusal(403, "LOCAL_SIGN_IN_NOT_ALLOWED"));
    renderWithQuery(<SignInScreen go={vi.fn()} />);
    await toPassword();
    type("Password", "a long enough passphrase");
    fireEvent.click(screen.getByRole("button", { name: "Sign in" }));

    expect((await screen.findByRole("alert")).textContent).toMatch(/only system administrators/);
    expect(screen.getByLabelText("Username")).toBeTruthy();
  });
});
