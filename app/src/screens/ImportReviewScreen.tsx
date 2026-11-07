import { useState } from "react";
import { ApiError, isDemoBuild } from "../api/client";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import {
  approveImport,
  getCarnival,
  getImportDefaults,
  getImportReconciliation,
  getImportValidation,
  listImportBatches,
  promoteImport,
  stageImport,
} from "../api/resources";
import { useApiQuery, useInvalidate } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import { BatchList, batchLabel } from "../features/imports/BatchList";
import { agesAsAt } from "../features/imports/cutOff";
import { ReconciliationSection } from "../features/imports/ReconciliationSection";
import { ValidationSection } from "../features/imports/ValidationSection";
import { SignInOffer, useAccessGate } from "../features/ui/SignInOffer";
import { href } from "../router";
import { showToast } from "../shell/toast";

const LAST_BATCH_KEY = "carnival.lastImportBatchId";

export default function ImportReviewScreen({ carnivalId }: { carnivalId: string | null }) {
  const session = useSession();
  const canManage = hasRole(session, "SystemAdministrator");
  // The server allows even `imports:view` only to a SystemAdministrator; an open client gate would
  // show anyone else a failing batch list that reads as an outage.
  const canView = canManage;
  const gate = useAccessGate(canView);

  const [batchId, setBatchId] = useState<string | null>(() =>
    localStorage.getItem(LAST_BATCH_KEY),
  );
  const [uploading, setUploading] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  // Structural errors block promotion, so that one stays on screen rather than toasting.
  const [stageBlocked, setStageBlocked] = useState<string | null>(null);
  const [busyAction, setBusyAction] = useState<string | null>(null);
  const [promotedId, setPromotedId] = useState<string | null>(null);
  const [approvedId, setApprovedId] = useState<string | null>(null);

  const batches = useApiQuery(keys.imports(), () => listImportBatches(), { enabled: canView });
  const defaults = useApiQuery(keys.importDefaults(), () => getImportDefaults(), {
    enabled: canManage,
  });
  const carnival = useApiQuery(keys.carnival(carnivalId ?? ""), () => getCarnival(carnivalId!), {
    enabled: canManage && carnivalId !== null,
  });
  // A failed carnival read falls back to the server's default rather than holding the form.
  const carnivalPending = carnivalId !== null && carnival.loading;
  const carnivalYear = carnival.data ? Number(carnival.data.date.slice(0, 4)) : undefined;
  const carnivalYearAllowed =
    carnivalYear !== undefined &&
    defaults.data !== undefined &&
    carnivalYear >= defaults.data.earliestYear &&
    carnivalYear <= defaults.data.latestYear;
  // Null until the organiser picks one, so a late defaults read still fills the field.
  const [chosenYear, setChosenYear] = useState<number | null>(null);
  const year =
    chosenYear ?? (carnivalYearAllowed ? carnivalYear : defaults.data?.competitionYear);
  // Both reports are per-batch; with no batch selected the query is parked
  // rather than resolved with undefined, so no cache entry is created for it.
  const validation = useApiQuery(
    keys.importValidation(batchId ?? ""),
    () => getImportValidation(batchId!),
    { enabled: canView && batchId !== null },
  );
  const reconciliation = useApiQuery(
    keys.importReconciliation(batchId ?? ""),
    () => getImportReconciliation(batchId!),
    { enabled: canView && batchId !== null },
  );
  const invalidate = useInvalidate();

  function selectBatch(id: string) {
    localStorage.setItem(LAST_BATCH_KEY, id);
    setBatchId(id);
    setActionError(null);
    setStageBlocked(null);
  }

  async function onUpload(fileList: FileList | null) {
    const file = fileList?.[0];
    if (!file) return;
    setUploading(true);
    setActionError(null);
    try {
      const staged = await stageImport(file, year);
      selectBatch(staged.id);
      invalidate(keys.imports());
      if (staged.structuralErrors.length > 0) {
        setStageBlocked(
          `Staged with ${staged.structuralErrors.length} structural error(s) — this batch cannot be promoted.`,
        );
      } else {
        showToast(
          `Staged '${file.name}' for ${staged.competitionYear} — review below, then update the ` +
            "student list.",
        );
      }
    } catch (err) {
      // A direct write: the "saved on this device" default would be false.
      setActionError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setUploading(false);
    }
  }

  // ADR-0066 §8: two calls, so the batch still passes through Approved and both audit events land.
  async function approveAndPromote(id: string) {
    setActionError(null);
    setBusyAction("update");
    let approved = false;
    try {
      await approveImport(id, true);
      approved = true;
      setApprovedId(id);
      await promoteImport(id);
      promoted(id);
    } catch (err) {
      setActionError(
        approved
          ? "The batch is approved, but the student list was not updated. " +
              `${errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT)} Press Promote to try again.`
          : errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT),
      );
    } finally {
      invalidate(keys.imports());
      setBusyAction(null);
    }
  }

  function promoted(id: string) {
    setPromotedId(id);
    showToast("The student list is updated.");
    // The roster is school-wide: every carnival's checklist and age reads move with it.
    invalidate(keys.carnivals());
  }

  async function run(name: string, action: () => Promise<unknown>, doneMessage?: string) {
    setActionError(null);
    // One-way batch transitions: a double tap on a slow connection must not fire the second.
    setBusyAction(name);
    try {
      await action();
      if (doneMessage) showToast(doneMessage);
      // Prefix key: approve/reject/promote move the batch's status, which both
      // per-batch reports carry as well as the list.
      invalidate(keys.imports());
    } catch (err) {
      setActionError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
    } finally {
      setBusyAction(null);
    }
  }

  if (gate === "loading") return <p>Loading…</p>;
  if (gate === "signedOut") {
    return (
      <SignInOffer title="Roster import">
        <p>Sign in to upload and review roster imports.</p>
      </SignInOffer>
    );
  }

  const reportedStatus = validation.data?.status ?? reconciliation.data?.status;
  // Until the re-read lands, a batch approved here must offer only Promote, never approve again.
  const status =
    reportedStatus === "Validated" && approvedId === batchId ? "Approved" : reportedStatus;
  const expired =
    reconciliation.error instanceof ApiError && reconciliation.error.code === "IMPORT_BATCH_EXPIRED";
  const selected = batches.data?.find((b) => b.id === batchId);
  const justPromoted = batchId !== null && promotedId === batchId;

  return (
    <>
      <p>
        {carnivalId ? (
          <a href={href(`/carnivals/${carnivalId}/manage`)}>← Manage</a>
        ) : (
          <a href={href("/school")}>← School</a>
        )}
      </p>
      <h1>Roster import</h1>
      {!canView && (
        <div className="alert notice">
          Import review is available to system administrators. You are signed in
          {session ? ` as ${session.displayName} (${session.roles.join(", ") || "no roles"})` : " with no session"}.
        </div>
      )}

      <div className="card">
        <h2 className="flush-top">Upload roster export (CSV)</h2>
        {canManage ? (
          <>
            {defaults.data && year !== undefined && !carnivalPending ? (
              <div className="form-grid">
                <label className="field">
                  <span>Competition year</span>
                  <select
                    value={year}
                    disabled={uploading}
                    onChange={(e) => setChosenYear(Number(e.target.value))}
                  >
                    {yearsBetween(defaults.data.earliestYear, defaults.data.latestYear).map((y) => (
                      <option key={y} value={y}>
                        {y}
                      </option>
                    ))}
                  </select>
                </label>
                <p className="row-sub field-note">
                  Ages as at {agesAsAt(defaults.data.cutOff, year)}. Choose the year of the
                  carnival this roster is for.
                </p>
                {carnivalYear !== undefined && !carnivalYearAllowed && (
                  <p className="row-sub field-note">
                    This carnival is in {carnivalYear}; a roster can be imported for{" "}
                    {defaults.data.earliestYear}–{defaults.data.latestYear} only.
                  </p>
                )}
              </div>
            ) : (
              <p className="row-sub">
                {defaults.error
                  ? "The competition year couldn’t be read, so the server will choose it. The " +
                    "review below states the year it used."
                  : "Reading the competition year…"}
              </p>
            )}
            <label className="field">
              <span>Student list: a CSV export from your student system</span>
              <input
                type="file"
                accept=".csv,text/csv"
                disabled={uploading || carnivalPending}
                onChange={(e) => {
                  void onUpload(e.target.files);
                  e.target.value = "";
                }}
              />
            </label>
            {(import.meta.env.DEV || isDemoBuild()) && (
              <p className="row-sub">Synthetic or de-identified data only in this build.</p>
            )}
          </>
        ) : (
          <p className="row-sub">Uploading requires the SystemAdministrator role.</p>
        )}
      </div>

      <BatchList state={batches} selectedId={batchId} onSelect={selectBatch} />

      {actionError && (
        <div className="alert error" role="alert">
          {actionError}
        </div>
      )}
      {stageBlocked && <div className="alert notice">{stageBlocked}</div>}

      {batchId && (
        <>
          <div className="card">
            <strong>
              {selected
                ? batchLabel(selected)
                : (validation.data?.sourceFileName ?? "Selected batch")}
            </strong>{" "}
            {status && <span className="badge info">{status}</span>}
            {canManage && justPromoted && carnivalId && (
              <div className="button-row">
                <a className="button primary-link" href={href(`/carnivals/${carnivalId}/manage`)}>
                  Back to {carnival.data?.name ?? "the carnival"}
                </a>
              </div>
            )}
            {canManage && !justPromoted && status !== "Promoted" && (
              <div className="button-row">
                {status === "Approved" ? (
                  <button
                    className="primary"
                    disabled={expired || busyAction !== null}
                    onClick={() =>
                      void run("promote", async () => {
                        await promoteImport(batchId);
                        promoted(batchId);
                      })
                    }
                  >
                    {busyAction === "promote" ? "Promoting…" : "Promote"}
                  </button>
                ) : (
                  <>
                    <button
                      className="primary"
                      disabled={status !== "Validated" || expired || busyAction !== null}
                      onClick={() => void approveAndPromote(batchId)}
                    >
                      {busyAction === "update"
                        ? "Updating the student list…"
                        : "Approve and update the student list"}
                    </button>
                    <button
                      className="danger"
                      disabled={status !== "Validated" || busyAction !== null}
                      onClick={() =>
                        void run(
                          "reject",
                          () => approveImport(batchId, false, "Rejected in review"),
                          "Batch rejected.",
                        )
                      }
                    >
                      {busyAction === "reject" ? "Rejecting…" : "Reject"}
                    </button>
                  </>
                )}
              </div>
            )}
          </div>

          <ValidationSection state={validation} />
          <ReconciliationSection state={reconciliation} />
        </>
      )}
    </>
  );
}

function yearsBetween(earliest: number, latest: number): number[] {
  return Array.from({ length: Math.max(0, latest - earliest + 1) }, (_, i) => earliest + i);
}
