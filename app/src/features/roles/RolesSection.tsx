import { Fragment, useState, type FormEvent } from "react";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../../api/errorText";
import { keys } from "../../api/queryKeys";
import { grantRole, listRoleAssignments, revokeRole } from "../../api/resources";
import type { PlatformRole, RoleAssignment } from "../../api/types";
import { useApiQuery, useInvalidate } from "../../api/useApiQuery";
import { showToast } from "../../shell/toast";

/**
 * Assignment takes a UPN because a department-tenant administrator cannot look up an object id.
 * Layered over the Entra group mapping: a group-derived role is not a row here.
 */

const ROLES: readonly { value: PlatformRole; label: string; note: string }[] = [
  {
    value: "SystemAdministrator",
    label: "System administrator",
    note: "School settings, roster imports and roles, plus everything a carnival administrator can do.",
  },
  {
    value: "CarnivalAdministrator",
    label: "Carnival administrator",
    note: "Runs a carnival: events, entries, officials, races, results and finalisation.",
  },
  { value: "Recorder", label: "Recorder", note: "Marshals and records on the deck." },
  { value: "Viewer", label: "Viewer", note: "Read-only." },
];

const UNBOUND_NOTE = "has not signed in yet";

function roleLabel(role: PlatformRole): string {
  return ROLES.find((r) => r.value === role)?.label ?? role;
}

function personLabel(row: RoleAssignment): string {
  return row.displayName?.trim() || row.upn;
}

/** The calendar day of an ISO stamp in the reader's zone. */
function dayOf(isoTimestamp: string | null): string | null {
  return isoTimestamp === null ? null : new Date(isoTimestamp).toLocaleDateString();
}

