import { IMPORT_BATCH_EXPIRED_TEXT } from "../../api/errorText";
import type { ValidationReport } from "../../api/types";
import type { ApiState } from "../../api/useApiQuery";
import { rejectedRowsSummary } from "./rejectedRows";

// The report names students, so it goes with the staged rows: no second copy of the roster is kept.
const PROMOTED_REPORT_TEXT =
  "This batch has updated the student list. Its row-by-row details, which named students, were " +
  "deleted then so that no second copy of the list is kept. The counts in the table above, and " +
  "the record of who updated the list and when, remain.";
const REJECTED_REPORT_TEXT =
  "This batch was rejected. Its row-by-row details, which named students, were deleted then. The " +
  "counts in the table above, and the record of who rejected it and when, remain.";

/** Structural + row-level validation issues for the selected batch. */
export function ValidationSection({ state }: { state: ApiState<ValidationReport> }) {
  if (state.loading) return <p>Loading validation…</p>;
  // Stale report stays readable when a revalidation fails.
  const failed = state.error ? (
    <div className="alert error">Validation report: {state.error.message}</div>
  ) : null;
  const report = state.data;
  // Promotion and rejection delete the row-by-row report with the staged rows; the section says so
  // rather than silently vanishing.
  if (report && !report.validation) {
    return (
      <>
        <h2>Validation</h2>
        {failed}
        <div className="alert notice">
          {report.status === "Promoted"
            ? PROMOTED_REPORT_TEXT
            : report.status === "Rejected"
              ? REJECTED_REPORT_TEXT
              : IMPORT_BATCH_EXPIRED_TEXT}
        </div>
      </>
    );
  }
  if (!report?.validation) return failed;
  const { structuralErrors, rowIssues } = report.validation;
  const rejected = rejectedRowsSummary(rowIssues);

  return (
    <>
      <h2>Validation</h2>
      {failed}
      {structuralErrors.length === 0 && rowIssues.length === 0 && (
        <div className="alert success">No validation issues.</div>
      )}
      {structuralErrors.length > 0 && (
        <div className="alert error">
          <strong>This file can't update the student list. Problems with its layout:</strong>
          <ul>
            {structuralErrors.map((e, i) => (
              <li key={i}>{e}</li>
            ))}
          </ul>
        </div>
      )}
      {rejected && (
        <div className="alert notice">
          <p className="flush-top">{rejected}</p>
          <p>
            The rest can still update the student list. To bring these students in, fix them in
            your student system and upload a new export.
          </p>
        </div>
      )}
      {rowIssues.length > 0 && (
        <div className="card table-wrap">
          <table>
            <caption className="visually-hidden">Row-level validation issues</caption>
            <thead>
              <tr>
                <th scope="col">Row</th>
                <th scope="col">STKEY</th>
                <th scope="col">Code</th>
                <th scope="col">Severity</th>
                <th scope="col">Message</th>
              </tr>
            </thead>
            <tbody>
              {rowIssues.flatMap((row) =>
                row.issues.map((issue, i) => (
                  <tr key={`${row.rowNumber}-${i}`}>
                    <td className="mono">{row.rowNumber}</td>
                    <td className="mono">{row.externalStudentId ?? "—"}</td>
                    <td className="mono">{issue.code}</td>
                    <td>
                      <span
                        className={`badge ${issue.severity === "Warning" ? "warn" : issue.severity === "Exception" ? "info" : "bad"}`}
                      >
                        {issue.severity}
                      </span>
                    </td>
                    <td className="wrap-message">{issue.message}</td>
                  </tr>
                )),
              )}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
