import { useState, type FormEvent } from "react";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import { getSchoolSettings } from "../api/resources";
import type { IssuedSetupLink, LocalAccount } from "../api/types";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import {
  changeLocalAccount,
  createLocalAccount,
  listLocalAccounts,
  localAccountKeys,
  resetLocalAccount,
  setupLinkUrl,
  type AccountChange,
} from "../auth/localAccounts";
import { hasRole, useSession } from "../auth/session";
import { QrCode } from "../features/accounts/QrCode";
import { SignInOffer, useAccessGate } from "../features/ui/SignInOffer";
import { href } from "../router";
import { showToast } from "../shell/toast";
import { useConnectivity } from "../shell/useOnline";

// Accounts are disabled, never deleted (ADR-0065 §4): history stays attributable.

const when = (iso: string): string => new Date(iso).toLocaleString();

export default function SchoolPeopleScreen() {
  const session = useSession();
  const isAdmin = hasRole(session, "SystemAdministrator");
  const gate = useAccessGate(isAdmin);
  const offline = useConnectivity() !== "connected";
  const accounts = useApiQuery(localAccountKeys.localAccounts(), listLocalAccounts, {
    enabled: isAdmin,
  });
  const settings = useApiQuery(keys.schoolSettings(), getSchoolSettings, { enabled: isAdmin });
  const invalidate = useInvalidate();
  const [issued, setIssued] = useState<IssuedSetupLink | null>(null);
  const [username, setUsername] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [adding, setAdding] = useState(false);
  const [addError, setAddError] = useState<string | null>(null);

  const refresh = () => invalidate(localAccountKeys.localAccounts());
  const access = settings.data;
  const adminsOnly = access?.providerConfigured === true && access.localAccounts !== "Everyone";

  async function add(e: FormEvent): Promise<void> {
    e.preventDefault();
    if (adding || username.trim() === "" || displayName.trim() === "") return;
    setAdding(true);
    setAddError(null);
    try {
      setIssued(await createLocalAccount(username.trim(), displayName.trim()));
      setUsername("");
      setDisplayName("");
      refresh();
    } catch (err) {
      setAddError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setAdding(false);
    }
  }

  if (gate === "loading") return <p>Loading…</p>;
  if (gate === "signedOut") {
    return (
      <SignInOffer title="People">
        <p>Sign in to manage the school's local accounts.</p>
      </SignInOffer>
    );
  }
  if (gate === "refused") {
    return (
      <>
        <h1>People</h1>
        <div className="alert error" role="alert">
          Managing local accounts needs the system administrator role.
        </div>
        <p>
          <a href={href("/carnivals")}>← Back to carnivals</a>
        </p>
      </>
    );
  }

  const rows = accounts.data ?? [];

  return (
    <>
      <p>
        <a href={href("/school")}>← School</a>
      </p>
      <h1>People</h1>
      <p className="row-sub">
        Accounts that sign in with a username and password here. Add someone, hand them their
        setup link, then give them a role on the Roles page.
      </p>
      {adminsOnly && (
        <div className="alert notice" role="status">
          Beside the school's own sign-in, only system administrators may use a local account.
          Anyone else added here can't sign in until School settings allows everyone.
        </div>
      )}
      {offline && (
        <div className="alert notice" role="status">
          Can’t reach the server, so accounts can’t be changed until you reconnect.
        </div>
      )}
      {accounts.error && (
        <div className="alert error" role="alert">
          {errorText(accounts.error, "Can’t reach the server, so the accounts couldn’t be read.")}
        </div>
      )}

      {issued && <SetupLinkPanel issued={issued} onDone={() => setIssued(null)} />}

      {accounts.loading ? (
        <p>Loading…</p>
      ) : rows.length === 0 ? (
        <p className="row-sub">No local accounts yet.</p>
      ) : (
        <ul className="row-list card">
          {rows.map((row) => (
            <AccountRow
              key={row.id}
              row={row}
              self={session?.subject === `local:${row.id}`}
              writable={!offline}
              onChanged={refresh}
              onIssued={setIssued}
            />
          ))}
        </ul>
      )}

      <form className="card settings-form" onSubmit={(e) => void add(e)}>
        <h2>Add a person</h2>
        <div className="form-grid">
          <label className="field">
            <span>Username</span>
            <input
              value={username}
              maxLength={64}
              autoComplete="off"
              autoCapitalize="none"
              spellCheck={false}
              placeholder="e.g. jsmith"
              onChange={(e) => setUsername(e.target.value)}
            />
          </label>
          <label className="field">
            <span>Display name</span>
            <input
              value={displayName}
              maxLength={100}
              autoComplete="off"
              placeholder="e.g. Jo Smith"
              onChange={(e) => setDisplayName(e.target.value)}
            />
          </label>
        </div>
        <p className="row-sub">
          A plain name, never an email address: lower-case letters, digits, dots, dashes and underscores. The
          app makes a setup link, valid 24 hours, for you to pass on in person or by message.
        </p>
        {addError && (
          <div className="alert error" role="alert">
            {addError}
          </div>
        )}
        <div className="button-row">
          <button
            className="primary"
            type="submit"
            disabled={adding || offline || username.trim() === "" || displayName.trim() === ""}
          >
            {adding ? "Adding…" : "Add person"}
          </button>
        </div>
      </form>
    </>
  );
}

