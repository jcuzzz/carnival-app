import { useId } from "react";

export const MIN_PASSWORD_LENGTH = 15;
const MAX_PASSWORD_LENGTH = 256;

export interface NewPassword {
  password: string;
  confirm: string;
}

/** A preview of the server's length rule; the server applies it again with the rest. */
export function newPasswordReady({ password, confirm }: NewPassword): boolean {
  return [...password].length >= MIN_PASSWORD_LENGTH && password === confirm;
}

export function PasswordFields({
  value,
  label = "New password",
  error,
  onChange,
}: {
  value: NewPassword;
  label?: string;
  error: string | null;
  onChange: (next: NewPassword) => void;
}) {
  const id = useId();
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;
  const length = [...value.password].length;
  const mismatch = value.confirm !== "" && value.confirm !== value.password;

  return (
    <>
      <label className="field wide">
        <span>{label}</span>
        <input
          type="password"
          autoComplete="new-password"
          value={value.password}
          maxLength={MAX_PASSWORD_LENGTH}
          aria-describedby={error ? `${hintId} ${errorId}` : hintId}
          aria-invalid={error ? true : undefined}
          onChange={(e) => onChange({ ...value, password: e.target.value })}
        />
      </label>
      <p className="row-sub" id={hintId}>
        At least {MIN_PASSWORD_LENGTH} characters. A short sentence works well; it needs no
        symbols or numbers, and it never expires.
        {length > 0 && length < MIN_PASSWORD_LENGTH && (
          <> {MIN_PASSWORD_LENGTH - length} more to go.</>
        )}
      </p>
      {error && (
        <p className="field-error" id={errorId} role="alert">
          {error}
        </p>
      )}
      <label className="field wide">
        <span>Type it again</span>
        <input
          type="password"
          autoComplete="new-password"
          value={value.confirm}
          maxLength={MAX_PASSWORD_LENGTH}
          aria-invalid={mismatch ? true : undefined}
          onChange={(e) => onChange({ ...value, confirm: e.target.value })}
        />
      </label>
      {mismatch && <p className="field-error">The two passwords don't match yet.</p>}
    </>
  );
}
