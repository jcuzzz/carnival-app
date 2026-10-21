/** The catalogue's own row model: one list, grouped by sport. */

import type { EventDefinition } from "../../api/types";
import { formatMeasure } from "../../results/measureFormat";
import { eventTypeSummary } from "./eventLabels";

/** "AT-LJ · Best of 3 attempts (metres)", or a bar ladder's two suggestions. */
export function definitionSummary(
  definition: Pick<EventDefinition, "code" | "measureType" | "units" | "attemptCount" | "ladder">,
): string {
  const type = eventTypeSummary(definition.measureType, definition.attemptCount);
  const parts = [definition.code, `${type} (${definition.units})`];
  if (definition.ladder != null)
    parts.push(
      `suggests ${formatMeasure("Height", definition.ladder.start)}, ` +
        `+${formatMeasure("Height", definition.ladder.step)}`,
    );
  return parts.join(" · ");
}

/** Sports A–Z; rows inside a group by name. The server refuses a blank sport. */
export function groupBySport(
  definitions: readonly EventDefinition[],
): [string, EventDefinition[]][] {
  const groups = new Map<string, EventDefinition[]>();
  for (const definition of definitions) {
    const rows = groups.get(definition.sport);
    if (rows) rows.push(definition);
    else groups.set(definition.sport, [definition]);
  }
  for (const rows of groups.values()) rows.sort((a, b) => a.name.localeCompare(b.name));
  return [...groups.entries()].sort(([a], [b]) => a.localeCompare(b));
}

/** The sports already in the catalogue, offered before a free-typed one. */
export function knownSports(definitions: readonly EventDefinition[]): string[] {
  return [...new Set(definitions.map((d) => d.sport))].sort((a, b) => a.localeCompare(b));
}
