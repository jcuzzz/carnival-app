import { ApiError, apiBase, devAuthHeaders } from "../api/client";
import { reportReachable, reportUnreachable } from "../api/reachability";
import type {
  ApiErrorBody,
  AuthenticatorEnrolment,
  IdentifyResponse,
  IssuedSetupLink,
  LocalAccount,
  LocalSignInRequest,
  LocalSignInResponse,
  MyAccount,
  SetupCompleteRequest,
  SetupLinkInspection,
  StartAuthenticatorRequest,
} from "../api/types";

// Raw fetch until the generated document names these routes and `api()` can type them.
async function request<T>(
  path: string,
  options: { method?: "GET" | "POST"; body?: unknown } = {},
): Promise<T> {
  const url = `${apiBase()}/api/v1${path}`;
  const headers: Record<string, string> = { ...devAuthHeaders() };
  if (options.body !== undefined) headers["Content-Type"] = "application/json";
  let response: Response;
  try {
    response = await fetch(url, {
      method: options.method ?? "GET",
      headers,
      ...(options.body !== undefined ? { body: JSON.stringify(options.body) } : {}),
      credentials: "include",
    });
  } catch (err) {
    reportUnreachable();
    throw err;
  }
  reportReachable();
  let parsed: unknown;
  try {
    parsed = await response.json();
  } catch {
    parsed = undefined;
  }
  if (!response.ok) throw new ApiError(response.status, parsed as ApiErrorBody | undefined, url);
  return parsed as T;
}

const post = <T>(path: string, body: unknown = {}) => request<T>(path, { method: "POST", body });

export const localAccountKeys = {
  authMethods: () => ["/auth/methods"] as const,
  myAccount: () => ["/account"] as const,
  localAccounts: () => ["/admin/local-accounts"] as const,
};

// ---- Signing in -------------------------------------------------------

export function getAuthMethods(): Promise<{ provider: "Entra" | "Adfs" | null }> {
  return request("/auth/methods");
}

export function identify(username: string, returnUrl: string | null): Promise<IdentifyResponse> {
  return post("/auth/identify", { username, returnUrl });
}

export function signInLocal(body: LocalSignInRequest): Promise<LocalSignInResponse> {
  return post("/auth/local", body);
}

export function inspectSetupLink(token: string): Promise<SetupLinkInspection> {
  return post("/auth/setup/inspect", { token });
}

export function completeSetup(body: SetupCompleteRequest): Promise<LocalSignInResponse> {
  return post("/auth/setup/complete", body);
}

// ---- My account -------------------------------------------------------

export function getMyAccount(): Promise<MyAccount> {
  return request("/account");
}

export function changePassword(currentPassword: string, newPassword: string): Promise<void> {
  return post("/account/password", { currentPassword, newPassword });
}

/** Asks for the password again, so an unattended signed-in device cannot enrol someone's phone. */
export function startAuthenticator(currentPassword: string): Promise<AuthenticatorEnrolment> {
  const body: StartAuthenticatorRequest = { currentPassword };
  return post("/account/authenticator", body);
}

export function confirmAuthenticator(code: string): Promise<{ recoveryCodes: string[] }> {
  return post("/account/authenticator/confirm", { code });
}

// ---- Administration ---------------------------------------------------

export function listLocalAccounts(): Promise<LocalAccount[]> {
  return request("/admin/local-accounts");
}

export function createLocalAccount(username: string, displayName: string): Promise<IssuedSetupLink> {
  return post("/admin/local-accounts", { username, displayName });
}

export function resetLocalAccount(id: string): Promise<IssuedSetupLink> {
  return post(`/admin/local-accounts/${encodeURIComponent(id)}/reset`);
}

export type AccountChange = "disable" | "enable" | "clear-lockout";

export function changeLocalAccount(id: string, change: AccountChange): Promise<LocalAccount> {
  return post(`/admin/local-accounts/${encodeURIComponent(id)}/${change}`);
}

/** The address an administrator hands over: hash-routed, so the token never reaches a server log. */
export function setupLinkUrl(token: string): string {
  return `${location.origin}/#/setup/${token}`;
}

// ---- The setup token --------------------------------------------------

// Memory only: never storage, so a reload or a shared device cannot replay it.
let heldSetupToken: string | null = null;

export function holdSetupToken(token: string): void {
  heldSetupToken = token;
}

export function setupToken(): string | null {
  return heldSetupToken;
}

export function dropSetupToken(): void {
  heldSetupToken = null;
}
