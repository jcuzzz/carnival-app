import type { CSSProperties } from "react";
import { houseColourVarOf, useHouseMarks } from "../../branding/houseMarks";
import { HouseMark } from "../houses/HouseMark";
import type { CarnivalSettings, Standings } from "../../api/types";
import { scoringLegend } from "./scoringLegend";

/** Components shown so the total stays reproducible. */
export function StandingsTable({
  data,
  settings,
  colour = false,
}: {
  data: Standings;
  settings: CarnivalSettings | undefined;
  /** House colour is the Dashboard's alone so far; reports stay text. */
  colour?: boolean;
}) {
  const maxTotal = data.houses.length > 0 ? Math.max(...data.houses.map((h) => h.total), 1) : 1;
  const marks = useHouseMarks();
  // The server ranks; "=" only reads a rank another house also holds.
  const shared = new Set(
    data.houses
      .map((h) => h.rank)
      .filter((rank, i, ranks) => ranks.indexOf(rank) !== i),
  );
  return (
    <>
      <ol className="standings">
        {data.houses.map((h) => (
          <li key={h.houseCode} className="standings-row">
            <span className="standings-rank">{`${shared.has(h.rank) ? "=" : ""}${h.rank}`}</span>
            <span className="standings-body">
              <span className="standings-head">
                <span className="standings-house">
                  {colour ? <HouseMark code={h.houseCode} /> : h.houseCode}
                </span>
                <span className="standings-total mono">{h.total}</span>
              </span>
              {/* Per-house computed values, not rules; `--bar` rather than `background` so the
                  print rule can still win. */}
              <span
                className="standings-bar"
                style={
                  {
                    width: `${Math.round((Math.max(h.total, 0) / maxTotal) * 100)}%`,
                    "--bar":
                      (colour && houseColourVarOf(marks, h.houseCode)) || "var(--brand)",
                  } as CSSProperties
                }
                aria-hidden="true"
              />
              <span className="row-sub">
                {h.placementPoints} placement · {h.participationPoints} participation
                {h.bonusPoints !== 0 ? ` · ${h.bonusPoints > 0 ? "+" : ""}${h.bonusPoints} bonus` : ""}
              </span>
            </span>
          </li>
        ))}
      </ol>
      <p className="row-sub">
        {data.provisional
          ? "Provisional — updates as results are recorded; official once the carnival is finalised."
          : "Final standings."}{" "}
        From {data.resultsCounted} results across {data.eventsCounted} events · rules {data.ruleVersion}.
      </p>
      {settings && <p className="row-sub">Scoring: {scoringLegend(settings.scoring, data)}</p>}
    </>
  );
}
