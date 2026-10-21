import { Fragment, useState } from "react";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import { getRecordsForAdministration, voidRecord } from "../api/resources";
import type { SchoolRecord } from "../api/types";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import { SignInOffer, useAccessGate } from "../features/ui/SignInOffer";
import { href } from "../router";
import { showToast } from "../shell/toast";
import { useConnectivity } from "../shell/useOnline";

// Voiding withdraws a record, never deletes it: the voided row and its reason are the history.
export default function RecordsAdminScreen() {
  const session = useSession();
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator");
  const gate = useAccessGate(canManage);
  const connectivity = useConnectivity();
  const offline = connectivity !== "connected";
  const records = useApiQuery(keys.recordsAdministration(), () => getRecordsForAdministration(), {
    enabled: canManage,
  });

  if (gate === "loading") return <p>Loading…</p>;
  if (gate === "signedOut") {
    return (
      <SignInOffer title="School records">
        <p>Sign in to administer the record book.</p>
      </SignInOffer>
    );
  }
  if (gate === "refused") {
    return (
      <>
        <h1>School records</h1>
        <div className="alert error" role="alert">
          Administering the record book needs the carnival administrator role.
        </div>
        <p>
          <a href={href("/carnivals")}>← Back to carnivals</a>
        </p>
      </>
    );
  }

  const groups = groupByEvent(records.data);

  return (
    <>
      <p>
        <a href={href("/school")}>← School</a>
      </p>
      <h1>School records</h1>
      <p className="row-sub">
        Every record the school's finalised carnivals have set. Records are minted by
        finalisation and by nothing else, so this screen cannot add or edit one — it can only
        withdraw a record that should never have been set.
      </p>
      <p className="row-sub">
        Voiding does not change the result behind the record. A finalised carnival's results
        book still shows the swim as it was recorded; what changes is the record book.
      </p>

      {offline && (
        <div className="alert notice" role="status">
          Can’t reach the server, so the record book is read-only for now — this screen keeps no
          offline copy. Reconnect and the actions come back.
        </div>
      )}
      {records.error && (
        <div className="alert error" role="alert">
          {errorText(records.error, "Can’t reach the server, so the record book couldn’t be read.")}
        </div>
      )}

      {records.loading ? (
        <p>Loading…</p>
      ) : groups.length === 0 ? (
        <p className="row-sub">
          No records yet. The first finalised carnival seeds the book.
        </p>
      ) : (
        groups.map((group) => (
          <section className="subsection" key={group.eventCode}>
            <h2>
              {group.eventCode} · {group.eventName}
            </h2>
            <ul className="row-list card">
              {group.records.map((record) => (
                <RecordRow key={record.id} record={record} writable={!offline} />
              ))}
            </ul>
          </section>
        ))
      )}
    </>
  );
}

interface EventGroup {
  eventCode: string;
  eventName: string;
  records: SchoolRecord[];
}

/** The server orders the list; grouping only folds its runs, never re-sorts. */
function groupByEvent(records: SchoolRecord[] | undefined): EventGroup[] {
  const groups: EventGroup[] = [];
  for (const record of records ?? []) {
    const last = groups.at(-1);
    if (last?.eventCode === record.eventCode) last.records.push(record);
    else
      groups.push({
        eventCode: record.eventCode,
        eventName: record.eventName,
        records: [record],
      });
  }
  return groups;
}

function RecordRow({ record, writable }: { record: SchoolRecord; writable: boolean }) {
  const invalidate = useInvalidate();
  const [armed, setArmed] = useState(false);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function disarm(): void {
    setArmed(false);
    setReason("");
    setError(null);
  }

  async function confirmVoid(): Promise<void> {
    setBusy(true);
    setError(null);
    try {
      await voidRecord(record.id, reason.trim());
      // Both reads change: this list, and the book the deck badge indexes.
      invalidate(keys.recordsAll());
      showToast("Record voided.");
      disarm();
    } catch (err) {
      setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setBusy(false);
    }
  }

  return (
    <li>
      <div className="entry-row">
        <span className="row-main">
          <span className={`row-title${record.voided ? " voided" : ""}`}>
            {record.division} · {record.formattedValue} · {record.holderDisplayName}
          </span>
          <span className="row-sub">
            {[record.carnivalName, record.setOn].filter(Boolean).join(" · ")}
          </span>
          {record.voided && (
            <span className="row-sub">
              Voided{record.voidedAtUtc ? ` ${dayOf(record.voidedAtUtc)}` : ""}
              {record.voidedBySubject ? ` by ${record.voidedBySubject}` : ""}
              {record.voidReason ? `: ${record.voidReason}` : ""}
            </span>
          )}
        </span>
        {!record.voided && !armed && (
          <button type="button" className="danger" disabled={!writable} onClick={() => setArmed(true)}>
            Void…
          </button>
        )}
        {record.voided && <span className="badge muted">voided</span>}
      </div>
      {armed && (
        <Fragment>
          <form
            className="row-armed"
            role="alert"
            onSubmit={(e) => {
              e.preventDefault();
              void confirmVoid();
            }}
          >
            <span>
              Void the {record.division} record of {record.formattedValue}? It leaves the record
              book and cannot be un-voided. The result itself is unchanged.
            </span>
            {/* The typed reason is the second step: a stray tap cannot write a sentence, so no
                arming lockout or auto-disarm. */}
            <label className="field wide flush-bottom">
              <span>Reason for voiding (recorded on the audit trail)</span>
                <input
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                placeholder="e.g. timekeeper's sheet misread — the swim was 30.01"
                autoFocus
              />
            </label>
            <button
              type="submit"
              className="danger armed"
              disabled={busy || !writable || reason.trim() === ""}
              aria-label={`Confirm voiding the ${record.division} record`}
            >
              {busy ? "Voiding…" : "Confirm void"}
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

// The reader's zone: slicing the UTC string puts an 08:00 AEST void on the previous day.
function dayOf(isoTimestamp: string): string {
  return new Date(isoTimestamp).toLocaleDateString();
}
