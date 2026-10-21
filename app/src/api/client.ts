import { getSession } from "../auth/session";
import { errorCodeOf } from "./errorCodes";
import { reportReachable, reportUnreachable } from "./reachability";
import type { paths } from "./generated";
import type { ApiErrorBody } from "./types";

export function apiBase(): string {
  const configured = import.meta.env.VITE_API_BASE as string | undefined;
  // "" = same-origin relative URLs: the single reverse proxy routes /api/* to the API.
  return (configured ?? "").trim().replace(/\/+$/, "");
}

/** The public demo build (ADR-0061): banner, landing page, a demo session cookie. */
export function isDemoBuild(): boolean {
  return import.meta.env.VITE_DEMO_MODE === "true";
}

/** Null rather than invented when the build supplied no `VITE_APP_VERSION`. */
export function appVersion(): string | null {
  const configured = (import.meta.env.VITE_APP_VERSION as string | undefined)?.trim();
  return configured ? configured : null;
}

/**
 * The Vite dev loop only; the API honours them only when dev auth is enabled server-side. A demo
 * build's identity is its session cookie, and a demo host ignores these headers (ADR-0061 §2).
 */
export function devAuthHeaders(): Record<string, string> {
  if (!import.meta.env.DEV) return {};
  const session = getSession();
  if (!session) return {};
  return {
    "X-Dev-Subject": session.subject,
    "X-Dev-Roles": session.roles.join(","),
  };
}

export class ApiError extends Error {
  readonly status: number;
  readonly body: ApiErrorBody | undefined;
  /** The only part callers may branch on; undefined for a response predating the contract. */
  readonly code: string | undefined;

  constructor(status: number, body: ApiErrorBody | undefined, url: string) {
    super(ApiError.describe(status, body, url));
    this.name = "ApiError";
    this.status = status;
    this.body = body;
    this.code = errorCodeOf(body);
  }

  private static describe(status: number, body: ApiErrorBody | undefined, url: string): string {
    // `error` is a machine code, never prose; it is the last resort only for a pre-contract body
    // that held the whole sentence there.
    const serverMessage = body?.detail ?? body?.title ?? body?.error;
    if (serverMessage) return String(serverMessage);
    if (status === 401) return "Not signed in (401). Pick an official on the Sign in screen.";
    if (status === 403) return "You do not have permission for this action (403).";
    return `Request failed: HTTP ${status} for ${url}`;
  }
}

export interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "DELETE";
  /** Mutually exclusive with formData. */
  body?: unknown;
  /** Sent as-is, no Content-Type set: the browser writes the multipart boundary. */
  formData?: FormData;
  headers?: Record<string, string>;
  signal?: AbortSignal;
}

// ---- Route typing: every path must be a route of the generated OpenAPI document ----

type DocumentRoute<K> = K extends `/api/v1${infer R}` ? PlaceholderRoute<R> : never;
type PlaceholderRoute<R extends string> = R extends `${infer A}{${string}}${infer B}`
  ? `${A}{}${PlaceholderRoute<B>}`
  : R;
type KnownRoute = DocumentRoute<keyof paths>;
type Segments<P extends string> = P extends `${infer H}/${infer T}` ? [H, ...Segments<T>] : [P];
type KnownSegments = KnownRoute extends infer K extends string ? Segments<K> : never;

// "{}" takes any segment but an empty one, so a stray trailing slash never passes.
type SegmentsMatch<P extends string[], K extends string[]> = P extends [
  infer PH extends string,
  ...infer PT extends string[],
]
  ? K extends [infer KH extends string, ...infer KT extends string[]]
    ? (KH extends "{}" ? (PH extends "" ? false : true) : PH extends KH ? true : false) extends true
      ? SegmentsMatch<PT, KT>
      : false
    : false
  : K extends []
    ? true
    : false;

type WithoutQuery<P extends string> = P extends `${infer A}?${string}` ? A : P;
type MatchesAnyRoute<S extends string[], K = KnownSegments> = K extends string[]
  ? SegmentsMatch<S, K>
  : never;
type RouteMatches<P extends string> = P extends unknown
  ? true extends MatchesAnyRoute<Segments<WithoutQuery<P>>>
    ? true
    : false
  : never;

/**
 * `P` itself when it names a route of the generated document (template holes stand for path
 * parameters, `?…` for a query string), otherwise a type no path is assignable to.
 */
export type ApiRoute<P extends string> = false extends RouteMatches<P> ? Unrouted<P> : P;
type Unrouted<P extends string> = `Not a route in generated.d.ts: ${P}`;

/** A parameterless route, so a caller can name `T` explicitly. */
export type StaticApiRoute = Exclude<KnownRoute, `${string}{}${string}`>;

export function apiUrl<P extends string>(path: ApiRoute<P>): string {
  return `${apiBase()}/api/v1${path}`;
}

export type Query = "" | `?${string}`;

/**
 * Throws ApiError on non-2xx and TypeError on network failure. `T` is inferred from the caller's
 * return type; only a parameterless, query-free route also accepts it explicitly.
 */
export function api<T>(path: StaticApiRoute, options?: RequestOptions): Promise<T>;
export function api<T, P extends string>(path: ApiRoute<P>, options?: RequestOptions): Promise<T>;
export async function api(path: string, options: RequestOptions = {}): Promise<unknown> {
  const url = `${apiBase()}/api/v1${path}`;
  const headers: Record<string, string> = { ...devAuthHeaders(), ...options.headers };
  let body: BodyInit | undefined;
  if (options.formData) {
    body = options.formData;
  } else if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
    body = JSON.stringify(options.body);
  }

  let response: Response;
  try {
    response = await fetch(url, {
      method: options.method ?? "GET",
      headers,
      ...(body !== undefined ? { body } : {}),
      ...(options.signal ? { signal: options.signal } : {}),
      credentials: "include",
    });
  } catch (err) {
    // Only a throw means no answer came back; an aborted request says nothing about the uplink.
    if (!(err instanceof Error && err.name === "AbortError")) reportUnreachable();
    throw err;
  }
  reportReachable();

  let parsed: unknown;
  try {
    parsed = await response.json();
  } catch {
    parsed = undefined;
  }
  if (!response.ok) {
    throw new ApiError(response.status, parsed as ApiErrorBody | undefined, url);
  }
  return parsed;
}

export interface ApiFile {
  blob: Blob;
  filename: string;
}

/** Separate from `api()` only because that wrapper parses every response as JSON. */
export async function apiFile<P extends string>(
  path: ApiRoute<P>,
  fallbackName: string,
): Promise<ApiFile> {
  const url = `${apiBase()}/api/v1${path}`;
  let response: Response;
  try {
    response = await fetch(url, { headers: devAuthHeaders(), credentials: "include" });
  } catch (err) {
    if (!(err instanceof Error && err.name === "AbortError")) reportUnreachable();
    throw err;
  }
  reportReachable();

  if (!response.ok) {
    let parsed: unknown;
    try {
      parsed = await response.json();
    } catch {
      parsed = undefined;
    }
    throw new ApiError(response.status, parsed as ApiErrorBody | undefined, url);
  }
  return { blob: await response.blob(), filename: attachmentName(response) ?? fallbackName };
}

/** `attachment; filename=standings-….csv` → the name, or undefined. */
function attachmentName(response: Response): string | undefined {
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(
    response.headers.get("Content-Disposition") ?? "",
  );
  return match?.[1];
}
