import type { ImportBatchSummary } from "../../api/types";
import type { ApiState } from "../../api/useApiQuery";

/** Import batches, most recent first — pick one to review. */
export function BatchList({
  state,
  selectedId,
  onSelect,
}: {
  state: ApiState<ImportBatchSummary[]>;
  selectedId: string | null;
  onSelect: (id: string) => void;
}) {
  if (state.loading) return <p>Loading import batches…</p>;
  // A failed revalidation keeps the cached list selectable and puts the error above it; only an
  // empty cache renders the error alone.
  const failed = state.error ? (
    <div className="alert error">Import batches: {state.error.message}</div>
  ) : null;
  const batches = state.data;
  if (!batches) return failed;
  if (batches.length === 0)
    return (
      <>
        {failed}
        <p className="row-sub">No import batches yet.</p>
      </>
    );

  return (
    <div className="card table-wrap">
      <h2 className="flush-top">Import batches</h2>
      {failed}
      <table>
        <caption className="visually-hidden">Import batches, most recent first</caption>
        <thead>
          <tr>
            <th scope="col">File</th>
            <th scope="col">Status</th>
            <th scope="col">New</th>
            <th scope="col">Changed</th>
            <th scope="col">Inactive</th>
            <th scope="col">Rejected</th>
            <th scope="col">Imported</th>
            <th scope="col">By</th>
            <th scope="col"></th>
          </tr>
        </thead>
        <tbody>
          {batches.map((batch) => (
            <tr key={batch.id}>
              <td>{batch.sourceFileName}</td>
              <td>
                <span className="badge info">{batch.status}</span>
              </td>
              <td>{batch.newCount}</td>
              <td>{batch.changedCount}</td>
              <td>{batch.inactiveCount}</td>
              <td>{batch.rejectedCount}</td>
              <td>{new Date(batch.importedAtUtc).toLocaleString()}</td>
              <td className="mono">{batch.importedBySubject}</td>
              <td>
                {batch.id === selectedId ? (
                  <span className="badge ok">reviewing</span>
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
