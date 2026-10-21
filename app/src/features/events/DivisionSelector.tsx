import type { CarnivalEvent } from "../../api/types";
import { href } from "../../router";
import { Picker, PickerValue, type PickerItem } from "../ui/Picker";
import { axisValueLabel, divisionSelectorModel, type DivisionPicker } from "./divisionSelectorModel";
import { categoryValueLabel } from "./eventLabels";
import { progressText, sumProgress, type QueuedProgress } from "./progress";

/** Every item is a real link to a division's own route; a group, not a landmark. */
export function DivisionSelector({
  definitionId,
  events,
  current,
  queued,
  subject,
}: {
  definitionId: string;
  events: CarnivalEvent[] | undefined;
  /** The division on screen — every item resolves relative to it. */
  current: CarnivalEvent;
  /** Undelivered captures from the device outbox. */
  queued?: Map<string, QueuedProgress>;
  /** Marks the divisions this official is assigned to run. */
  subject?: string | null;
}) {
  const model = divisionSelectorModel(events, definitionId, {
    current,
    subject: subject ?? null,
  });

  return (
    <div className="division-selector" role="group" aria-label="Divisions">
      {model.pickers.length === 0 ? (
        // No siblings read — at a venue with no uplink, say. The page still has
        // to state which division it is.
        <>
          <PickerValue axisLabel="Category" value={categoryValueLabel(current.category)} />
          <PickerValue axisLabel="Age group" value={axisValueLabel("age", current.ageGroup)} />
        </>
      ) : (
        model.pickers.map((picker) => (
          <AxisPicker key={picker.axis} picker={picker} queued={queued} />
        ))
      )}
    </div>
  );
}

function AxisPicker({
  picker,
  queued,
}: {
  picker: DivisionPicker;
  queued: Map<string, QueuedProgress> | undefined;
}) {
  if (picker.items.length < 2)
    return <PickerValue axisLabel={picker.label} value={picker.value} />;
  return (
    <Picker
      axisLabel={picker.label}
      value={picker.value}
      items={picker.items.map((item): PickerItem => {
        const progress = picker.names ? sumProgress([item.event], queued) : null;
        return {
          key: item.key,
          label: item.label,
          href: href(`/carnivals/${item.event.carnivalId}/events/${item.event.id}`),
          current: item.current,
          ariaCurrent: picker.names ? "page" : undefined,
          count: progress ? progressText(progress) : undefined,
          // Ticked only where results were expected: "8 entered" on a Social
          // division is not a division that finished swimming.
          complete:
            progress !== null &&
            progress.expectedCount > 0 &&
            progress.resultCount >= progress.expectedCount,
          badge: item.assigned ? "yours" : undefined,
        };
      })}
    />
  );
}
