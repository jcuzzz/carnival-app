import { useState } from "react";

/** Shown once: the caller drops the codes when `onDone` fires, and the server keeps only hashes. */
export function RecoveryCodes({ codes, onDone }: { codes: string[]; onDone: () => void }) {
  const [copied, setCopied] = useState<boolean | null>(null);

  async function copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(codes.join("\n"));
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  return (
    <div className="card">
      <h2 className="flush-top">Your recovery codes</h2>
      <p>
        If you lose your phone, each of these codes signs you in once instead of the
        authenticator app. Save them somewhere safe now, such as a password manager or a printed
        copy kept locked away. <strong>They won't be shown again.</strong>
      </p>
      <ol className="code-list mono" aria-label="Recovery codes">
        {codes.map((code) => (
          <li key={code}>{code}</li>
        ))}
      </ol>
      <div className="button-row">
        <button type="button" onClick={() => void copy()}>
          Copy codes
        </button>
        <button className="primary" type="button" onClick={onDone}>
          I've saved them — continue
        </button>
      </div>
      {copied === true && (
        <p className="row-sub" role="status">
          Copied.
        </p>
      )}
      {copied === false && (
        <p className="field-error" role="alert">
          This browser wouldn't copy them. Write them down or select the list instead.
        </p>
      )}
    </div>
  );
}
