import { keys } from "../api/queryKeys";
import { getCarnival, getCarnivalSchedule, listCarnivalEvents } from "../api/resources";
import { useApiQuery } from "../api/useApiQuery";
import { ProgramView } from "../features/schedule/ProgramView";
import { href } from "../router";

// The print stylesheet's arithmetic: portrait A4 holds four 42mm columns, landscape six.
const LANDSCAPE_ABOVE_STREAMS = 4;

// Advice one stream earlier; it also survives Safari ignoring `@page { size }`.
const HINT_ABOVE_STREAMS = LANDSCAPE_ABOVE_STREAMS - 1;

// Everything but the program itself is no-print, so the printout is the program alone.
export default function ProgramScreen({ carnivalId }: { carnivalId: string }) {
  const carnival = useApiQuery(keys.carnival(carnivalId), () => getCarnival(carnivalId));
  const schedule = useApiQuery(keys.schedule(carnivalId), () => getCarnivalSchedule(carnivalId));
  const events = useApiQuery(keys.carnivalEvents(carnivalId), () => listCarnivalEvents(carnivalId));

  const c = carnival.data;
  const loadError = carnival.error ?? schedule.error ?? events.error;
  const streamCount = schedule.data?.streams.length ?? 0;

  // All three reads or nothing: a partial page printed in that window is wrong rather than late.
  // Bundled, not a boolean, so the render cannot reach a payload the gate has not seen.
  const ready =
    c && schedule.data && events.data
      ? { carnival: c, schedule: schedule.data, events: events.data }
      : null;

  return (
    <>
      <div className="button-row no-print">
        <a href={href(`/carnivals/${carnivalId}`)}>← Carnival</a>
        <a href={href(`/carnivals/${carnivalId}/schedule`)}>Edit the program</a>
        <button type="button" className="primary" onClick={() => window.print()}>
          🖨 Print
        </button>
      </div>

      {loadError && (
        <div className="alert error no-print" role="alert">
          {loadError.message}
        </div>
      )}
      {!ready && !loadError && <p>Loading…</p>}

      {ready && streamCount > HINT_ABOVE_STREAMS && (
        <p className="row-sub no-print">
          {streamCount} streams — print in landscape; streams wrap onto extra rows when they do
          not fit.
        </p>
      )}

      {/* `@page` cannot be scoped to a class, so it is emitted only where needed. Outside
          `@media print` deliberately: it applies to paged output only. */}
      {ready && streamCount > LANDSCAPE_ABOVE_STREAMS && (
        <style>{"@page { size: landscape; }"}</style>
      )}

      {ready && (
        <ProgramView carnival={ready.carnival} schedule={ready.schedule} events={ready.events} />
      )}
    </>
  );
}
