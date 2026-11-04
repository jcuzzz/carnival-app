import { useEffect, useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import { SIGN_IN_NEEDS_SERVER_TEXT, errorText, passwordCheckText } from "../api/errorText";
import type { MeResponse, SetupLinkInspection } from "../api/types";
import { resumeAfterSignIn } from "../api/syncSetup";
import { sessionOf } from "../auth/entra";
import {
  completeSetup,
  confirmAuthenticator,
  dropSetupToken,
  inspectSetupLink,
  setupToken,
  signInLocal,
  startAuthenticator,
} from "../auth/localAccounts";
import { signIn, useSession } from "../auth/session";
import { AuthenticatorEnrolment } from "../features/accounts/AuthenticatorEnrolment";
import {
  PasswordFields,
  newPasswordReady,
  type NewPassword,
} from "../features/accounts/PasswordFields";
import { RecoveryCodes } from "../features/accounts/RecoveryCodes";
import { navigate } from "../router";

type Phase =
  | { kind: "inspecting" }
  | { kind: "unusable"; message: string; retry?: boolean }
  | { kind: "form"; link: SetupLinkInspection }
  | { kind: "offer" }
  | { kind: "enrol"; secret: string; otpauthUri: string; required: boolean }
  | { kind: "codes"; codes: string[] };

type Field = "username" | "displayName" | "password";

function fieldOf(code: string | undefined): Field | null {
  if (code?.startsWith("PASSWORD_")) return "password";
  if (code?.startsWith("ACCOUNT_USERNAME_")) return "username";
  if (code === "ACCOUNT_DISPLAY_NAME_INVALID") return "displayName";
  return null;
}

const NO_TOKEN_TEXT =
  "Open your setup link again. The link leaves the address bar as soon as it is opened, " +
  "so reloading this page loses it.";

export default function SetupScreen() {
  const session = useSession();
  const [token] = useState(setupToken);
  const [phase, setPhase] = useState<Phase>(() =>
    token ? { kind: "inspecting" } : { kind: "unusable", message: NO_TOKEN_TEXT },
  );
  const [username, setUsername] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [password, setPassword] = useState<NewPassword>({ password: "", confirm: "" });
  const [fieldError, setFieldError] = useState<{ field: Field; text: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [inspection, setInspection] = useState(0);

  // The screen holds its own copy; nothing outlives it.
  useEffect(() => () => dropSetupToken(), []);

  useEffect(() => {
    if (!token) return;
    let live = true;
    inspectSetupLink(token).then(
      (link) => live && setPhase({ kind: "form", link }),
      (err: unknown) =>
        live &&
        setPhase({
          kind: "unusable",
          message: errorText(err, SIGN_IN_NEEDS_SERVER_TEXT),
          // Only an unanswered request is worth repeating; a refused link stays refused.
          retry: !(err instanceof ApiError),
        }),
    );
    return () => {
      live = false;
    };
  }, [token, inspection]);

  function adopt(me: MeResponse): void {
    signIn(sessionOf(me));
    void resumeAfterSignIn();
  }

  function finish(): void {
    navigate("/", { replace: true });
  }

  async function run(work: () => Promise<void>): Promise<void> {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      await work();
    } catch (err) {
      setError(passwordCheckText(err, SIGN_IN_NEEDS_SERVER_TEXT));
    } finally {
      setBusy(false);
    }
  }

  async function complete(e: FormEvent, link: SetupLinkInspection): Promise<void> {
    e.preventDefault();
    if (busy || !token) return;
    const first = link.kind === "firstAdministrator";
    const name = first ? username.trim() : (link.username ?? "");
    setBusy(true);
    setError(null);
    setFieldError(null);
    try {
      const answer = await completeSetup({
        token,
        ...(first ? { username: name, displayName: displayName.trim() } : {}),
        password: password.password,
      });
      dropSetupToken();
      setUsername(name);
      if (answer.status === "enrolmentRequired") {
        setPhase({
          kind: "enrol",
          secret: answer.secret,
          otpauthUri: answer.otpauthUri,
          required: true,
        });
      } else if (answer.status === "signedIn") {
        adopt(answer.me);
        setPhase(
          answer.recoveryCodes?.length
            ? { kind: "codes", codes: answer.recoveryCodes }
            : { kind: "offer" },
        );
      } else {
        navigate("/login", { replace: true });
      }
    } catch (err) {
      const code = err instanceof ApiError ? err.code : undefined;
      const field = fieldOf(code);
      if (code === "SETUP_LINK_INVALID") {
        dropSetupToken();
        setPhase({ kind: "unusable", message: errorText(err) });
      } else if (field) setFieldError({ field, text: errorText(err) });
      else setError(passwordCheckText(err, SIGN_IN_NEEDS_SERVER_TEXT));
    } finally {
      setBusy(false);
    }
  }

  const fieldErrorFor = (field: Field) => (fieldError?.field === field ? fieldError.text : null);

  const errorAlert = error && (
    <div className="alert error" role="alert">
      {error}
    </div>
  );

  return (
    <>
      <h1>Set up your account</h1>

      {phase.kind === "inspecting" && <p>Checking the setup link…</p>}

      {phase.kind === "unusable" && (
        <div className="alert error" role="alert">
          {phase.message}
          {phase.retry && (
            <div className="button-row">
              <button
                type="button"
                onClick={() => {
                  setPhase({ kind: "inspecting" });
                  setInspection((n) => n + 1);
                }}
              >
                Retry
              </button>
            </div>
          )}
        </div>
      )}

      {phase.kind === "form" && (
        <form className="card settings-form" onSubmit={(e) => void complete(e, phase.link)}>
          {session && (
            <div className="alert notice" role="status">
              {session.displayName} is signed in on this device. Finishing setup signs this device
              in as the new account instead.
            </div>
          )}
          {phase.link.kind === "firstAdministrator" ? (
            <>
              <p>
                You are setting up the first system administrator. This account can always sign
                in, even if the school's own sign-in is down.
              </p>
              <label className="field wide">
                <span>Username</span>
                <input
                  value={username}
                  autoComplete="username"
                  autoCapitalize="none"
                  spellCheck={false}
                  maxLength={64}
                  aria-invalid={fieldErrorFor("username") ? true : undefined}
                  onChange={(e) => setUsername(e.target.value)}
                />
              </label>
              <p className="row-sub">
                A plain name such as backup-admin: lower-case letters, digits, dots and dashes,
                never an email address.
              </p>
              {fieldErrorFor("username") && (
                <p className="field-error" role="alert">
                  {fieldErrorFor("username")}
                </p>
              )}
              <label className="field wide">
                <span>Display name</span>
                <input
                  value={displayName}
                  autoComplete="name"
                  maxLength={100}
                  aria-invalid={fieldErrorFor("displayName") ? true : undefined}
                  onChange={(e) => setDisplayName(e.target.value)}
                />
              </label>
              {fieldErrorFor("displayName") && (
                <p className="field-error" role="alert">
                  {fieldErrorFor("displayName")}
                </p>
              )}
            </>
          ) : (
            <>
              <p>
                Choose a password for <strong>{phase.link.displayName}</strong>.
              </p>
              <p className="row-sub">
                Your username is <strong>{phase.link.username}</strong>.
              </p>
              <input
                type="text"
                autoComplete="username"
                value={phase.link.username ?? ""}
                readOnly
                hidden
              />
            </>
          )}
          <PasswordFields
            value={password}
            label="Password"
            error={fieldErrorFor("password")}
            onChange={(next) => {
              setPassword(next);
              if (fieldError?.field === "password") setFieldError(null);
            }}
          />
          <p className="row-sub">
            This link works once and expires {new Date(phase.link.expiresAtUtc).toLocaleString()}.
          </p>
          {errorAlert}
          <div className="button-row">
            <button
              className="primary"
              type="submit"
              disabled={
                busy ||
                !newPasswordReady(password) ||
                (phase.link.kind === "firstAdministrator" &&
                  (username.trim() === "" || displayName.trim() === ""))
              }
            >
              {busy ? "Saving…" : "Save password"}
            </button>
          </div>
        </form>
      )}

      {phase.kind === "offer" && (
        <div className="card">
          <h2>Add an authenticator app?</h2>
          <p>
            With an authenticator app on your phone, a stolen password alone can't open your
            account. It takes a minute and needs no phone number or email.
          </p>
          {errorAlert}
          <div className="button-row">
            <button
              className="primary"
              type="button"
              disabled={busy}
              onClick={() =>
                void run(async () => {
                  // The password just chosen is the current one: enrolling asks for it again.
                  const started = await startAuthenticator(password.password);
                  setPhase({ kind: "enrol", ...started, required: false });
                })
              }
            >
              Set up an authenticator app (recommended)
            </button>
            <button type="button" disabled={busy} onClick={finish}>
              Skip for now
            </button>
          </div>
        </div>
      )}

      {phase.kind === "enrol" && (
        <>
          {phase.required && (
            <div className="alert notice" role="status">
              Your school requires an authenticator app. Set one up to finish.
            </div>
          )}
          <AuthenticatorEnrolment
            secret={phase.secret}
            otpauthUri={phase.otpauthUri}
            busy={busy}
            error={error}
            onSkip={phase.required ? undefined : finish}
            onConfirm={(code) =>
              void run(async () => {
                if (phase.required) {
                  const answer = await signInLocal({
                    username,
                    password: password.password,
                    enrolmentCode: code,
                  });
                  if (answer.status !== "signedIn")
                    throw new Error("Setup didn't finish. Sign in with your new password.");
                  adopt(answer.me);
                  setPassword({ password: "", confirm: "" });
                  if (answer.recoveryCodes?.length)
                    setPhase({ kind: "codes", codes: answer.recoveryCodes });
                  else finish();
                } else {
                  const { recoveryCodes } = await confirmAuthenticator(code);
                  setPassword({ password: "", confirm: "" });
                  setPhase({ kind: "codes", codes: recoveryCodes });
                }
              })
            }
          />
        </>
      )}

      {phase.kind === "codes" && <RecoveryCodes codes={phase.codes} onDone={finish} />}
    </>
  );
}
