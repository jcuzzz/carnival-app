import { useMemo, useState } from "react";
import { errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import {
  downloadReportCsv,
  getCarnival,
  getCarnivalSettings,
  getRecords,
  getResultsReport,
  getStandings,
  listCarnivalEvents,
  type ReportName,
} from "../api/resources";
import type { MeasureType, SchoolRecord } from "../api/types";
import { useApiQuery } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import { carnivalSport } from "../features/events/eventLabels";
import { ResultsBookView } from "../features/reports/ResultsBookView";
import { saveFile } from "../features/reports/download";
import { StandingsTable } from "../features/standings/StandingsTable";
import { href } from "../router";
import { useConnectivity } from "../shell/useOnline";

const READ_ERROR_TEXT = "Can’t reach the server, so this report couldn’t be read.";
/** The same failure with a cached report ON SCREEN: it loaded, it may be old. */
const STALE_READ_ERROR_TEXT = "Can’t reach the server, so this report may be out of date.";
// No outbox behind an export, so the "saved on this device" default would be false.
const EXPORT_ERROR_TEXT =
  "Can’t reach the server, so no file was produced. Exports are generated on the server; try again once you’re back online.";
// Said on the button so the refusal is legible before the request; the 409 says the same.
const DISTRICT_NOT_FINALISED_TEXT =
  "The district list is produced from published results. Finalise the carnival to produce it.";

// Online-only: a device holding only its own captures has nothing honest to report from.
export default function ReportsScreen({ carnivalId }: { carnivalId: string }) {
  const session = useSession();
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator");
  const offline = useConnectivity() !== "connected";
  // Gated: a refused official should not spend a venue's uplink on four reads
  // whose screen they will never see.
  const enabled = { enabled: canManage };
  const carnival = useApiQuery(keys.carnival(carnivalId), () => getCarnival(carnivalId), enabled);
  const standings = useApiQuery(keys.standings(carnivalId), () => getStandings(carnivalId), enabled);
  const settings = useApiQuery(
    keys.carnivalSettings(carnivalId),
    () => getCarnivalSettings(carnivalId),
    enabled,
  );
  const results = useApiQuery(
    keys.resultsReport(carnivalId),
    () => getResultsReport(carnivalId),
    enabled,
  );
  // The book is school-wide. Additive: a failure or an empty book leaves the rest of the page.
  // "Other" names no sport, so it reads the whole book.
  const sport = carnival.data ? carnivalSport(carnival.data.type) || undefined : undefined;
  const records = useApiQuery(keys.records(sport), () => getRecords(sport), {
    enabled: canManage && carnival.data !== undefined,
  });
  // Report rows carry no measure; the division list is where it is known.
  const events = useApiQuery(
    keys.carnivalEvents(carnivalId),
    () => listCarnivalEvents(carnivalId),
    enabled,
  );
  const measureByEvent = useMemo(() => {
    const byEvent = new Map<string, MeasureType>();
    for (const event of events.data ?? []) byEvent.set(event.id, event.measureType);
    return byEvent;
  }, [events.data]);

  const [downloading, setDownloading] = useState<ReportName | null>(null);
  const [exportError, setExportError] = useState<string | null>(null);

  async function download(report: ReportName): Promise<void> {
    setExportError(null);
    setDownloading(report);
    try {
      saveFile(await downloadReportCsv(carnivalId, report));
    } catch (err) {
      setExportError(errorText(err, EXPORT_ERROR_TEXT));
    } finally {
      setDownloading(null);
    }
  }

  if (!canManage)
    return (
      <>
        <h1>Reports</h1>
        <div className="alert error" role="alert">
          Reports and exports need the carnival administrator role.
        </div>
        <p>
          <a href={href(`/carnivals/${carnivalId}/dashboard`)}>← Back to the dashboard</a>
        </p>
      </>
    );

  const c = carnival.data;
  const loadError = standings.error ?? results.error;
  const hasData = standings.data !== undefined && results.data !== undefined;
  const finalised = c?.status === "Finalised";
  const book = records.data ?? [];
  // Records this carnival set, once it is published. `setOn` is the carnival's
  // own date, so within one sport it names this carnival's mints.
  const setHere = finalised
    ? book.filter((r) => r.setOn === c!.date && r.carnivalName === c!.name)
    : [];

  return (
    <>
      <div className="button-row no-print">
        <a href={href(`/carnivals/${carnivalId}/manage`)}>← Manage</a>
        <button type="button" className="primary" onClick={() => window.print()}>
          🖨 Print
        </button>
        <button type="button" disabled={offline || downloading !== null} onClick={() => void download("standings")}>
          {downloading === "standings" ? "Preparing…" : "Download standings CSV"}
        </button>
        <button type="button" disabled={offline || downloading !== null} onClick={() => void download("results")}>
          {downloading === "results" ? "Preparing…" : "Download results CSV"}
        </button>
        <button
          type="button"
          disabled={offline || downloading !== null || !finalised}
          onClick={() => void download("district")}
        >
          {downloading === "district" ? "Preparing…" : "Download district CSV"}
        </button>
      </div>
      {c && !finalised && <p className="row-sub no-print">{DISTRICT_NOT_FINALISED_TEXT}</p>}

      <h1>Reports</h1>
      {c && (
        <p className="row-sub">
          {c.name} · {c.type} · {c.date} · {c.status}
        </p>
      )}

      {offline && (
        <div className="alert notice no-print" role="status">
          Can’t reach the server, so the CSV exports are unavailable — a file is generated by the
          server, not from anything held on this device. Reconnect and the buttons come back.
        </div>
      )}
      {exportError && (
        <div className="alert error no-print" role="alert">
          {exportError}
        </div>
      )}
      {loadError && (
        <div className="alert error no-print" role="alert">
          {errorText(loadError, hasData ? STALE_READ_ERROR_TEXT : READ_ERROR_TEXT)}
        </div>
      )}

      <section className="subsection">
        <h2>
          House standings{" "}
          {standings.data?.provisional && <span className="badge warn">provisional</span>}
        </h2>
        {standings.loading && <p>Loading standings…</p>}
        {standings.data && standings.data.houses.length === 0 && (
          <p>No points yet — standings appear as results and bonus points are recorded.</p>
        )}
        {standings.data && standings.data.houses.length > 0 && (
          <StandingsTable data={standings.data} settings={settings.data} />
        )}
      </section>

      <section className="subsection">
        <h2>Results book</h2>
        {results.loading && <p>Loading the results book…</p>}
        {results.data && (
          <ResultsBookView
            report={results.data}
            records={book}
            measureByEvent={measureByEvent}
          />
        )}
      </section>

      <section className="subsection">
        <h2>School records</h2>
        {records.loading && <p>Loading the record book…</p>}
        {records.data && book.length === 0 && (
          <p>
            No {sport ? `${sport.toLowerCase()} records` : "records"} stand yet — the book starts
            at the first finalised carnival.
          </p>
        )}
        {book.length > 0 && <RecordsTable records={book} />}
        {finalised && (
          <>
            <h3>Set at this carnival</h3>
            {setHere.length === 0 ? (
              <p>No records were set at this carnival.</p>
            ) : (
              <RecordsTable records={setHere} />
            )}
          </>
        )}
      </section>
    </>
  );
}

function RecordsTable({ records }: { records: readonly SchoolRecord[] }) {
  return (
    <div className="table-wrap">
      <table>
        <thead>
          <tr>
            <th scope="col">Event</th>
            <th scope="col">Division</th>
            <th scope="col">Record</th>
            <th scope="col">Holder</th>
            <th scope="col">Set</th>
          </tr>
        </thead>
        <tbody>
          {records.map((record) => (
            <tr key={`${record.eventDefinitionId}:${record.ageGroup}:${record.category}`}>
              <td>{record.eventName}</td>
              <td>{record.division}</td>
              <td className="mono">{record.formattedValue}</td>
              <td>{record.holderDisplayName}</td>
              <td>{record.setOn ?? "—"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
