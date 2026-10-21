// A cached read looks as current as a live one; pass the oldest `updatedAt` of a composed screen.
export function Freshness({
  updatedAt,
  isRefetching = false,
}: {
  updatedAt: number | undefined;
  isRefetching?: boolean;
}) {
  if (isRefetching) return <span aria-live="polite"> · refreshing…</span>;
  if (updatedAt === undefined) return null;
  return (
    <span className="freshness"> · updated {formatClock(updatedAt)}</span>
  );
}

// Local wall-clock hh:mm — the only form that means anything on a pool deck.
export function formatClock(epochMs: number): string {
  return new Date(epochMs).toLocaleTimeString(undefined, {
    hour: "2-digit",
    minute: "2-digit",
  });
}

export function oldestUpdatedAt(...stamps: (number | undefined)[]): number | undefined {
  const known = stamps.filter((s): s is number => s !== undefined);
  return known.length === 0 ? undefined : Math.min(...known);
}
