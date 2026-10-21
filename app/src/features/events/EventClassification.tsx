import type { CarnivalEvent } from "../../api/types";

/**
 * Badges only departures from the norm; a scoring-mode override that disagrees with the tier's
 * default earns one, so a flat-per-entry Competitive event does not look ordinary.
 */
export function EventClassification({
  event,
}: {
  event: Pick<CarnivalEvent, "tier" | "scoresPoints" | "scoringMode" | "effectiveScoringMode">;
}) {
  const tierDefault = event.tier === "Social" ? "Social" : "Places";
  const overridden = event.scoringMode !== null && event.effectiveScoringMode !== tierDefault;
  return (
    <>
      {event.tier === "Social" && <span className="badge info">Social</span>}
      {overridden && (
        <span className="badge info">
          {event.effectiveScoringMode === "Social" ? "flat points" : "placings"}
        </span>
      )}
      {!event.scoresPoints && <span className="badge muted">no points</span>}
    </>
  );
}
