import { IMPORT_BATCH_EXPIRED_TEXT } from "../../api/errorText";
import type { ValidationReport } from "../../api/types";
import type { ApiState } from "../../api/useApiQuery";

/** Structural + row-level validation issues for the selected batch. */
export function ValidationSection({ state }: { state: ApiState<ValidationReport> }) {
  if (state.loading) return <p>Loading validation…</p>;
  // Stale report stays readable when a revalidation fails.
  const failed = state.error ? (
    <div className="alert error">Validation report: {state.error.message}</div>
  ) : null;
  const report = state.data;
  // Promotion purges the stored report with the staged rows; the section explains itself rather
  // than silently vanishing.
  if (report?.status === "Promoted" && !report.validation) {
    return (
      <>
        <h2>Validation</h2>
        <div className="alert notice">
          The validation report was purged when this batch was promoted — the summary counts and
          the audit record are the durable evidence.
        </div>
      </>
    );
  }
  if (report && !report.validation) {
    return (
      <>
        <h2>Validation</h2>
        {failed}
        <div className="alert notice">
          {report.status === "Rejected"
            ? "The validation report was discarded when this batch was rejected — the summary counts and the audit record are the durable evidence."
            : IMPORT_BATCH_EXPIRED_TEXT}
        </div>
      </>
    );
  }
  if (!report?.validation) return failed;
  const { structuralErrors, rowIssues } = report.validation;
  const exceptions = rowIssues.filter((r) =>
    r.issues.some((i) => i.code === "DUPLICATE_STKEY" || i.code === "MISSING_HOUSE"),
  );

  return (
    <>
      <h2>Validation</h2>
      {failed}
      {structuralErrors.length === 0 && rowIssues.length === 0 && (
        <div className="alert success">No validation issues.</div>
      )}
      {structuralErrors.length > 0 && (
        <div className="alert error">
          <strong>Structural errors (batch cannot promote):</strong>
          <ul>
            {structuralErrors.map((e, i) => (
              <li key={i}>{e}</li>
            ))}
          </ul>
        </div>
      )}
      {exceptions.length > 0 && (
        <div className="alert notice">
          {exceptions.length} row(s) carry DUPLICATE_STKEY / MISSING_HOUSE exceptions — expected
          operator work: resolve at source and re-import; these rows never promote.
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
