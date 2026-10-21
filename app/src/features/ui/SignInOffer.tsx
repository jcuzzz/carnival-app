import type { ReactNode } from "react";
import { entraAuthEnabled, loginHref, useEntraSessionSettled } from "../../auth/entra";
import { useSession } from "../../auth/session";
import { href } from "../../router";

/**
 * Signed out is not the same as wrong role: a role-named refusal is
 * unanswerable advice to somebody whose session simply lapsed.
 */
export function SignInOffer({ title, children }: { title: string; children: ReactNode }) {
  return (
    <>
      <h1>{title}</h1>
      {children}
      <p>
        <a href={entraAuthEnabled() ? loginHref() : href("/login")}>Sign in</a>
      </p>
    </>
  );
}

export type AccessGate = "loading" | "signedOut" | "refused" | "allowed";

/** A real build resolves /me after first paint, so a gate must wait to refuse. */
export function useAccessGate(allowed: boolean): AccessGate {
  const settled = useEntraSessionSettled();
  const session = useSession();
  if (!settled) return "loading";
  if (session === null) return "signedOut";
  return allowed ? "allowed" : "refused";
}
