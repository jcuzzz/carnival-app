import { Fragment, useEffect, useMemo, useState, type FormEvent } from "react";
import { ApiError } from "../../api/client";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../../api/errorText";
import { keys } from "../../api/queryKeys";
import {
  applyTemplateReconcile,
  listEventTemplates,
  previewTemplateReconcile,
} from "../../api/resources";
import type { TemplateDiffRow, TemplateReconcileOutcome } from "../../api/types";
import { useApiQuery, useInvalidate } from "../../api/useApiQuery";
import { showToast } from "../../shell/toast";

const FIELD_LABELS: Readonly<Record<string, string>> = {
  name: "Name",
  sport: "Sport",
  measureType: "Measure",
  units: "Units",
  isActive: "Active",
  format: "Format",
  teamSize: "Team size",
  legsJson: "Legs",
  scoringWeight: "Scoring weight",
  tagsJson: "Tags",
  recordCategoriesJson: "Record categories",
};

const MAX_REASON = 500;

function fieldLabel(field: string): string {
  return FIELD_LABELS[field] ?? field;
}

function value(text: string | null): string {
  return text === null || text === "" ? "—" : text;
}

/** The calendar day of an ISO stamp in the reader's zone. */
function dayOf(isoTimestamp: string | null): string | null {
  return isoTimestamp === null ? null : new Date(isoTimestamp).toLocaleDateString();
}

