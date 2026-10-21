import { useMemo, useState } from "react";
import { ApiError } from "../../api/client";
import { IMPORT_BATCH_EXPIRED_TEXT } from "../../api/errorText";
import type { ReconciliationReport, RowDisposition, StagedRow } from "../../api/types";
import type { ApiState } from "../../api/useApiQuery";
import { agesAsAt } from "./cutOff";

const DISPOSITION_ORDER: RowDisposition[] = ["Rejected", "New", "Changed", "Inactive", "Unchanged"];

/** Staged rows with their disposition — the pre-promotion preview. */
export function ReconciliationSection({
  state,
}: {
  state: ApiState<ReconciliationReport>;
}) {
  const [filter, setFilter] = useState<RowDisposition | "all">("all");
  const report = state.data;
  const rows = useMemo(() => {
    if (!report) return [];
    const filtered = filter === "all" ? report.rows : report.rows.filter((r) => r.disposition === filter);
    return [...filtered].sort(
      (a, b) =>
        DISPOSITION_ORDER.indexOf(a.disposition) - DISPOSITION_ORDER.indexOf(b.disposition)
        || a.rowNumber - b.rowNumber,
    );
  }, [report, filter]);

  if (state.loading) return <p>Loading reconciliation…</p>;
  // Promotion purges the staged rows: that 409 is a finished state, not a failure — the counts
  // and the audit row are the durable record.
  if (state.error instanceof ApiError && state.error.code === "IMPORT_ROWS_PURGED") {
    return <div className="alert notice">Reconciliation preview: {state.error.message}</div>;
  }
  if (state.error instanceof ApiError && state.error.code === "IMPORT_BATCH_EXPIRED") {
    return <div className="alert notice">{IMPORT_BATCH_EXPIRED_TEXT}</div>;
  }
  // Other 409 = staged rows absent (validation failed at intake); message explains.
  // With a preview already cached the error goes above it, not over it.
  const failed = state.error ? (
    <div className="alert error">Reconciliation preview: {state.error.message}</div>
  ) : null;
  if (!report) return failed;

  const { summary } = report;
  return (
    <>
      <h2>Reconciliation preview — competition year {report.competitionYear}</h2>
      {failed}
      <p>
        Ages as at <strong>{agesAsAt(report.cutOff, report.competitionYear)}</strong>.
      </p>
      <p className="row-sub">
        Wrong year? Reject this batch and upload the file again with the carnival’s year.
      </p>
      <div className="button-row" role="group" aria-label="Filter rows by disposition">
        {(
          [
            ["all", report.rows.length],
            ["New", summary.new],
            ["Changed", summary.changed],
            ["Unchanged", summary.unchanged],
            ["Inactive", summary.inactive],
            ["Rejected", summary.rejected],
          ] as const
        ).map(([key, count]) => (
          <button
            key={key}
            aria-pressed={filter === key}
            className={filter === key ? "primary" : ""}
            onClick={() => setFilter(key as RowDisposition | "all")}
          >
            {key === "all" ? "All" : key} ({count})
          </button>
        ))}
      </div>
      {summary.unmatched > 0 && (
        <div className="alert notice">
          {summary.unmatched} active student(s) on the current roster are missing from this file
          (report-only; they are not deactivated by promotion).
        </div>
      )}
      <div className="card table-wrap">
        <table>
          <caption className="visually-hidden">Staged roster rows</caption>
          <thead>
            <tr>
              <th scope="col">Row</th>
              <th scope="col">STKEY</th>
              <th scope="col">Name</th>
              <th scope="col">Year</th>
              <th scope="col">House</th>
              <th scope="col">Age group</th>
              <th scope="col">Category</th>
              <th scope="col">Disposition</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row) => (
              <RowLine key={row.rowNumber} row={row} />
            ))}
          </tbody>
        </table>
      </div>
      {report.unmatchedStudents.length > 0 && (
        <>
          <h2>Unmatched active students</h2>
          <ul className="card row-list">
            {report.unmatchedStudents.map((s) => (
              <li key={s.externalStudentId} className="row-text">
                <span className="mono">{s.externalStudentId}</span> — {s.firstName} {s.surname}
              </li>
            ))}
          </ul>
        </>
      )}
    </>
  );
}

function RowLine({ row }: { row: StagedRow }) {
  const tone =
    row.disposition === "Rejected"
      ? "bad"
      : row.disposition === "New"
        ? "ok"
        : row.disposition === "Changed"
          ? "info"
          : "muted";
  return (
    <tr>
      <td className="mono">{row.rowNumber}</td>
      <td className="mono">{row.externalStudentId ?? "—"}</td>
      <td>
        {row.preferredName ?? row.firstName} {row.surname}
      </td>
      <td>{row.yearLevel ?? "—"}</td>
      <td>{row.houseCode ?? "—"}</td>
      <td>{row.competitionAgeGroup ?? "—"}</td>
      <td>{row.competitionCategory ?? "—"}</td>
      <td>
        <span className={`badge ${tone}`}>{row.disposition}</span>
        {row.issues.length > 0 && (
          <span className="row-sub"> {row.issues.map((i) => i.code).join(", ")}</span>
        )}
      </td>
    </tr>
  );
}
