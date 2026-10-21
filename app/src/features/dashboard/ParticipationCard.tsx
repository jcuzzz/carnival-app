import type { CSSProperties } from "react";
import { houseColourVarOf, useHouseMarks } from "../../branding/houseMarks";
import { HouseMark } from "../houses/HouseMark";
import type { HouseParticipation } from "../../api/types";

/** Three integers per house and no per-student payload. */
export function ParticipationCard({ houses }: { houses: HouseParticipation[] }) {
  const marks = useHouseMarks();
  return (
    <section className="card" aria-labelledby="participation-heading">
      <h2 id="participation-heading" className="flush-top">
        Participation
      </h2>
      {houses.length === 0 ? (
        <p className="row-sub">No roster to count yet.</p>
      ) : (
        <ul className="row-list dash-list">
          {houses.map((house) => (
            <li key={house.houseCode} className="row-text participation-row">
              <span className="participation-head">
                <HouseMark code={house.houseCode} />
                {/* A swimmer who entered and then left the roster makes entered
                    exceed competitors; the bar clamps, so the text must too. */}
                <span className="row-sub mono">
                  {Math.min(house.entered, house.competitors)} of {house.competitors} entered
                </span>
              </span>
              <span className="deck-bar dash-bar" aria-hidden="true">
                <span
                  className="deck-bar-fill"
                  style={
                    {
                      width: `${percent(house)}%`,
                      "--bar": houseColourVarOf(marks, house.houseCode) ?? "var(--brand)",
                    } as CSSProperties
                  }
                />
              </span>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

function percent(house: HouseParticipation): number {
  return house.competitors > 0
    ? Math.min(Math.round((house.entered / house.competitors) * 100), 100)
    : 0;
}
