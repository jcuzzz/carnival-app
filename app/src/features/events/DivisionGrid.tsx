import { Fragment, useState } from "react";
import { ApiError } from "../../api/client";
import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../../api/errorText";
import { generateDivisions, unscheduleDivision } from "../../api/resources";
import type { CarnivalEvent, DivisionProfile } from "../../api/types";
import { href } from "../../router";
import { counted } from "./counted";
import { buildDivisionGrid, type DivisionGridCell } from "./divisionGridModel";

export type DivisionGridMode =
  | {
      kind: "live";
      carnivalId: string;
      eventDefinitionId: string;
      /** A division was created or removed — the parent refetches. */
      onChanged: () => void;
    }
  | {
      kind: "draft";
      ticked: ReadonlySet<string>;
      onTickedChange: (ticked: ReadonlySet<string>) => void;
    };

export function DivisionGrid({
  profile,
  divisions,
  mode,
  writable,
  profilePath,
}: {
  profile: DivisionProfile | null | undefined;
  /** This carnival's divisions of the one event; empty in draft mode. */
  divisions: readonly CarnivalEvent[];
  mode: DivisionGridMode;
  writable: boolean;
  /** Route of the divisions-profile screen, for the not-configured state. */
  profilePath: string;
}) {
  const [pending, setPending] = useState<string | null>(null);
  const [refusal, setRefusal] = useState<{ cellKey: string; text: string } | null>(null);
  // A removed division stays in `divisions` until the refetch drops it; its box holds disabled,
  // or a second click would DELETE an id that is already gone.
  const [removed, setRemoved] = useState<ReadonlySet<string>>(() => new Set());
  // A ticked box holds itself ticked from the press until the write answers.
  const [added, setAdded] = useState<ReadonlySet<string>>(() => new Set());
  const model = buildDivisionGrid(profile, divisions);

  async function toggle(cell: DivisionGridCell, tick: boolean): Promise<void> {
    if (mode.kind === "draft") {
      const next = new Set(mode.ticked);
      if (tick) next.add(cell.key);
      else next.delete(cell.key);
      mode.onTickedChange(next);
      return;
    }
    setRefusal(null);
    setPending(cell.key);
    if (tick) setAdded((prior) => new Set(prior).add(cell.key));
    else setAdded((prior) => without(prior, cell.key));
    try {
      if (tick)
        await generateDivisions(mode.carnivalId, {
          eventDefinitionId: mode.eventDefinitionId,
          divisions: [cell.spec],
        });
      else if (cell.event) {
        const removedId = cell.event.id;
        await unscheduleDivision(mode.carnivalId, removedId);
        setRemoved((prior) => new Set(prior).add(removedId));
      }
      mode.onChanged();
    } catch (err) {
      setRefusal({ cellKey: cell.key, text: refusalText(cell, err) });
    } finally {
      // Dropped once the write has answered, either way: held any longer, the
      // key would still claim a tick after the division is unscheduled again.
      if (tick) setAdded((prior) => without(prior, cell.key));
      setPending(null);
    }
  }

  const awaitingRefetch = (cell: DivisionGridCell): boolean =>
    cell.event !== null && removed.has(cell.event.id);
  const awaitingCreate = (cell: DivisionGridCell): boolean =>
    cell.event === null && added.has(cell.key);
  const ticked = (cell: DivisionGridCell): boolean =>
    mode.kind === "draft"
      ? mode.ticked.has(cell.key)
      : awaitingCreate(cell) || (cell.event !== null && !awaitingRefetch(cell));
  const drawable = model.rows.length > 0 && model.columns.length > 0;

  return (
    <>
      {!model.configured && (
        <p className="row-sub">
          This carnival has no division profile yet, so there are no boxes to tick. Set the
          ages, categories and tiers it contests on{" "}
          <a href={href(profilePath)}>the divisions profile</a>, and they appear here for every
          event.
        </p>
      )}
      {model.outsideProfile > 0 && (
        <p className="row-sub">{outsideProfileText(model.outsideProfile)}</p>
      )}
      {drawable && (
        <>
          <div className="table-wrap division-grid">
            <table>
              <thead>
                <tr>
                  <th scope="col">Age group</th>
                  {model.columns.map((column) => (
                    <th key={column.key} scope="col">
                      {column.label}
                      {column.outsideProfile && <span className="badge warn"> off profile</span>}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {model.rows.map((row) => (
                  <Fragment key={row.key}>
                    <tr>
                      <th scope="row">
                        {row.label}
                        {row.outsideProfile && <span className="badge warn"> off profile</span>}
                      </th>
                      {row.cells.map((cell) => (
                        <td key={cell.key}>
                          <span className="check-row">
                            <input
                              type="checkbox"
                              checked={ticked(cell)}
                              disabled={
                                !writable ||
                                pending !== null ||
                                awaitingRefetch(cell) ||
                                awaitingCreate(cell)
                              }
                              aria-label={cell.label}
                              {...(refusal?.cellKey === cell.key
                                ? { "aria-describedby": refusalId(cell) }
                                : {})}
                              onChange={(e) => void toggle(cell, e.target.checked)}
                            />
                            {mode.kind === "live" && ticked(cell) && (
                              <span className="row-sub">{cell.entryCount}</span>
                            )}
                            {cell.status === "Closed" && (
                              <span className="badge muted">Closed</span>
                            )}
                          </span>
                        </td>
                      ))}
                    </tr>
                    {refusal !== null && row.cells.some((cell) => cell.key === refusal.cellKey) && (
                      <tr>
                        <td
                          className="wrap-error"
                          colSpan={model.columns.length + 1}
                          id={refusalId({ key: refusal.cellKey })}
                          role="alert"
                        >
                          {refusal.text}
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
              </tbody>
            </table>
          </div>
          {mode.kind === "draft" ? (
            <p className="row-sub">
              Every box starts ticked — untick anything this carnival will not contest. Nothing is
              created until the button below is pressed.
            </p>
          ) : (
            <p className="row-sub">
              A ticked box is a division this carnival contests, and the number beside it is its
              entries. Un-ticking removes a division created in error; one that has been contested
              is closed instead, on the event's own screen.
            </p>
          )}
        </>
      )}
    </>
  );
}

function outsideProfileText(count: number): string {
  return count === 1
    ? "One division is contested outside this carnival's profile — the profile changed " +
        "after it was created. It is still run, and is shown below."
    : `${count} divisions are contested outside this carnival's profile — the profile changed ` +
        "after they were created. They are still run, and are shown below.";
}

function without(set: ReadonlySet<string>, key: string): Set<string> {
  const next = new Set(set);
  next.delete(key);
  return next;
}

function refusalId(cell: Pick<DivisionGridCell, "key">): string {
  return `division-refusal-${cell.key.replace(/[^A-Za-z0-9]+/g, "-")}`;
}

// Not `codeText` (api/errorText): the sentence has to name the division the tick
// belongs to, which a code-keyed map cannot see.
function refusalText(cell: DivisionGridCell, err: unknown): string {
  if (err instanceof ApiError && err.code === "EVENT_HAS_ENTRIES") {
    const entries = countOf(err.body?.entryCount);
    const results = countOf(err.body?.resultCount);
    return (
      `${cell.label} has ${counted(entries, "entry", "entries")} and ` +
      `${counted(results, "result", "results")}, so it can't be removed. Close the division ` +
      `instead — removing one is only for a division created in error.`
    );
  }
  return `${cell.label}: ${errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT)}`;
}

function countOf(value: unknown): number {
  return typeof value === "number" ? value : 0;
}
