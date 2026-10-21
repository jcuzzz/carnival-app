import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  bootstrapEntraSession,
  fetchMe,
  loginHref,
  logoutHref,
  previousSessionHasEnded,
} from "./entra";
import { getSession, signIn, signOut } from "./session";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  signOut();
});

describe("loginHref / logoutHref", () => {
  it("point at the server's cookie/BFF endpoints, same-origin by default", () => {
    expect(loginHref()).toBe("/api/v1/auth/login?returnUrl=%2F");
    expect(loginHref("/carnivals/1/standings")).toBe(
      "/api/v1/auth/login?returnUrl=%2Fcarnivals%2F1%2Fstandings",
    );
    expect(logoutHref()).toBe("/api/v1/auth/logout");
  });

  it("honour VITE_API_BASE for the dev loop's cross-origin API", () => {
    vi.stubEnv("VITE_API_BASE", "http://localhost:5080");
    expect(loginHref()).toBe("http://localhost:5080/api/v1/auth/login?returnUrl=%2F");
  });
});

describe("fetchMe", () => {
  it("maps a 200 /me body to a session", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        jsonResponse(200, {
          subject: "oid-123",
          displayName: "Jordan Teacher",
          roles: ["SystemAdministrator"],
        }),
      ),
    );

    const session = await fetchMe();
    expect(session).toEqual({
      subject: "oid-123",
      displayName: "Jordan Teacher",
      roles: ["SystemAdministrator"],
    });
  });

  it("returns null on 401 (not signed in) rather than throwing", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 401 })));
    expect(await fetchMe()).toBeNull();
    // fetchMe must not itself establish a session.
    expect(getSession()).toBeNull();
  });

  it("propagates a non-401 failure (e.g. offline) so callers keep their state", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("Failed to fetch")));
    await expect(fetchMe()).rejects.toBeInstanceOf(TypeError);
  });
});

/**
 * Adopting the cookie is the REAL build's only sign-in, so it owes the outbox
 * the same resume the dev picker gives it — otherwise ADR-0029's paused banner
 * sends the marshal to a sign-in that cannot clear it.
 */
describe("bootstrapEntraSession", () => {
  beforeEach(() => {
    // The real-auth build: not the Vite dev server, not a demo build.
    vi.stubEnv("DEV", false);
  });

  it("adopts the cookie session and resumes the queue behind it", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        jsonResponse(200, { subject: "oid-9", displayName: "Jordan Teacher", roles: ["Recorder"] }),
      ),
    );
    const resume = vi.fn();

    await bootstrapEntraSession(resume);

    expect(getSession()?.subject).toBe("oid-9");
    expect(resume).toHaveBeenCalledTimes(1);
  });

  it("resumes nothing when there is no session to adopt", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 401 })));
    const resume = vi.fn();

    await bootstrapEntraSession(resume);

    expect(getSession()).toBeNull();
    expect(resume).not.toHaveBeenCalled();
  });

  it("leaves an offline start alone — no sign-out, no resume", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("Failed to fetch")));
    const resume = vi.fn();

    await bootstrapEntraSession(resume);

    expect(resume).not.toHaveBeenCalled();
  });
});

/** ADR-0061 §5: the demo build's session is its cookie, and a refused one is "expired". */
describe("bootstrapEntraSession in a demo build", () => {
  beforeEach(() => {
    vi.stubEnv("DEV", false);
    vi.stubEnv("VITE_DEMO_MODE", "true");
  });

  it("adopts the demo session cookie", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(
        jsonResponse(200, { subject: "demo-1", displayName: "Demo visitor", roles: ["SystemAdministrator"] }),
      ),
    );

    await bootstrapEntraSession(vi.fn());

    expect(getSession()?.subject).toBe("demo-1");
    expect(previousSessionHasEnded()).toBe(false);
  });

  it("reports a held session the server no longer honours as ended", async () => {
    signIn({ subject: "demo-1", displayName: "Demo visitor", roles: ["SystemAdministrator"] });
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 401 })));

    await bootstrapEntraSession(vi.fn());

    expect(getSession()).toBeNull();
    expect(previousSessionHasEnded()).toBe(true);
  });

  it("does not call a first visit an expiry", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 401 })));

    await bootstrapEntraSession(vi.fn());

    expect(previousSessionHasEnded()).toBe(false);
  });
});
