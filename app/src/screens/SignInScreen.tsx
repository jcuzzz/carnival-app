import { useState, type FormEvent } from "react";
import { ApiError, apiBase } from "../api/client";
import {
  SIGN_IN_FAILED_TEXT,
  SIGN_IN_NEEDS_SERVER_TEXT,
  passwordCheckText,
} from "../api/errorText";
import type { LocalSignInRequest, MeResponse } from "../api/types";
import { useApiQuery } from "../api/useApiQuery";
import { resumeAfterSignIn } from "../api/syncSetup";
import { sessionOf } from "../auth/entra";
import {
  getAuthMethods,
  identify,
  localAccountKeys,
  signInLocal,
} from "../auth/localAccounts";
import { signIn, useSession } from "../auth/session";
import { PRODUCT_NAME, useBranding } from "../branding/branding";
import { AuthenticatorEnrolment } from "../features/accounts/AuthenticatorEnrolment";
import { RecoveryCodes } from "../features/accounts/RecoveryCodes";
import { navigate, useRouteQuery } from "../router";
import { useConnectivity } from "../shell/useOnline";

// Identifier first (ADR-0065 §2): the username decides between a password here and the provider.

const UNEXPECTED_ANSWER_TEXT =
  "The server answered in a way this app doesn't recognise, so sign-in stopped. Try again.";

type Step =
  | { kind: "username" }
  | { kind: "password" }
  | { kind: "code" }
  | { kind: "recovery" }
  | { kind: "enrol"; secret: string; otpauthUri: string }
  | { kind: "codes"; codes: string[] };

/** Same-origin paths only, or the box becomes an open redirect. */
export function returnUrlOf(query: string): string | null {
  const raw = new URLSearchParams(query).get("returnUrl");
  if (!raw || !raw.startsWith("/") || raw.startsWith("//") || raw.includes("\\")) return null;
  return raw;
}

/** The hash route inside a return address; never back into sign-in or setup. */
export function returnPathOf(returnUrl: string | null): string {
  const hash = returnUrl?.indexOf("#") ?? -1;
  const path = returnUrl && hash !== -1 ? returnUrl.slice(hash + 1) : "";
  if (!path.startsWith("/") || /^\/(login|setup)\b/.test(path)) return "/";
  return path;
}

function failureText(err: unknown): string {
  if (err instanceof TypeError) return SIGN_IN_NEEDS_SERVER_TEXT;
  return passwordCheckText(err, SIGN_IN_NEEDS_SERVER_TEXT);
}

