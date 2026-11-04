import { useState, type FormEvent } from "react";
import { QrCode } from "./QrCode";

/** The key as the apps print it: groups of four are easier to type from across a desk. */
function grouped(secret: string): string {
  return secret.replace(/\s+/g, "").replace(/(.{4})(?=.)/g, "$1 ");
}

export function AuthenticatorEnrolment({
  secret,
  otpauthUri,
  busy,
  error,
  onConfirm,
  onSkip,
}: {
  secret: string;
  otpauthUri: string;
  busy: boolean;
  error: string | null;
  onConfirm: (code: string) => void;
  onSkip?: () => void;
}) {
  const [code, setCode] = useState("");
  const digits = code.replace(/\s+/g, "");

  function submit(e: FormEvent): void {
    e.preventDefault();
    if (busy || digits.length !== 6) return;
    onConfirm(digits);
  }

  return (
    <form className="card" onSubmit={submit}>
      <h2>Set up an authenticator app</h2>
      <p>
        Open an authenticator app on your phone (Microsoft Authenticator, Google Authenticator,
        the iPhone's Passwords app or similar), add an account, and scan this code.
      </p>
      <QrCode value={otpauthUri} label="QR code for your authenticator app" />
      <p className="row-sub">Can't scan it? Type this key into the app instead:</p>
      <p className="mono secret-key">{grouped(secret)}</p>
      <label className="field">
        <span>The 6-digit code the app now shows</span>
        <input
          value={code}
          inputMode="numeric"
          autoComplete="one-time-code"
          maxLength={7}
          aria-invalid={error ? true : undefined}
          onChange={(e) => setCode(e.target.value)}
        />
      </label>
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
      <div className="button-row">
        <button className="primary" type="submit" disabled={busy || digits.length !== 6}>
          {busy ? "Checking…" : "Confirm"}
        </button>
        {onSkip && (
          <button type="button" disabled={busy} onClick={onSkip}>
            Skip for now
          </button>
        )}
      </div>
    </form>
  );
}
