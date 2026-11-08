import { useMemo, useState } from "react";
import { ApiError } from "../../api/client";
import { IMPORT_BATCH_EXPIRED_TEXT, errorText } from "../../api/errorText";
import type { ReconciliationReport, RowDisposition, StagedRow } from "../../api/types";
import type { ApiState } from "../../api/useApiQuery";
import { agesAsAt } from "./cutOff";
import { rowProblems } from "./rejectedRows";

const DISPOSITION_ORDER: RowDisposition[] = ["Rejected", "New", "Changed", "Inactive", "Unchanged"];

/** Each wire disposition as the organiser reads it: "Rejected" is the summary's "Left out". */
const DISPOSITION_LABEL: Readonly<Record<RowDisposition, string>> = {
  New: "New",
  Changed: "Changed",
  Unchanged: "Unchanged",
  Inactive: "Inactive",
  Rejected: "Left out",
};

/** Every row of the upload and what updating the student list would do with it. */
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

  if (state.loading) return <p>Reading the students in this file…</p>;
  // Promotion and rejection delete the staged rows: a finished state, not a failure, and the
  // validation section above already says so.
  if (state.error instanceof ApiError && state.error.code === "IMPORT_ROWS_PURGED") return null;
  if (state.error instanceof ApiError && state.error.code === "IMPORT_BATCH_EXPIRED") {
    return <div className="alert notice">{IMPORT_BATCH_EXPIRED_TEXT}</div>;
  }
  // Other 409 = staged rows absent (validation failed at intake); message explains.
  // With a preview already cached the error goes above it, not over it.
  const failed = state.error ? (
    <div className="alert error">
      The students in this file couldn't be read: {errorText(state.error)}
    </div>
  ) : null;
  if (!report) return failed;

  const { summary } = report;
  return (
    <>
      <h2>Students in this file — competition year {report.competitionYear}</h2>
      {failed}
      <p>
        Ages as at <strong>{agesAsAt(report.cutOff, report.competitionYear)}</strong>.
      </p>
      <p className="row-sub">
        Wrong year? Reject this upload and send the file again with the carnival’s year.
      </p>
      <div className="button-row" role="group" aria-label="Show rows by outcome">
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
            {key === "all" ? "All" : DISPOSITION_LABEL[key]} ({count})
          </button>
        ))}
      </div>
      {summary.unmatched > 0 && (
        <div className="alert notice">
          {summary.unmatched === 1
            ? "1 student on the student list is not in this file. "
            : `${summary.unmatched} students on the student list are not in this file. `}
          Updating the list leaves them as they are.
        </div>
      )}
      <div className="card table-wrap">
        <table>
          <caption className="visually-hidden">
            Each row of this file, and what updating the student list does with it
          </caption>
          <thead>
            <tr>
              <th scope="col">Row</th>
              <th scope="col">Student ID</th>
              <th scope="col">Name</th>
              <th scope="col">Year</th>
              <th scope="col">House</th>
              <th scope="col">Age group</th>
              <th scope="col">Category</th>
              <th scope="col">Outcome</th>
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
          <h2>On the student list, not in this file</h2>
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
        <span className={`badge ${tone}`}>{DISPOSITION_LABEL[row.disposition] ?? row.disposition}</span>
        {row.issues.length > 0 && <span className="row-sub"> {rowProblems(row.issues)}</span>}
      </td>
    </tr>
  );
}
