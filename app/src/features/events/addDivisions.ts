import { DIRECT_WRITE_NETWORK_ERROR_TEXT, errorText } from "../../api/errorText";
import { getCarnivalSettings, updateCarnivalSettings } from "../../api/resources";
import type { DivisionProfile, GenerateDivisionRequest } from "../../api/types";
import { allCellKeys, buildDivisionGrid, tickedSpecs } from "./divisionGridModel";

/** The failure sentence's last word, composed per press as the raced one is:
 *  the setup list adds, the new-event screen creates. */
export type NothingWas = "added" | "created";

export type EnsureProfileResult =
  | { kind: "ready" }
  /** A profile was saved elsewhere since the screen loaded — nothing written. */
  | { kind: "raced" }
  | { kind: "failed"; text: string };

/**
 * The roster default becomes the carnival's profile before generation. Settings are re-read: a
 * profile saved elsewhere would be overwritten by ages nobody saw (equal ages are our own save).
 */
export async function ensureProfile(
  carnivalId: string,
  defaultProfile: DivisionProfile | null,
  onSettingsChanged: () => void,
  nothingWas: NothingWas,
): Promise<EnsureProfileResult> {
  if (defaultProfile === null) return { kind: "ready" };
  try {
    const stored = (await getCarnivalSettings(carnivalId)).divisions;
    if (stored != null) {
      onSettingsChanged();
      return sameProfile(stored, defaultProfile) ? { kind: "ready" } : { kind: "raced" };
    }
    await updateCarnivalSettings(carnivalId, { divisions: defaultProfile });
    onSettingsChanged();
    return { kind: "ready" };
  } catch (err) {
    return {
      kind: "failed",
      text:
        `The ages couldn’t be saved, so nothing was ${nothingWas}: ` +
        `${errorText(err, DIRECT_WRITE_NETWORK_ERROR_TEXT)}`,
    };
  }
}

/** Every division the profile contests, in the grid's row-major order. Empty
 *  where the profile is not configured, which is what disables the press. */
export function fullMatrixSpecs(
  profile: DivisionProfile | null | undefined,
): GenerateDivisionRequest[] {
  const model = buildDivisionGrid(profile, []);
  return tickedSpecs(model, allCellKeys(model));
}

function sameProfile(a: DivisionProfile, b: DivisionProfile): boolean {
  return (
    sameList(a.ageGroups, b.ageGroups) &&
    sameList(a.categories, b.categories) &&
    sameList(a.tiers, b.tiers)
  );
}

function sameList<T>(a: readonly T[] | null, b: readonly T[] | null): boolean {
  const left = a ?? [];
  const right = b ?? [];
  return left.length === right.length && left.every((item, i) => item === right[i]);
}

/** Non-null only while the roster default is what the divisions come from. */
export function rosterDefaultToSave(
  profile: DivisionProfile | null | undefined,
  rosterDefault: DivisionProfile | null | undefined,
): DivisionProfile | null {
  return profile == null ? (rosterDefault ?? null) : null;
}
