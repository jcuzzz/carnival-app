import type { EventTier, SchoolRecord } from "../../api/types";
import { beatsRecord } from "./standingRecords";

export function RecordBadge({
  record,
  value,
  tier,
}: {
  record: SchoolRecord | undefined;
  value: number | null | undefined;
  /** Required so no surface can badge tier-blind: only Competitive events mint records. */
  tier: EventTier | undefined;
}) {
  if (tier !== "Competitive" || record === undefined || !beatsRecord(record, value)) return null;
  return (
    <span
      className="badge ok"
      title={`Beats the school record: ${record.formattedValue}, ${record.holderDisplayName}`}
    >
      {" "}
      record
    </span>
  );
}
