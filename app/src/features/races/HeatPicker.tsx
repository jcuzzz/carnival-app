import { Picker, type PickerItem } from "../ui/Picker";
import type { Heat } from "./heats";

/** One heat and how much of it is done, in the division selector's arithmetic. */
export interface HeatOption extends Heat {
  /** `progressText` over this heat's own rows — "3/8", or "8 entered" (Social). */
  count: string;
  complete: boolean;
}

export function HeatPicker({
  heats,
  selectedRaceId,
  hrefOf,
  onAdd,
  addBlocked,
}: {
  heats: readonly HeatOption[];
  /** Null for the prospective heat — the only one a raceless division has. */
  selectedRaceId: string | null;
  hrefOf: (raceId: string) => string;
  /** Null with nobody signed in: minting a heat is an actor-stamped act. */
  onAdd: (() => void) | null;
  /** Why "New heat" is dead, said inside the item. */
  addBlocked: string | undefined;
}) {
  const selected = heats.find((heat) => heat.raceId === selectedRaceId);
  const items: PickerItem[] = heats.map((heat) => ({
    key: heat.raceId ?? "prospective",
    label: heat.label,
    ...(heat.raceId !== null && heat.raceId !== selectedRaceId
      ? { href: hrefOf(heat.raceId) }
      : {}),
    current: heat.raceId === selectedRaceId,
    // "true", not "page": only the division may claim to be the page; a heat is a modifier.
    ariaCurrent: "true",
    count: heat.count,
    complete: heat.complete,
  }));
  if (onAdd)
    items.push({
      key: "add",
      label: "New heat",
      onSelect: onAdd,
      disabled: addBlocked !== undefined,
      disabledReason: addBlocked,
    });

  // No `axisLabel`: "Heat 2" names its axis, and a prefix would read "Heat Heat 2". With the races
  // read down, "No heat" still reads as the heat axis.
  return (
    <div className="heat-selector" role="group" aria-label="Heats">
      <Picker
        value={selected?.label ?? "No heat"}
        valueDetail={selected?.count}
        items={items}
      />
    </div>
  );
}
