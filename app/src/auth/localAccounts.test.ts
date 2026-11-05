import { afterEach, describe, expect, it, vi } from "vitest";
import { changePassword, changeLocalAccount, signInAnswerOf, signInLocal } from "./localAccounts";

function json(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

afterEach(() => vi.unstubAllGlobals());

describe("signInAnswerOf", () => {
  const me = { subject: "local:1", displayName: "Backup Admin", roles: [], localAccount: true };

  it("narrows the one wire object by its status", () => {
    expect(signInAnswerOf({ status: "signedIn", me })).toEqual({ status: "signedIn", me });
    expect(signInAnswerOf({ status: "codeRequired" })).toEqual({ status: "codeRequired" });
    expect(
      signInAnswerOf({ status: "enrolmentRequired", secret: "S", otpauthUri: "otpauth://x" }),
    ).toEqual({ status: "enrolmentRequired", secret: "S", otpauthUri: "otpauth://x" });
  });

  it("refuses a status whose fields are missing rather than showing an empty step", () => {
    expect(() => signInAnswerOf({ status: "signedIn" })).toThrow(/incomplete/);
    expect(() => signInAnswerOf({ status: "enrolmentRequired", secret: "S" })).toThrow();
  });
});

describe("the local-account calls", () => {
  it("post the sign-in step and hand back the narrowed answer", async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(200, { status: "codeRequired" }));
    vi.stubGlobal("fetch", fetchMock);

    expect(await signInLocal({ username: "a", password: "b" })).toEqual({ status: "codeRequired" });
    const [url, init] = fetchMock.mock.calls[0]!;
    expect(url).toBe("/api/v1/auth/local");
    expect(init.method).toBe("POST");
    expect(JSON.parse(init.body as string)).toEqual({ username: "a", password: "b" });
  });

  it("take a bodiless 204 for a password change", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 204 })));
    await expect(changePassword("old", "new")).resolves.toBeUndefined();
  });

  it("address each account change by its own route", async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json(200, {})));
    vi.stubGlobal("fetch", fetchMock);

    await changeLocalAccount("a 1", "disable");
    await changeLocalAccount("a 1", "clear-lockout");

    expect(fetchMock.mock.calls.map(([url]) => url)).toEqual([
      "/api/v1/admin/local-accounts/a%201/disable",
      "/api/v1/admin/local-accounts/a%201/clear-lockout",
    ]);
  });
});