export default function RolesSection({ offline }: { offline: boolean }) {
  const assignments = useApiQuery(keys.roleAssignments(), listRoleAssignments);
  const invalidate = useInvalidate();
  const [upn, setUpn] = useState("");
  const [role, setRole] = useState<PlatformRole>("Recorder");
  const [reason, setReason] = useState("");
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const rows = assignments.data ?? [];
  const complete = upn.trim() !== "" && reason.trim() !== "";

  async function assign(e: FormEvent): Promise<void> {
    e.preventDefault();
    if (saving || !complete) return;
    setSaving(true);
    setError(null);
    try {
      await grantRole({ upn: upn.trim(), role, reason: reason.trim() });
      invalidate(keys.roleAssignments());
      showToast(`${roleLabel(role)} assigned to ${upn.trim()}`);
      setUpn("");
      setReason("");
    } catch (err) {
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="subsection">
      <p className="row-sub">
        Who holds which role in this app. A role granted through the school's sign-in groups is
        not listed here and is unaffected by anything on this screen — the two sources add up.
        A change takes effect on that person's next request; they do not need to sign in again.
      </p>

      {assignments.error && (
        <div className="alert error" role="alert">
          {errorText(assignments.error, "Can’t reach the server, so the roles couldn’t be read.")}
        </div>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}

      {assignments.loading ? (
        <p>Loading…</p>
      ) : rows.length === 0 ? (
        <p className="row-sub">
          Nobody has been given a role in the app yet. Roles from the school's sign-in groups
          still apply.
        </p>
      ) : (
        <ul className="row-list card">
          {rows.map((row) => (
            <RoleRow
              key={row.id ?? `bootstrap:${row.upn}`}
              row={row}
              writable={!offline}
              onRevoked={() => invalidate(keys.roleAssignments())}
            />
          ))}
        </ul>
      )}

      <form className="card settings-form" onSubmit={(e) => void assign(e)}>
        <h2>Assign a role</h2>
        <div className="form-grid">
          <label className="field wide">
            <span>Sign-in address (UPN or email)</span>
            <input
              id="role-upn"
              value={upn}
              maxLength={256}
              autoComplete="off"
              placeholder="name@school.vic.edu.au"
              onChange={(e) => setUpn(e.target.value)}
            />
          </label>
          <label className="field">
            <span>Role</span>
            <select
              id="role-role"
              // Explicit: a wrapping label's accessible name would otherwise
              // absorb the selected option's text ("RoleRecorder").
              aria-label="Role"
              value={role}
              onChange={(e) => setRole(e.target.value as PlatformRole)}
            >
              {ROLES.map((option) => (
                <option key={option.value} value={option.value}>
                  {option.label}
                </option>
              ))}
            </select>
          </label>
        </div>
        <p className="row-sub">{ROLES.find((r) => r.value === role)?.note}</p>
        <label className="field wide">
          <span>Reason (recorded on the audit trail)</span>
          <input
            id="role-reason"
            value={reason}
            maxLength={500}
            placeholder="e.g. timing desk for the swimming carnival"
            onChange={(e) => setReason(e.target.value)}
          />
        </label>
        <p className="row-sub">
          The person does not need to have signed in yet. Their account is matched the first
          time they do.
        </p>
        <div className="button-row">
          <button className="primary" type="submit" disabled={saving || !complete || offline}>
            {saving ? "Assigning…" : "Assign role"}
          </button>
        </div>
      </form>
    </section>
  );
}

function RoleRow({
  row,
  writable,
  onRevoked,
}: {
  row: RoleAssignment;
  writable: boolean;
  onRevoked: () => void;
}) {
  const [armed, setArmed] = useState(false);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const person = personLabel(row);
  const revoked = row.revokedAtUtc !== null;

  function disarm(): void {
    setArmed(false);
    setReason("");
    setError(null);
  }

  async function confirmRevoke(): Promise<void> {
    if (row.id === null) return;
    setBusy(true);
    setError(null);
    try {
      await revokeRole(row.id, reason.trim());
      showToast(`${roleLabel(row.role)} revoked for ${person}`);
      disarm();
      onRevoked();
    } catch (err) {
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setBusy(false);
    }
  }

  const assignedOn = dayOf(row.assignedAtUtc);
  const revokedOn = dayOf(row.revokedAtUtc);

  return (
    <li>
      <div className="entry-row">
        <span className="row-main">
          <span className={`row-title${revoked ? " voided" : ""}`}>
            {person} · {roleLabel(row.role)}
          </span>
          {row.displayName?.trim() && <span className="row-sub">{row.upn}</span>}
          <span className="row-sub">
            {row.bootstrap
              ? "From the server configuration — it cannot be changed here."
              : [
                  row.assignedBySubject ? `Assigned by ${row.assignedBySubject}` : "Assigned",
                  assignedOn,
                ]
                  .filter(Boolean)
                  .join(" · ")}
          </span>
          {row.reason && <span className="row-sub">{row.reason}</span>}
          {revoked && (
            <span className="row-sub">
              Revoked{revokedOn ? ` ${revokedOn}` : ""}
              {row.revokedBySubject ? ` by ${row.revokedBySubject}` : ""}
              {row.revocationReason ? `: ${row.revocationReason}` : ""}
            </span>
          )}
        </span>
        <span className="role-status">
          {row.bootstrap && <span className="badge muted">from configuration</span>}
          {revoked && <span className="badge muted">revoked</span>}
          {!row.bootstrap && !revoked && !row.boundToSubject && (
            <span className="badge warn">{UNBOUND_NOTE}</span>
          )}
          {!row.bootstrap && !revoked && !armed && (
            <button
              type="button"
              className="danger"
              disabled={!writable}
              aria-label={`Revoke ${roleLabel(row.role)} for ${person}`}
              onClick={() => setArmed(true)}
            >
              Revoke…
            </button>
          )}
        </span>
      </div>
      {armed && (
        <Fragment>
          <form
            className="row-armed role-revoke"
            role="alert"
            onSubmit={(e) => {
              e.preventDefault();
              void confirmRevoke();
            }}
          >
            <span>
              Revoke {roleLabel(row.role)} for {person}? They lose it on their next request.
              Anything they queued offline is refused, so let their device finish syncing first.
            </span>
            {/* The typed reason is the second step: no stray tap writes a sentence. */}
            <label className="field wide flush-bottom">
              <span>Reason for revoking (recorded on the audit trail)</span>
              <input
                value={reason}
                maxLength={500}
                aria-label={`Reason for revoking ${roleLabel(row.role)} for ${person}`}
                placeholder="e.g. left the school at the end of term"
                autoFocus
                onChange={(e) => setReason(e.target.value)}
              />
            </label>
            <button
              type="submit"
              className="danger armed"
              disabled={busy || !writable || reason.trim() === ""}
              aria-label={`Confirm revoking ${roleLabel(row.role)} for ${person}`}
            >
              {busy ? "Revoking…" : "Confirm revoke"}
            </button>
            <button type="button" disabled={busy} onClick={disarm}>
              Cancel
            </button>
          </form>
          {error && (
            <div className="alert error" role="alert">
              {error}
            </div>
          )}
        </Fragment>
      )}
    </li>
  );
}
