import { api } from "../api/client";
import type {
  AuthMethods,
  AuthenticatorEnrolment,
  IdentifyResponse,
  IssuedSetupLink,
  LocalAccount,
  LocalSignInAnswer,
  LocalSignInRequest,
  LocalSignInResponse,
  MyAccount,
  SetupCompleteRequest,
  SetupLinkInspection,
} from "../api/types";

export const localAccountKeys = {
  authMethods: () => ["/auth/methods"] as const,
  myAccount: () => ["/account"] as const,
  localAccounts: () => ["/admin/local-accounts"] as const,
};

/** A status without the fields it promises is a server fault, never a step to show. */
export function signInAnswerOf(response: LocalSignInResponse): LocalSignInAnswer {
  const { status, me, recoveryCodes, secret, otpauthUri } = response;
  if (status === "signedIn" && me) return { status, me, recoveryCodes };
  if (status === "enrolmentRequired" && secret && otpauthUri)
    return { status, secret, otpauthUri };
  if (status === "codeRequired") return { status };
  throw new Error("The server's sign-in answer was incomplete. Try again.");
}

// ---- Signing in -------------------------------------------------------

export function getAuthMethods(): Promise<AuthMethods> {
  return api<AuthMethods>("/auth/methods");
}

export function identify(username: string, returnUrl: string | null): Promise<IdentifyResponse> {
  return api<IdentifyResponse>("/auth/identify", {
    method: "POST",
    body: { username, returnUrl },
  });
}

export async function signInLocal(body: LocalSignInRequest): Promise<LocalSignInAnswer> {
  return signInAnswerOf(await api<LocalSignInResponse>("/auth/local", { method: "POST", body }));
}

export function inspectSetupLink(token: string): Promise<SetupLinkInspection> {
  return api<SetupLinkInspection>("/auth/setup/inspect", { method: "POST", body: { token } });
}

export async function completeSetup(body: SetupCompleteRequest): Promise<LocalSignInAnswer> {
  return signInAnswerOf(
    await api<LocalSignInResponse>("/auth/setup/complete", { method: "POST", body }),
  );
}

// ---- My account -------------------------------------------------------

export function getMyAccount(): Promise<MyAccount> {
  return api<MyAccount>("/account");
}

/** 204, no body. */
export async function changePassword(currentPassword: string, newPassword: string): Promise<void> {
  await api<unknown>("/account/password", {
    method: "POST",
    body: { currentPassword, newPassword },
  });
}

/** Asks for the password again, so an unattended signed-in device cannot enrol someone's phone. */
export function startAuthenticator(currentPassword: string): Promise<AuthenticatorEnrolment> {
  return api<AuthenticatorEnrolment>("/account/authenticator", {
    method: "POST",
    body: { currentPassword },
  });
}

export function confirmAuthenticator(code: string): Promise<{ recoveryCodes: string[] }> {
  return api<{ recoveryCodes: string[] }>("/account/authenticator/confirm", {
    method: "POST",
    body: { code },
  });
}

// ---- Administration ---------------------------------------------------

export function listLocalAccounts(): Promise<LocalAccount[]> {
  return api<LocalAccount[]>("/admin/local-accounts");
}

export function createLocalAccount(username: string, displayName: string): Promise<IssuedSetupLink> {
  return api<IssuedSetupLink>("/admin/local-accounts", {
    method: "POST",
    body: { username, displayName },
  });
}

export function resetLocalAccount(id: string): Promise<IssuedSetupLink> {
  return api(`/admin/local-accounts/${encodeURIComponent(id)}/reset`, { method: "POST", body: {} });
}

export type AccountChange = "disable" | "enable" | "clear-lockout";

export function changeLocalAccount(id: string, change: AccountChange): Promise<LocalAccount> {
  const path = `/admin/local-accounts/${encodeURIComponent(id)}` as const;
  const options = { method: "POST", body: {} } as const;
  if (change === "disable") return api(`${path}/disable`, options);
  if (change === "enable") return api(`${path}/enable`, options);
  return api(`${path}/clear-lockout`, options);
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
