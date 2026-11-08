import { errorText } from "../../api/errorText";
import type { ImportBatchStatus, ImportBatchSummary } from "../../api/types";
import type { ApiState } from "../../api/useApiQuery";

/** The wire status in the words an organiser reads, and its badge tone. */
const STATUS: Readonly<Record<ImportBatchStatus, readonly [label: string, tone: string]>> = {
  Staged: ["Uploaded", "info"],
  Validated: ["Ready to review", "info"],
  ValidationFailed: ["Can't be imported", "bad"],
  Approved: ["Approved, list not yet updated", "warn"],
  Rejected: ["Rejected", "muted"],
  Promoted: ["Student list updated", "ok"],
};

export function StatusBadge({ status }: { status: ImportBatchStatus }) {
  const [label, tone] = STATUS[status] ?? [status, "info"];
  return <span className={`badge ${tone}`}>{label}</span>;
}

/** What a promoted batch did to the student list, from its stored counts. */
export function promotedResult(
  batch: Pick<ImportBatchSummary, "newCount" | "changedCount" | "inactiveCount" | "rejectedCount">,
): string {
  const parts = [
    batch.newCount > 0 && `${students(batch.newCount)} added`,
    batch.changedCount > 0 && `${students(batch.changedCount)} updated`,
    batch.inactiveCount > 0 && `${students(batch.inactiveCount)} marked as no longer enrolled`,
    batch.rejectedCount > 0 && `${rows(batch.rejectedCount)} left out`,
  ].filter((part): part is string => typeof part === "string");
  return parts.length > 0
    ? `Student list updated: ${parts.join(", ")}.`
    : "Student list updated: nothing in it changed.";
}

function students(n: number): string {
  return n === 1 ? "1 student" : `${n} students`;
}

function rows(n: number): string {
  return n === 1 ? "1 row" : `${n} rows`;
}

/** A batch as the organiser knows it: the file and when it went up, never its id. */
export function batchLabel(batch: Pick<ImportBatchSummary, "sourceFileName" | "importedAtUtc">) {
  return `${batch.sourceFileName}, uploaded ${uploadedAt(batch.importedAtUtc)}`;
}

function uploadedAt(utc: string): string {
  return new Date(utc).toLocaleString();
}

/** Uploads, most recent first — pick one to review. */
export function BatchList({
  state,
  selectedId,
  onSelect,
}: {
  state: ApiState<ImportBatchSummary[]>;
  selectedId: string | null;
  onSelect: (id: string) => void;
}) {
  if (state.loading) return <p>Loading uploads…</p>;
  // A failed revalidation keeps the cached list selectable and puts the error above it; only an
  // empty cache renders the error alone.
  const failed = state.error ? (
    <div className="alert error">The uploads couldn't be read: {errorText(state.error)}</div>
  ) : null;
  const batches = state.data;
  if (!batches) return failed;
  if (batches.length === 0)
    return (
      <>
        {failed}
        <p className="row-sub">No uploads yet.</p>
      </>
    );

  return (
    <div className="card table-wrap">
      <h2 className="flush-top">Uploads</h2>
      {failed}
      <table>
        <caption className="visually-hidden">Uploads, most recent first</caption>
        <thead>
          <tr>
            <th scope="col">File</th>
            <th scope="col">Status</th>
            <th scope="col">New</th>
            <th scope="col">Changed</th>
            <th scope="col">Inactive</th>
            <th scope="col">Left out</th>
            <th scope="col">Uploaded</th>
            <th scope="col">By</th>
            <th scope="col">
              <span className="visually-hidden">Review</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {batches.map((batch) => (
            <tr key={batch.id}>
              <td>{batch.sourceFileName}</td>
              <td>
                <StatusBadge status={batch.status} />
              </td>
              <td>{batch.newCount}</td>
              <td>{batch.changedCount}</td>
              <td>{batch.inactiveCount}</td>
              <td>{batch.rejectedCount}</td>
              <td>{uploadedAt(batch.importedAtUtc)}</td>
              {batch.importedByDisplayName ? (
                <td>{batch.importedByDisplayName}</td>
              ) : (
                <td className="mono">{batch.importedBySubject}</td>
              )}
              <td>
                {batch.id === selectedId ? (
                  <span className="row-sub">Shown below</span>
                ) : (
                  <button onClick={() => onSelect(batch.id)}>Review</button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
