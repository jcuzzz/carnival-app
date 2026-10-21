import type { CarnivalEvent } from "../../api/types";
import { divisionChipLabel, siblingDivisions } from "./divisions";
import { ageScopeLabel, ageScopeOrder, categoryValueLabel } from "./eventLabels";
import { isAssignedTo } from "./tiles";

export type DivisionAxis = "tier" | "category" | "age";

/** Coarse → fine. A picker is scoped by the current values of the ones above it. */
const AXES: readonly DivisionAxis[] = ["tier", "category", "age"];

const AXIS_LABEL: Record<DivisionAxis, string> = {
  tier: "Tier",
  category: "Category",
  age: "Age group",
};

export interface DivisionChip {
  key: string;
  /** Visible text, and therefore the accessible name (no aria-label anywhere). */
  label: string;
  /** Where the tap goes — the current item's own division. */
  event: CarnivalEvent;
  /** The division on screen: rendered as the current item, never a link. */
  current: boolean;
  assigned: boolean;
}

export interface DivisionPicker {
  axis: DivisionAxis;
  label: string;
  /** Its items carry the whole division, so the count, badge and `aria-current` belong to it. */
  names: boolean;
  /** What the closed button reads: the current division's value on this axis. */
  value: string;
  items: DivisionChip[];
}

export interface DivisionSelectorModel {
  divisions: CarnivalEvent[];
  pickers: DivisionPicker[];
}

function axisValue(event: CarnivalEvent, axis: DivisionAxis): string {
  if (axis === "tier") return event.tier;
  // A null category is a value of its own, never a missing one.
  if (axis === "category") return event.category ?? "";
  return event.ageGroup;
}

// An item is a value among values, not a filter: the uncategorised one opens the
// division that HAS no category, which "All categories" would deny.
export function axisValueLabel(axis: DivisionAxis, value: string): string {
  if (axis === "category") return categoryValueLabel(value === "" ? null : value);
  if (axis === "age") return ageScopeLabel({ ageGroup: value }) ?? "All ages";
  return value;
}

/** The closed button's text: short, and the age exactly as the division carries it. */
function currentValueLabel(axis: DivisionAxis, current: CarnivalEvent): string {
  return axisValueLabel(axis, axisValue(current, axis));
}

/**
 * Nearest on the age ladder, younger on a tie: a dead end on a pool deck is worse than an
 * approximate landing, which names itself.
 */
function nearestAge(candidates: readonly CarnivalEvent[], to: CarnivalEvent): CarnivalEvent {
  const target = ageScopeOrder(to.ageGroup);
  const distance = (d: CarnivalEvent) => Math.abs(ageScopeOrder(d.ageGroup) - target);
  // `candidates` is age-ascending (siblingDivisions), so a strict `<` keeps the
  // first of a tie, which is the younger division.
  return candidates.reduce((best, d) => (distance(d) < distance(best) ? d : best));
}

/**
 * The tapped value, holding the page's value on every other coarse axis (never crossing tier
 * unasked), with the nearest age where the combination has none.
 */
function resolve(
  divisions: readonly CarnivalEvent[],
  current: CarnivalEvent,
  axis: DivisionAxis,
  value: string,
): CarnivalEvent {
  const pool = AXES.filter((a) => a !== axis && a !== "age").reduce((held, a) => {
    const kept = held.filter((d) => axisValue(d, a) === axisValue(current, a));
    // Relaxed rather than emptied: an approximate landing beats an item that opens nothing.
    return kept.length > 0 ? kept : held;
  }, divisions.filter((d) => axisValue(d, axis) === value));
  // `ageScopeOrder` ranks every non-numeric code alike, so the same age must win outright before
  // distance can call two different ages equidistant.
  return pool.find((d) => d.ageGroup === current.ageGroup) ?? nearestAge(pool, current);
}

/** Pickers for one definition's divisions, given the division on screen. */
export function divisionSelectorModel(
  events: readonly CarnivalEvent[] | undefined,
  definitionId: string,
  options: { current: CarnivalEvent; subject?: string | null },
): DivisionSelectorModel {
  const { current, subject = null } = options;
  const divisions = siblingDivisions(events ?? [], { eventDefinitionId: definitionId });

  const axes = AXES.map((axis) => {
    const coarser = AXES.slice(0, AXES.indexOf(axis));
    const scope = divisions.filter((d) =>
      coarser.every((a) => axisValue(d, a) === axisValue(current, a)),
    );
    const values = [...new Set(scope.map((d) => axisValue(d, axis)))];
    // Ages keep the ladder order `siblingDivisions` sorted them into; the other
    // two sort so a move along one axis never reshuffles another.
    return { axis, values: axis === "age" ? values : values.sort((a, b) => a.localeCompare(b)) };
  })
    // A tier picker on a definition with one tier would offer the page itself;
    // the twin hazard is instead carried on the naming axis's own face.
    .filter(({ axis, values }) => values.length > (axis === "tier" ? 1 : 0));

  // The naming axis is the finest with a choice: a one-value axis renders static, and a static
  // face carries no count, badge or `aria-current`.
  const naming = axes.filter((a) => a.values.length > 1).at(-1)?.axis ?? null;
  // The suffix belongs to the last thing read on the row, whether or not it opens.
  const lastAxis = axes.at(-1)?.axis ?? null;
  const hasTier = axes.some((a) => a.axis === "tier");

  return {
    divisions,
    pickers: axes.map(({ axis, values }) => ({
      axis,
      label: AXIS_LABEL[axis],
      names: axis === naming,
      value:
        axis === lastAxis && !hasTier && current.tier === "Social"
          ? `${currentValueLabel(axis, current)} · Social`
          : currentValueLabel(axis, current),
      items: values.map((value) => {
        const target = resolve(divisions, current, axis, value);
        return axis === naming
          ? {
              key: value,
              // A Competitive division and its Social twin must not read alike.
              label: divisionChipLabel(target),
              event: target,
              current: target.id === current.id,
              assigned: isAssignedTo(target, subject),
            }
          : {
              key: value,
              label: axisValueLabel(axis, value),
              event: target,
              current: axisValue(current, axis) === value,
              // An assignment is a fact about a DIVISION, and this is a value.
              assigned: false,
            };
      }),
    })),
  };
}
