import { IMPORT_BATCH_EXPIRED_TEXT, errorText } from "../../api/errorText";
import type { RowValidation, ValidationReport } from "../../api/types";
import type { ApiState } from "../../api/useApiQuery";
import { capitalised, hasWordsFor, rejectedRowsSummary, rowProblem } from "./rejectedRows";

// The report names students, so it goes with the staged rows: no second copy of the roster is kept.
const PROMOTED_REPORT_TEXT =
  "This upload has updated the student list. Its row-by-row details, which named students, were " +
  "deleted then so that no second copy of the list is kept. The counts in the table above, and " +
  "the record of who updated the list and when, remain.";
const REJECTED_REPORT_TEXT =
  "This upload was rejected. Its row-by-row details, which named students, were deleted then. The " +
  "counts in the table above, and the record of who rejected it and when, remain.";

/** Any problem but a Warning keeps the whole row out, so every line of the row says so. */
function leftOut(row: RowValidation): boolean {
  return row.issues.some((issue) => issue.severity !== "Warning");
}

/** The file's layout problems and each row left out, for the selected upload. */
export function ValidationSection({ state }: { state: ApiState<ValidationReport> }) {
  if (state.loading) return <p>Checking the file…</p>;
  // Stale report stays readable when a revalidation fails.
  const failed = state.error ? (
    <div className="alert error">
      The problems in this file couldn't be read: {errorText(state.error)}
    </div>
  ) : null;
  const report = state.data;
  // Promotion and rejection delete the row-by-row report with the staged rows; the section says so
  // rather than silently vanishing.
  if (report && !report.validation) {
    return (
      <>
        <h2>Row-by-row details</h2>
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
      <h2>Problems in the file</h2>
      {failed}
      {structuralErrors.length === 0 && rowIssues.length === 0 && (
        <div className="alert success">No problems: every row can update the student list.</div>
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
            <caption className="visually-hidden">Rows with a problem</caption>
            <thead>
              <tr>
                <th scope="col">Row</th>
                <th scope="col">Student ID</th>
                <th scope="col">Problem</th>
                <th scope="col">Outcome</th>
              </tr>
            </thead>
            <tbody>
              {rowIssues.flatMap((row) =>
                row.issues.map((issue, i) => (
                  <tr key={`${row.rowNumber}-${i}`}>
                    <td className="mono">{row.rowNumber}</td>
                    <td className="mono">{row.externalStudentId ?? "—"}</td>
                    <td className="wrap-message">
                      {capitalised(rowProblem(issue.code))}
                      {/* A code the app has no words for yet: the server's own sentence is all there is. */}
                      {!hasWordsFor(issue.code) && <span className="row-sub"> {issue.message}</span>}
                    </td>
                    <td>
                      {leftOut(row) ? (
                        <span className="badge bad">Left out</span>
                      ) : (
                        <span className="badge warn">Kept</span>
                      )}
                    </td>
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
