import { ordinal } from "./ordinal";

/** Fed only by the recorded result: a just-typed time claims no place until the server ranks it. */
export function PlaceBadge({ place }: { place: number | null | undefined }) {
  if (place == null) return null;
  const label = ordinal(place);
  return (
    <span
      className={`badge ${place === 1 ? "first" : "muted"}`}
      title={`Currently ${label}`}
    >
      {" "}
      {label}
    </span>
  );
}
