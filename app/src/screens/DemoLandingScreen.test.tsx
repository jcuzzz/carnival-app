// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import DemoLandingScreen from "./DemoLandingScreen";
import { getSession, signOut } from "../auth/session";

vi.mock("../api/syncSetup", () => ({ resumeAfterSignIn: vi.fn().mockResolvedValue(undefined) }));

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  signOut();
});

/** ADR-0061 §5: the demo's front door. */
describe("DemoLandingScreen", () => {
  it("says what the demo is, and says nothing about expiry on a first visit", () => {
    render(<DemoLandingScreen expired={false} />);

    expect(screen.getByRole("heading", { level: 1 })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Start demo" })).toBeTruthy();
    expect(screen.queryByText(/previous demo has ended/)).toBeNull();
  });

  it("tells a returning visitor their previous demo ended", () => {
    render(<DemoLandingScreen expired />);

    expect(screen.getByRole("status").textContent).toMatch(/previous demo has ended/);
  });

  it("starts a sandbox and adopts the session the cookie now carries", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(json(201, { expiresAtUtc: "2026-10-10T02:00:00Z" }))
      .mockResolvedValueOnce(
        json(200, { subject: "demo-1", displayName: "Demo visitor", roles: ["SystemAdministrator"] }),
      );
    vi.stubGlobal("fetch", fetchMock);
    render(<DemoLandingScreen expired={false} />);

    fireEvent.click(screen.getByRole("button", { name: "Start demo" }));

    await waitFor(() => expect(getSession()?.subject).toBe("demo-1"));
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/demo/sessions");
    expect(init.method).toBe("POST");
    expect(new Headers(init.headers).has("X-Dev-Subject")).toBe(false);
  });

  it("shows the server's reason when the demo is full, and lets the visitor retry", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        json(503, { error: "DEMO_AT_CAPACITY", detail: "The demo is full right now. Try again in a few minutes." }),
      ),
    );
    render(<DemoLandingScreen expired={false} />);

    fireEvent.click(screen.getByRole("button", { name: "Start demo" }));

    await waitFor(() =>
      expect(screen.getByRole("alert").textContent).toBe("The demo is full right now. Try again in a few minutes."),
    );
    expect((screen.getByRole("button", { name: "Start demo" }) as HTMLButtonElement).disabled).toBe(false);
    expect(getSession()).toBeNull();
  });
});