/** Shown once: the token is dropped from memory when the administrator is done with it. */
function SetupLinkPanel({ issued, onDone }: { issued: IssuedSetupLink; onDone: () => void }) {
  const link = setupLinkUrl(issued.setupToken);
  const [copied, setCopied] = useState<boolean | null>(null);

  async function copy(): Promise<void> {
    try {
      await navigator.clipboard.writeText(link);
      setCopied(true);
    } catch {
      setCopied(false);
    }
  }

  return (
    <section className="card" aria-label={`Setup link for ${issued.account.displayName}`}>
      <h2 className="flush-top">Setup link for {issued.account.displayName}</h2>
      <p>
        Give this to <strong>{issued.account.displayName}</strong> ({issued.account.username}).
        Opening it lets them choose a password. It works once and expires{" "}
        {when(issued.expiresAtUtc)}. It is shown only now.
      </p>
      <label className="field wide">
        <span>Setup link</span>
        <input value={link} readOnly onFocus={(e) => e.currentTarget.select()} />
      </label>
      <QrCode value={link} label={`QR code of the setup link for ${issued.account.displayName}`} />
      <div className="button-row">
        <button type="button" onClick={() => void copy()}>
          Copy link
        </button>
        <button type="button" onClick={onDone}>
          Done
        </button>
      </div>
      {copied === true && (
        <p className="row-sub" role="status">
          Copied.
        </p>
      )}
      {copied === false && (
        <p className="field-error" role="alert">
          This browser wouldn't copy it. Select the link and copy it by hand.
        </p>
      )}
    </section>
  );
}

function statusBadge(row: LocalAccount) {
  if (row.status === "Disabled") return <span className="badge muted">disabled</span>;
  if (row.status === "AwaitingSetup") return <span className="badge info">awaiting setup</span>;
  return null;
}

function setupLinkLine(row: LocalAccount): string | null {
  const link = row.setupLink;
  if (!link) return null;
  if (link.usedAtUtc) {
    const from = link.usedFromAddress ? ` from ${link.usedFromAddress}` : "";
    return `Setup link used ${when(link.usedAtUtc)}${from}`;
  }
  return new Date(link.expiresAtUtc).getTime() < Date.now()
    ? `Setup link expired ${when(link.expiresAtUtc)}`
    : `Setup link valid until ${when(link.expiresAtUtc)}`;
}

function AccountRow({
  row,
  self,
  writable,
  onChanged,
  onIssued,
}: {
  row: LocalAccount;
  self: boolean;
  writable: boolean;
  onChanged: () => void;
  onIssued: (issued: IssuedSetupLink) => void;
}) {
  const [armed, setArmed] = useState<"reset" | "disable" | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const person = row.displayName || row.username;
  const disabled = row.status === "Disabled";
  const resetting = armed === "reset";

  async function act(work: () => Promise<void>): Promise<void> {
    setBusy(true);
    setError(null);
    try {
      await work();
      setArmed(null);
      onChanged();
    } catch (err) {
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setBusy(false);
    }
  }

  const change = (kind: AccountChange, toast: string) =>
    act(async () => {
      await changeLocalAccount(row.id, kind);
      showToast(toast);
    });

  const meta = [
    row.isSystemAdministrator ? "System administrator" : null,
    row.status === "AwaitingSetup"
      ? null
      : row.authenticatorEnrolled
        ? "Authenticator app set up"
        : "No authenticator app",
    row.lastSignInAtUtc ? `Last signed in ${when(row.lastSignInAtUtc)}` : "Never signed in",
  ].filter(Boolean);

  return (
    <li>
      <div className="entry-row">
        <span className="row-main">
          <span className={`row-title${disabled ? " voided" : ""}`}>
            {person}
            {self && " (you)"}
          </span>
          <span className="row-sub">{row.username}</span>
          <span className="row-sub">{meta.join(" · ")}</span>
          {setupLinkLine(row) && <span className="row-sub">{setupLinkLine(row)}</span>}
          {self && (
            <span className="row-sub">
              Change your own password on <a href={href("/account")}>My account</a>.
            </span>
          )}
        </span>
        <span className="role-status">
          {statusBadge(row)}
          {row.lockedOut && <span className="badge warn">locked out</span>}
          {!self && armed === null && (
            <>
              {row.lockedOut && (
                <button
                  type="button"
                  disabled={busy || !writable}
                  aria-label={`Clear lockout for ${person}`}
                  onClick={() => void change("clear-lockout", `Lockout cleared for ${person}`)}
                >
                  Clear lockout
                </button>
              )}
              {disabled ? (
                <button
                  type="button"
                  disabled={busy || !writable}
                  aria-label={`Enable ${person}`}
                  onClick={() => void change("enable", `${person} enabled`)}
                >
                  Enable
                </button>
              ) : (
                <>
                  <button
                    type="button"
                    disabled={busy || !writable}
                    aria-label={`Reset ${person}`}
                    onClick={() => setArmed("reset")}
                  >
                    Reset…
                  </button>
                  <button
                    type="button"
                    className="danger"
                    disabled={busy || !writable}
                    aria-label={`Disable ${person}`}
                    onClick={() => setArmed("disable")}
                  >
                    Disable…
                  </button>
                </>
              )}
            </>
          )}
        </span>
      </div>
      {armed && (
        <div className="row-armed" role="alert">
          <span>
            {resetting
              ? `Reset ${person}? This clears their password and authenticator app, signs them ` +
                "out everywhere and makes a new setup link."
              : `Disable ${person}? They are signed out at their next request and can't ` +
                "sign in until enabled again."}
          </span>
          <button
            type="button"
            className="danger armed"
            disabled={busy || !writable}
            aria-label={resetting ? `Confirm resetting ${person}` : `Confirm disabling ${person}`}
            onClick={() =>
              void (resetting
                ? act(async () => onIssued(await resetLocalAccount(row.id)))
                : change("disable", `${person} disabled`))
            }
          >
            {busy ? "Working…" : resetting ? "Confirm reset" : "Confirm disable"}
          </button>
          <button type="button" disabled={busy} onClick={() => setArmed(null)}>
            Cancel
          </button>
        </div>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
    </li>
  );
}
