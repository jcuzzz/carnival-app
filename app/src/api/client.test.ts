import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api, ApiError, apiBase, apiFile, devAuthHeaders, isDemoBuild } from "./client";
import { signIn, signOut } from "../auth/session";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

beforeEach(() => {
  signOut();
});

afterEach(() => {
  vi.unstubAllGlobals();
  signOut();
});

describe("apiBase", () => {
  it("defaults to '' (same-origin relative /api) when VITE_API_BASE is unset", () => {
    // The test env does not define VITE_API_BASE — like a production build,
    // where the single Caddy serves app and API from one origin.
    expect(apiBase()).toBe("");
  });

  it("uses VITE_API_BASE and strips trailing slashes when set (dev loop)", () => {
    vi.stubEnv("VITE_API_BASE", "http://localhost:5080/");
    try {
      expect(apiBase()).toBe("http://localhost:5080");
    } finally {
      vi.unstubAllEnvs();
    }
  });
});

describe("devAuthHeaders", () => {
  it("is empty with no session", () => {
    expect(devAuthHeaders()).toEqual({});
  });

  it("carries the picked official's subject and roles in development", () => {
    signIn({
      subject: "dev-official-1",
      displayName: "Olivia Official",
      roles: ["Recorder", "CarnivalAdministrator"],
    });
    expect(devAuthHeaders()).toEqual({
      "X-Dev-Subject": "dev-official-1",
      "X-Dev-Roles": "Recorder,CarnivalAdministrator",
    });
  });
});

describe("devAuthHeaders in a demo build", () => {
  it("sends no identity headers: the demo session cookie is the identity (ADR-0061 §2)", () => {
    vi.stubEnv("DEV", false);
    vi.stubEnv("VITE_DEMO_MODE", "true");
    try {
      signIn({ subject: "demo-1", displayName: "Demo visitor", roles: ["SystemAdministrator"] });
      expect(devAuthHeaders()).toEqual({});
    } finally {
      vi.unstubAllEnvs();
    }
  });
});

describe("isDemoBuild", () => {
  it("is false by default (normal builds never show the demo banner)", () => {
    expect(isDemoBuild()).toBe(false);
  });

  it("is true only when the build-time flag VITE_DEMO_MODE is exactly 'true'", () => {
    vi.stubEnv("VITE_DEMO_MODE", "true");
    try {
      expect(isDemoBuild()).toBe(true);
    } finally {
      vi.unstubAllEnvs();
    }
    vi.stubEnv("VITE_DEMO_MODE", "1");
    try {
      expect(isDemoBuild()).toBe(false);
    } finally {
      vi.unstubAllEnvs();
    }
  });
});

describe("api", () => {
  it("GETs /api/v1 paths with dev headers and parses JSON", async () => {
    signIn({ subject: "s1", displayName: "S", roles: ["CarnivalAdministrator"] });
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(200, [{ id: "c1" }]));
    vi.stubGlobal("fetch", fetchMock);

    const result = await api<{ id: string }[]>("/carnivals");
    expect(result).toEqual([{ id: "c1" }]);

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe("/api/v1/carnivals");
    expect(init.method).toBe("GET");
    expect((init.headers as Record<string, string>)["X-Dev-Subject"]).toBe("s1");
  });

  it("serialises JSON bodies with Content-Type", async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse(201, { id: "x" }));
    vi.stubGlobal("fetch", fetchMock);

    await api("/carnivals", { method: "POST", body: { name: "Swim" } });
    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect((init.headers as Record<string, string>)["Content-Type"]).toBe("application/json");
    expect(init.body).toBe(JSON.stringify({ name: "Swim" }));
  });

  it("throws ApiError with the server's message on non-2xx", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn().mockResolvedValue(jsonResponse(409, { error: "Illegal carnival status transition" })),
    );
    const failure = await api("/carnivals/x/status", { method: "POST", body: {} }).catch((e) => e);
    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).status).toBe(409);
    expect((failure as ApiError).message).toContain("Illegal carnival status transition");
  });

  it("maps 401 to a sign-in hint when the body has no message", async () => {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(new Response(null, { status: 401 })));
    const failure = await api("/carnivals").catch((e) => e);
    expect((failure as ApiError).status).toBe(401);
    expect((failure as ApiError).message).toMatch(/sign(ed)? in/i);
  });
});

// Checked by tsc, not vitest: a path must be a route of the generated OpenAPI document.
describe("api route typing", () => {
  it("accepts document routes and refuses anything else at compile time", () => {
    const id: string = "c1";
    const accepted = [
      () => api("/carnivals"),
      () => api(`/carnivals/${id}/events/${id}/entries`),
      () => api(`/event-definitions?includeInactive=${true}`),
      () => api(`/carnivals/${id}/reports/${"results" as "results" | "standings"}.csv`),
      () => apiFile(`/carnivals/${id}/reports/district.csv`, "district.csv"),
    ];
    const refused = [
      // @ts-expect-error trailing slash
      () => api("/carnivals/"),
      // @ts-expect-error trailing slash after a parameter
      () => api(`/carnivals/${id}/`),
      // @ts-expect-error empty parameter segment
      () => api("/carnivals//events"),
      // @ts-expect-error no such route
      () => api(`/carnivals/${id}/bogus`),
      // @ts-expect-error a parameter where the route has a literal segment
      () => api(`/carnivals/${id}/${id}`),
      // @ts-expect-error an untyped string
      () => api(id),
      // @ts-expect-error explicit T needs a parameterless route
      () => api<string>(`/carnivals/${id}`),
    ];
    expect(accepted.length + refused.length).toBe(12);
  });
});

// ADR-0011 (K1): `error` is a machine code and `detail` the human sentence.
// The code is the only thing callers may branch on; the sentence is the only
// thing they may show.
describe("ApiError code", () => {
  async function failWith(status: number, body: unknown): Promise<ApiError> {
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(jsonResponse(status, body)));
    return (await api("/carnivals/x").catch((e) => e)) as ApiError;
  }

  it("carries the machine code and shows the detail sentence", async () => {
    const failure = await failWith(409, {
      error: "EVENT_NOT_OPEN",
      detail: "The event is not open for result capture.",
    });
    expect(failure.code).toBe("EVENT_NOT_OPEN");
    expect(failure.message).toBe("The event is not open for result capture.");
  });

  it("keeps the flat context a code carries (currentRevision)", async () => {
    const failure = await failWith(409, {
      error: "CONCURRENCY_CONFLICT",
      detail: "Re-read and retry.",
      currentRevision: 4,
    });
    expect(failure.code).toBe("CONCURRENCY_CONFLICT");
    expect(failure.body?.currentRevision).toBe(4);
  });

  // ADR-0011 transition note: a pre-contract body put a whole sentence in
  // `error`. That is a message, not a code — branching on it would be branching
  // on prose, so there is deliberately no code to branch on.
  it("has no code for a legacy prose body, but still shows the sentence", async () => {
    const failure = await failWith(409, { error: "Illegal carnival status transition" });
    expect(failure.code).toBeUndefined();
    expect(failure.message).toBe("Illegal carnival status transition");
  });

  it("has no code for an RFC7807 body or an empty one", async () => {
    expect((await failWith(422, { title: "Unprocessable" })).code).toBeUndefined();
    expect((await failWith(500, "")).code).toBeUndefined();
  });
});