export default function SignInScreen({
  go = (href: string) => location.assign(href),
}: {
  go?: (href: string) => void;
}) {
  const session = useSession();
  const { schoolName } = useBranding();
  const offline = useConnectivity() !== "connected";
  const methods = useApiQuery(localAccountKeys.authMethods(), getAuthMethods);
  const returnUrl = returnUrlOf(useRouteQuery());
  const [step, setStep] = useState<Step>({ kind: "username" });
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const provider = methods.data?.provider ?? null;

  function startAgain(message: string | null = null): void {
    setStep({ kind: "username" });
    setPassword("");
    setCode("");
    setError(message);
  }

  function adopt(me: MeResponse): void {
    signIn(sessionOf(me));
    // Pause-never-drop: a 401-paused queue resumes under the new session.
    void resumeAfterSignIn();
  }

  function leave(): void {
    navigate(returnPathOf(returnUrl), { replace: true });
  }

  async function submitUsername(e: FormEvent): Promise<void> {
    e.preventDefault();
    const name = username.trim();
    if (busy || name === "") return;
    setBusy(true);
    setError(null);
    try {
      const answer = await identify(name, returnUrl);
      if (answer.method === "provider") {
        // Only ever the API's own provider sign-in: anything else would be an open redirect.
        if (answer.loginUrl.startsWith(`${apiBase()}/api/v1/auth/login?`)) go(answer.loginUrl);
        else setError(UNEXPECTED_ANSWER_TEXT);
        return;
      }
      setUsername(name);
      setStep({ kind: "password" });
    } catch (err) {
      setError(failureText(err));
    } finally {
      setBusy(false);
    }
  }

  async function attempt(extra: Omit<LocalSignInRequest, "username" | "password">): Promise<void> {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const answer = await signInLocal({ username, password, ...extra });
      setCode("");
      if (answer.status === "codeRequired") {
        setStep({ kind: "code" });
      } else if (answer.status === "enrolmentRequired") {
        setStep({ kind: "enrol", secret: answer.secret, otpauthUri: answer.otpauthUri });
      } else {
        adopt(answer.me);
        setPassword("");
        if (answer.recoveryCodes && answer.recoveryCodes.length > 0)
          setStep({ kind: "codes", codes: answer.recoveryCodes });
        else leave();
      }
    } catch (err) {
      const refusal = err instanceof ApiError ? err.code : undefined;
      if (refusal === "SIGN_IN_FAILED") {
        setStep({ kind: "password" });
        setPassword("");
        setCode("");
        setError(SIGN_IN_FAILED_TEXT);
      } else if (refusal === "LOCAL_SIGN_IN_NOT_ALLOWED") {
        startAgain(failureText(err));
      } else {
        setError(failureText(err));
      }
    } finally {
      setBusy(false);
    }
  }

  function submit(e: FormEvent, extra: Omit<LocalSignInRequest, "username" | "password">): void {
    e.preventDefault();
    void attempt(extra);
  }

  const errorAlert = error && (
    <div className="alert error" role="alert">
      {error}
    </div>
  );
  const changeUser = (
    <p className="row-sub">
      Signing in as <strong>{username}</strong>{" "}
      <button type="button" disabled={busy} onClick={() => startAgain()}>
        Not you?
      </button>
    </p>
  );
  // Lets a password manager pair the saved password with the right name.
  const usernameForManagers = (
    <input type="text" autoComplete="username" value={username} readOnly hidden />
  );

  return (
    <>
      <h1>Sign in</h1>
      <p className="row-sub">To {schoolName ?? PRODUCT_NAME}.</p>
      {/* Always the box: a lapsed or ended session still reads as signed in on this device,
          and signing in again is what resumes a paused queue. */}
      {session && step.kind !== "codes" && (
        <div className="alert notice" role="status">
          This device has <strong>{session.displayName}</strong> signed in. If that session has
          ended, sign in again below; signing in as someone else takes over the device.
        </div>
      )}
      {offline && (
        <div className="alert notice" role="status">
          {SIGN_IN_NEEDS_SERVER_TEXT}
        </div>
      )}

      {step.kind === "username" && (
        <form className="card" onSubmit={(e) => void submitUsername(e)}>
          {errorAlert}
          <label className="field">
            <span>{provider ? "Username or school email" : "Username"}</span>
            <input
              value={username}
              autoComplete="username"
              autoCapitalize="none"
              spellCheck={false}
              maxLength={256}
              autoFocus
              onChange={(e) => setUsername(e.target.value)}
            />
          </label>
          {provider && (
            <p className="row-sub">
              A school email takes you to your school's own sign-in, then back here.
            </p>
          )}
          <div className="button-row">
            <button className="primary" type="submit" disabled={busy || username.trim() === ""}>
              {busy ? "Checking…" : "Continue"}
            </button>
          </div>
        </form>
      )}

      {step.kind === "password" && (
        <form className="card" onSubmit={(e) => submit(e, {})}>
          {changeUser}
          {errorAlert}
          {usernameForManagers}
          <label className="field">
            <span>Password</span>
            <input
              type="password"
              value={password}
              autoComplete="current-password"
              maxLength={256}
              autoFocus
              onChange={(e) => setPassword(e.target.value)}
            />
          </label>
          <div className="button-row">
            <button className="primary" type="submit" disabled={busy || password === ""}>
              {busy ? "Signing in…" : "Sign in"}
            </button>
          </div>
        </form>
      )}

      {step.kind === "code" && (
        <form className="card" onSubmit={(e) => submit(e, { code: code.replace(/\s+/g, "") })}>
          {changeUser}
          {errorAlert}
          <label className="field">
            <span>The 6-digit code from your authenticator app</span>
            <input
              value={code}
              inputMode="numeric"
              autoComplete="one-time-code"
              maxLength={7}
              autoFocus
              onChange={(e) => setCode(e.target.value)}
            />
          </label>
          <div className="button-row">
            <button
              className="primary"
              type="submit"
              disabled={busy || code.replace(/\s+/g, "").length !== 6}
            >
              {busy ? "Signing in…" : "Sign in"}
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={() => {
                setCode("");
                setError(null);
                setStep({ kind: "recovery" });
              }}
            >
              Use a recovery code instead
            </button>
          </div>
        </form>
      )}

      {step.kind === "recovery" && (
        <form className="card" onSubmit={(e) => submit(e, { recoveryCode: code.trim() })}>
          {changeUser}
          {errorAlert}
          <label className="field">
            <span>One of your recovery codes</span>
            <input
              value={code}
              autoComplete="off"
              autoCapitalize="none"
              spellCheck={false}
              maxLength={64}
              autoFocus
              onChange={(e) => setCode(e.target.value)}
            />
          </label>
          <p className="row-sub">
            Each code works once. A system administrator can reset your account if you have none
            left.
          </p>
          <div className="button-row">
            <button className="primary" type="submit" disabled={busy || code.trim() === ""}>
              {busy ? "Signing in…" : "Sign in"}
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={() => {
                setCode("");
                setError(null);
                setStep({ kind: "code" });
              }}
            >
              Use the authenticator app instead
            </button>
          </div>
        </form>
      )}

      {step.kind === "enrol" && (
        <>
          {changeUser}
          <div className="alert notice" role="status">
            Your school requires an authenticator app. Set one up to finish signing in.
          </div>
          <AuthenticatorEnrolment
            secret={step.secret}
            otpauthUri={step.otpauthUri}
            busy={busy}
            error={error}
            onConfirm={(enrolmentCode) => void attempt({ enrolmentCode })}
          />
        </>
      )}

      {step.kind === "codes" && <RecoveryCodes codes={step.codes} onDone={leave} />}
    </>
  );
}
