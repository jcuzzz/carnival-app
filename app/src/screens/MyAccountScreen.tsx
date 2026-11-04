import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import {
  DIRECT_WRITE_NETWORK_ERROR_TEXT,
  errorText,
  passwordCheckText,
} from "../api/errorText";
import type { AuthenticatorEnrolment as Enrolment } from "../api/types";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import {
  changePassword,
  confirmAuthenticator,
  getMyAccount,
  localAccountKeys,
  startAuthenticator,
} from "../auth/localAccounts";
import { useSession } from "../auth/session";
import { AuthenticatorEnrolment } from "../features/accounts/AuthenticatorEnrolment";
import {
  PasswordFields,
  newPasswordReady,
  type NewPassword,
} from "../features/accounts/PasswordFields";
import { RecoveryCodes } from "../features/accounts/RecoveryCodes";
import { SignInOffer, useAccessGate } from "../features/ui/SignInOffer";
import { href } from "../router";
import { showToast } from "../shell/toast";
import { useConnectivity } from "../shell/useOnline";

const LOW_RECOVERY_CODES = 3;

export default function MyAccountScreen() {
  const session = useSession();
  const local = session?.localAccount === true;
  const gate = useAccessGate(local);
  const offline = useConnectivity() !== "connected";
  const account = useApiQuery(localAccountKeys.myAccount(), getMyAccount, { enabled: local });
  const invalidate = useInvalidate();
  const [enrolment, setEnrolment] = useState<Enrolment | null>(null);
  const [codes, setCodes] = useState<string[] | null>(null);
  const [enrolError, setEnrolError] = useState<string | null>(null);
  const [enrolBusy, setEnrolBusy] = useState(false);
  const [enrolPassword, setEnrolPassword] = useState("");
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState<NewPassword>({ password: "", confirm: "" });
  const [passwordBusy, setPasswordBusy] = useState(false);
  const [currentError, setCurrentError] = useState<string | null>(null);
  const [newError, setNewError] = useState<string | null>(null);
  const [passwordError, setPasswordError] = useState<string | null>(null);

  async function begin(e: FormEvent): Promise<void> {
    e.preventDefault();
    if (enrolBusy || enrolPassword === "") return;
    setEnrolBusy(true);
    setEnrolError(null);
    try {
      setEnrolment(await startAuthenticator(enrolPassword));
      setEnrolPassword("");
    } catch (err) {
      setEnrolError(passwordCheckText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
      if (err instanceof ApiError && err.code === "AUTHENTICATOR_ALREADY_ENROLLED")
        invalidate(localAccountKeys.myAccount());
    } finally {
      setEnrolBusy(false);
    }
  }

  async function confirm(code: string): Promise<void> {
    setEnrolBusy(true);
    setEnrolError(null);
    try {
      const { recoveryCodes } = await confirmAuthenticator(code);
      setEnrolment(null);
      setCodes(recoveryCodes);
      invalidate(localAccountKeys.myAccount());
    } catch (err) {
      setEnrolError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setEnrolBusy(false);
    }
  }

  async function savePassword(e: FormEvent): Promise<void> {
    e.preventDefault();
    if (passwordBusy || current === "" || !newPasswordReady(next)) return;
    setPasswordBusy(true);
    setCurrentError(null);
    setNewError(null);
    setPasswordError(null);
    try {
      await changePassword(current, next.password);
      setCurrent("");
      setNext({ password: "", confirm: "" });
      showToast("Password changed. Other devices signed in as you are signed out.");
    } catch (err) {
      const code = err instanceof ApiError ? err.code : undefined;
      if (code === "CURRENT_PASSWORD_WRONG") setCurrentError(errorText(err));
      else if (code?.startsWith("PASSWORD_")) setNewError(errorText(err));
      else setPasswordError(passwordCheckText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setPasswordBusy(false);
    }
  }

  if (gate === "loading") return <p>Loading…</p>;
  if (gate === "signedOut") {
    return (
      <SignInOffer title="My account">
        <p>Sign in to change your password or set up an authenticator app.</p>
      </SignInOffer>
    );
  }
  if (gate === "refused") {
    return (
      <>
        <h1>My account</h1>
        <div className="alert notice" role="status">
          You signed in with your school account, so its password and sign-in checks are managed
          by the school, not here.
        </div>
        <p>
          <a href={href("/carnivals")}>← Back to carnivals</a>
        </p>
      </>
    );
  }

  const data = account.data;
  const enrolled = data?.authenticatorEnrolled === true;
  const passwordIsPrimary = enrolled && enrolment === null && codes === null;

  return (
    <>
      <h1>My account</h1>
      {account.error && (
        <div className="alert error" role="alert">
          {errorText(account.error, "Can’t reach the server, so your account couldn’t be read.")}
        </div>
      )}
      {offline && (
        <div className="alert notice" role="status">
          Can’t reach the server, so nothing here can be changed until you reconnect.
        </div>
      )}
      {account.loading && <p>Loading…</p>}

      {data && (
        <div className="card">
          <p>
            <strong>{data.displayName}</strong>
          </p>
          <p className="row-sub">Username: {data.username}</p>
        </div>
      )}

      <h2>Authenticator app</h2>
      {codes ? (
        <RecoveryCodes codes={codes} onDone={() => setCodes(null)} />
      ) : enrolment ? (
        <AuthenticatorEnrolment
          secret={enrolment.secret}
          otpauthUri={enrolment.otpauthUri}
          busy={enrolBusy}
          error={enrolError}
          onConfirm={(code) => void confirm(code)}
          onSkip={() => {
            setEnrolment(null);
            setEnrolError(null);
          }}
        />
      ) : data && enrolled ? (
        <div className="card">
          <p>
            <span className="badge ok">set up</span> Signing in asks for a code from your app.
          </p>
          <p className={data.recoveryCodesLeft <= LOW_RECOVERY_CODES ? "field-error" : "row-sub"}>
            <span className="mono">{data.recoveryCodesLeft}</span>{" "}
            {data.recoveryCodesLeft === 1 ? "recovery code" : "recovery codes"} left. If you lose
            your phone and run out, a system administrator can reset your account.
          </p>
        </div>
      ) : data ? (
        <form className="alert warn" onSubmit={(e) => void begin(e)}>
          <p role="status">
            Your account has no authenticator app, so your password alone signs you in.
            {data.requireAuthenticator
              ? " Your school requires one: you will be asked to set it up at your next sign-in."
              : " Setting one up stops anyone who learns your password from signing in as you."}
          </p>
          <input type="text" autoComplete="username" value={data.username} readOnly hidden />
          <label className="field">
            <span>Your password, to confirm it's you</span>
            <input
              type="password"
              autoComplete="current-password"
              value={enrolPassword}
              maxLength={256}
              aria-invalid={enrolError ? true : undefined}
              onChange={(e) => {
                setEnrolPassword(e.target.value);
                setEnrolError(null);
              }}
            />
          </label>
          {enrolError && (
            <p className="field-error" role="alert">
              {enrolError}
            </p>
          )}
          <div className="button-row">
            <button
              className="primary"
              type="submit"
              disabled={enrolBusy || offline || enrolPassword === ""}
            >
              {enrolBusy ? "Starting…" : "Set up an authenticator app"}
            </button>
          </div>
        </form>
      ) : null}

      <h2>Change password</h2>
      <form className="card settings-form" onSubmit={(e) => void savePassword(e)}>
        <input type="text" autoComplete="username" value={data?.username ?? ""} readOnly hidden />
        <label className="field wide">
          <span>Current password</span>
          <input
            type="password"
            autoComplete="current-password"
            value={current}
            maxLength={256}
            aria-invalid={currentError ? true : undefined}
            onChange={(e) => {
              setCurrent(e.target.value);
              setCurrentError(null);
            }}
          />
        </label>
        {currentError && (
          <p className="field-error" role="alert">
            {currentError}
          </p>
        )}
        <PasswordFields
          value={next}
          error={newError}
          onChange={(value) => {
            setNext(value);
            setNewError(null);
          }}
        />
        {passwordError && (
          <div className="alert error" role="alert">
            {passwordError}
          </div>
        )}
        <p className="row-sub">
          Changing it signs you out on every other device. This one stays signed in.
        </p>
        <div className="button-row">
          <button
            className={passwordIsPrimary ? "primary" : undefined}
            type="submit"
            disabled={passwordBusy || offline || current === "" || !newPasswordReady(next)}
          >
            {passwordBusy ? "Changing…" : "Change password"}
          </button>
        </div>
      </form>
    </>
  );
}