export default function TemplatesSection({ offline }: { offline: boolean }) {
  const templates = useApiQuery(keys.eventTemplates(), listEventTemplates);
  const [templateId, setTemplateId] = useState("");
  const [leftOut, setLeftOut] = useState<readonly string[]>([]);
  const [reason, setReason] = useState("");
  const [applying, setApplying] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [stale, setStale] = useState(false);
  const [outcome, setOutcome] = useState<TemplateReconcileOutcome | null>(null);
  const invalidate = useInvalidate();

  const available = templates.data ?? [];
  const firstReadable = available.find((t) => t.readable)?.id;

  useEffect(() => {
    if (templateId === "" && firstReadable !== undefined) setTemplateId(firstReadable);
  }, [firstReadable, templateId]);

  const preview = useApiQuery(
    keys.templateReconcile(templateId),
    () => previewTemplateReconcile(templateId),
    { enabled: templateId !== "" },
  );

  const diff = preview.data;
  const diffHash = diff?.diffHash;

  // A new diff is a new decision: what the last one left out means nothing here.
  useEffect(() => setLeftOut([]), [diffHash]);
  // The result summary belongs to the template it was produced for, and must
  // survive the refetch an apply triggers — that refetch IS what proves it.
  useEffect(() => {
    setOutcome(null);
    setStale(false);
  }, [templateId]);

  const rows = useMemo(() => diff?.rows ?? [], [diff]);
  const changing = rows.filter((row) => row.kind === "Update" || row.kind === "Missing");
  const catalogueOnly = rows.filter((row) => row.kind === "CatalogueOnly");
  const excluded = useMemo(
    () => rows.filter((row) => row.guarded || leftOut.includes(row.code)).map((row) => row.code),
    [rows, leftOut],
  );
  const selected = changing.filter((row) => !excluded.includes(row.code));
  const willUpdate = selected.filter((row) => row.kind === "Update").length;
  const willCreate = selected.filter((row) => row.kind === "Missing").length;
  const nothingToApply = willUpdate + willCreate === 0;

  function toggle(code: string): void {
    setLeftOut((current) =>
      current.includes(code) ? current.filter((c) => c !== code) : [...current, code],
    );
  }

  async function apply(e: FormEvent): Promise<void> {
    e.preventDefault();
    if (applying || diffHash === undefined || reason.trim() === "" || nothingToApply) return;
    setApplying(true);
    setError(null);
    setStale(false);
    try {
      const result = await applyTemplateReconcile(templateId, diffHash, reason.trim(), excluded);
      setOutcome(result);
      setReason("");
      showToast(`${result.templateName} applied: ${result.updated + result.created} event(s) changed`);
      invalidate(keys.eventTemplates(), keys.templateReconcile(templateId));
    } catch (err) {
      if (err instanceof ApiError && err.code === "TEMPLATE_RECONCILE_STALE") {
        // The preview no longer describes the server; re-read it rather than
        // leaving a diff on screen that cannot be applied.
        setStale(true);
        invalidate(keys.templateReconcile(templateId));
      } else {
        setError(errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT));
      }
    } finally {
      setApplying(false);
    }
  }

  const chosen = available.find((t) => t.id === templateId);

  return (
    <section className="subsection">
      <p className="row-sub">
        A template document ships with a deploy and changes nothing by itself. Preview what it
        would change in the event catalogue — every carnival, past and present, shares those
        events — then apply it with a reason. Nothing is scheduled, removed or deactivated here.
      </p>
      <p className="row-sub">
        To set a carnival up from a template — divisions, events and the program — use Apply a
        template on that carnival’s Manage screen.
      </p>

      {offline && (
        <div className="alert notice" role="status">
          Can’t reach the server, so a template can’t be previewed or applied — reconnect and
          this comes back.
        </div>
      )}
      {templates.error && (
        <div className="alert error" role="alert">
          {errorText(templates.error, "Can’t reach the server, so the templates couldn’t be read.")}
        </div>
      )}
      {preview.error && (
        <div className="alert error" role="alert">
          {errorText(preview.error, "Can’t reach the server, so the preview couldn’t be read.")}
        </div>
      )}
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
      {stale && (
        <div className="alert notice" role="alert">
          The catalogue or the template changed since this preview was taken, so nothing was
          applied. The preview below has been read again — check what it now proposes.
        </div>
      )}

      {templates.loading ? (
        <p>Loading…</p>
      ) : available.length === 0 ? (
        <p className="row-sub">This server holds no event templates.</p>
      ) : (
        <label className="field wide">
          <span>Template</span>
          <select
            id="template-key"
            aria-label="Template"
            value={templateId}
            onChange={(e) => setTemplateId(e.target.value)}
          >
            {available.map((template) => (
              <option key={template.id} value={template.id} disabled={!template.readable}>
                {template.readable
                  ? `${template.name ?? template.id} (${template.definitions} events)`
                  : `${template.id} — cannot be read`}
              </option>
            ))}
          </select>
        </label>
      )}

      {chosen?.readable && (
        <p className="row-sub">
          From <span className="mono">{chosen.version}</span>, as shipped by the last deploy.
        </p>
      )}

      {templateId !== "" && preview.loading && <p>Reading the catalogue…</p>}

      {diff && (
        <Fragment>
          <p className="row-sub">
            {diff.counts.update} to change · {diff.counts.missing} to create ·{" "}
            {diff.counts.unchanged} already match · {diff.counts.catalogueOnly} not in this
            template
            {diff.counts.guarded > 0 && ` · ${diff.counts.guarded} held back`}
          </p>

          {changing.length === 0 ? (
            <div className="alert success" role="status">
              The catalogue already matches this template. There is nothing to apply.
            </div>
          ) : (
            <div className="card table-wrap">
              <table>
                <caption className="visually-hidden">
                  What applying {diff.templateName} would change
                </caption>
                <thead>
                  <tr>
                    <th scope="col">Event</th>
                    <th scope="col">Field</th>
                    <th scope="col">Now</th>
                    <th scope="col">Template</th>
                  </tr>
                </thead>
                <tbody>
                  {changing.map((row) => (
                    <DiffRows
                      key={row.code}
                      row={row}
                      leftOut={leftOut.includes(row.code)}
                      onToggle={() => toggle(row.code)}
                    />
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {catalogueOnly.length > 0 && (
            <p className="row-sub">
              Untouched — in the catalogue but not in this template:{" "}
              {catalogueOnly.map((row) => row.code).join(", ")}. Reconcile never removes or
              deactivates an event; use the event editor for that.
            </p>
          )}

          <form className="card settings-form" onSubmit={(e) => void apply(e)}>
            <h2>Apply this template</h2>
            <p className="row-sub">
              {nothingToApply
                ? "Nothing is selected to apply."
                : `${willUpdate} event(s) will change and ${willCreate} will be created.`}
            </p>
            <label className="field wide">
              <span>Reason for applying (recorded on the audit trail)</span>
              <input
                id="template-reason"
                value={reason}
                maxLength={MAX_REASON}
                placeholder="e.g. category-first event names, ADR-0018"
                onChange={(e) => setReason(e.target.value)}
              />
            </label>
            <div className="button-row">
              <button
                className="primary"
                type="submit"
                disabled={applying || offline || nothingToApply || reason.trim() === ""}
              >
                {applying ? "Applying…" : "Apply template"}
              </button>
            </div>
          </form>

          {outcome && (
            <div className="alert success" role="status">
              <strong>
                {outcome.templateName} applied — {outcome.updated} changed, {outcome.created}{" "}
                created.
              </strong>
              {outcome.updatedCodes.length > 0 && <div>Changed: {outcome.updatedCodes.join(", ")}</div>}
              {outcome.createdCodes.length > 0 && <div>Created: {outcome.createdCodes.join(", ")}</div>}
              {outcome.excludedCodes.length > 0 && (
                <div>Left out: {outcome.excludedCodes.join(", ")}</div>
              )}
              <div>Devices show the new names the next time they read the event list.</div>
            </div>
          )}
        </Fragment>
      )}
    </section>
  );
}

/** A guarded row carries no checkbox: it cannot be applied at all. */
function DiffRows({
  row,
  leftOut,
  onToggle,
}: {
  row: TemplateDiffRow;
  leftOut: boolean;
  onToggle: () => void;
}) {
  const editedOn = dayOf(row.updatedAtUtc);
  // Guards sit beside the badge: the Template column is the first thing off a phone's right edge.
  const guards = [...new Set(row.fields.map((f) => f.guard).filter((g) => g !== null))];
  // .row-main stacks the code over its name; inside a table cell they would
  // otherwise run together on one line.
  const header = (
    <span className="row-main">
      <span className="row-title mono">{row.code}</span>
      <span className="row-sub">{row.name}</span>
      {(row.guarded || row.editedInApp) && (
        // One line: a badge is a flex item in .row-main and would otherwise
        // stretch into a full-width bar.
        <span className="row-sub">
          {row.guarded && <span className="badge bad">cannot change</span>}
          {row.editedInApp && (
            <span className="badge warn">edited in the app{editedOn ? ` ${editedOn}` : ""}</span>
          )}
        </span>
      )}
      {guards.map((guard) => (
        <span className="row-sub" key={guard}>
          {guard}
        </span>
      ))}
      {!row.guarded && (
        <label className="check-row">
          <input type="checkbox" checked={leftOut} onChange={onToggle} />
          <span>Leave out</span>
        </label>
      )}
    </span>
  );

  if (row.kind === "Missing") {
    return (
      <tr>
        <td className="wrap-message">{header}</td>
        <td colSpan={3}>
          <span className="badge info">will be created</span> from the template
        </td>
      </tr>
    );
  }

  return (
    <>
      {row.fields.map((field, index) => (
        <tr key={field.field}>
          {index === 0 && (
            <td className="wrap-message" rowSpan={row.fields.length}>
              {header}
            </td>
          )}
          <td>{fieldLabel(field.field)}</td>
          <td className="wrap-message">{value(field.prior)}</td>
          <td className="wrap-message">{value(field.new)}</td>
        </tr>
      ))}
    </>
  );
}
