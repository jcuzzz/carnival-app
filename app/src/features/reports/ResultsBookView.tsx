import type { MeasureType, ResultRow, ResultsReport, SchoolRecord } from "../../api/types";
import { formatHeight, heightKey, inNotation, sameHeight } from "../../results/jumpCard";
import { RecordBadge } from "../records/RecordBadge";
import { bookRowKey, indexRecords } from "../records/standingRecords";
import { blockHeights, groupResults, heatLabel } from "./resultsBook";

// Every value shown is the server's, the place especially.

/** The card is the evidence for a countback, so the rule prints with it. */
const COUNTBACK_NOTE =
  "O cleared · X failed · – passed. Places by best height, then fewest fails at that height, " +
  "then fewest fails up to and including it (Total); equal on all three is a tie.";

export function ResultsBookView({
  report,
  records,
  measureByEvent,
}: {
  report: ResultsReport;
  /** The standing book, for the record badge; absent badges nothing. */
  records?: readonly SchoolRecord[];
  /** A field block prints no heat caption; absent reads as a time. */
  measureByEvent?: ReadonlyMap<string, MeasureType>;
}) {
  const blocks = groupResults(report.rows, measureByEvent);
  const standing = indexRecords(records).byBookRow;
  if (blocks.length === 0)
    return <p>No results have been recorded yet, so the book is empty.</p>;

  return (
    <>
      {blocks.map((block) => (
        <section key={block.carnivalEventId} className="report-block">
          <h3>
            {block.eventName} · {block.division}{" "}
            {block.tier === "Social" && <span className="badge">Social</span>}
          </h3>
          <p className="row-sub">{block.eventCode}</p>
          {block.heats.map((heat) => {
            const heights = blockHeights(heat.rows);
            if (heights.length > 0)
              return (
                <JumpBlock
                  key={heat.key}
                  rows={heat.rows}
                  heights={heights}
                  standing={standing}
                  tier={block.tier}
                />
              );
            const caption = heatLabel(heat.heat, measureByEvent?.get(block.carnivalEventId));
            return (
              <div key={heat.key} className="table-wrap">
                <table>
                  {caption !== null && <caption className="row-sub">{caption}</caption>}
                  <thead>
                    <tr>
                      <th scope="col">Place</th>
                      <th scope="col">Name</th>
                      <th scope="col">House</th>
                      <th scope="col">Result</th>
                    </tr>
                  </thead>
                  <tbody>
                    {heat.rows.map((row, i) => (
                      <tr key={`${heat.key}:${i}`}>
                        <td className="mono">{row.place ?? "—"}</td>
                        <td>{row.displayName}</td>
                        <td>{row.houseCode}</td>
                        <td className="mono">
                          {row.status === "Ok" ? (row.value ?? "—") : row.status.toUpperCase()}
                          {row.status === "Ok" && (
                            <RecordBadge
                              record={standing.get(bookRowKey(row.eventCode, row.division))}
                              value={row.measuredValue}
                              tier={block.tier}
                            />
                          )}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            );
          })}
        </section>
      ))}
    </>
  );
}

/** The printed card: the deck's grid, placed and with the countback. */
function JumpBlock({
  rows,
  heights,
  standing,
  tier,
}: {
  rows: readonly ResultRow[];
  heights: readonly number[];
  standing: ReadonlyMap<string, SchoolRecord>;
  tier: ResultRow["tier"];
}) {
  return (
    <>
      <div className="table-wrap">
        <table className="jump-book">
          <thead>
            <tr>
              <th scope="col">Place</th>
              <th scope="col">Name</th>
              <th scope="col">House</th>
              {heights.map((height) => (
                <th key={heightKey(height)} scope="col" className="jump-book-height">
                  {formatHeight(height)}
                </th>
              ))}
              <th scope="col">Best</th>
              <th scope="col">Fails @ best</th>
              <th scope="col">Total</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row, i) => (
              <tr key={`${row.displayName}:${i}`}>
                <td className="mono">{row.place ?? "—"}</td>
                <td>{row.displayName}</td>
                <td>{row.houseCode}</td>
                {heights.map((height) => (
                  <td key={heightKey(height)} className="mono jump-book-height">
                    {inNotation(
                      row.jumps?.find((jump) => sameHeight(jump.height, height))?.marks ?? "",
                    )}
                  </td>
                ))}
                <td className="mono">
                  {row.status === "Ok" ? (row.value ?? "—") : row.status.toUpperCase()}
                  {row.status === "Ok" && (
                    <RecordBadge
                      record={standing.get(bookRowKey(row.eventCode, row.division))}
                      value={row.measuredValue}
                      tier={tier}
                    />
                  )}
                </td>
                <td className="mono jump-book-height">{row.failsAtBest ?? ""}</td>
                <td className="mono jump-book-height">{row.totalFails ?? ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="row-sub">
        {rows.some((row) => row.attemptsPerHeight === 2) &&
          "2 attempts per height: out when both fail at one height. "}
        {COUNTBACK_NOTE}
      </p>
    </>
  );
}
