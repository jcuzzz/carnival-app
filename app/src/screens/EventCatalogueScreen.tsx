import { useState } from "react";
import { errorText } from "../api/errorText";
import { keys } from "../api/queryKeys";
import { listEventDefinitions } from "../api/resources";
import type { EventDefinition } from "../api/types";
import { useApiQuery } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import { definitionSummary, groupBySport } from "../features/events/catalogueRows";
import { usageSummary } from "../features/events/setupRows";
import { SignInOffer, useAccessGate } from "../features/ui/SignInOffer";
import { href } from "../router";
import { Freshness } from "../shell/Freshness";
import { useConnectivity } from "../shell/useOnline";

// Retired definitions stay listed, badged: they must not vanish from the list that sent you there.
export default function EventCatalogueScreen() {
  const session = useSession();
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator");
  const connectivity = useConnectivity();
  const definitions = useApiQuery(keys.eventDefinitions(true), () => listEventDefinitions(true), {
    enabled: canManage,
  });
  const [q, setQ] = useState("");
  const [sport, setSport] = useState("");
  const gate = useAccessGate(canManage);

  if (gate === "loading") return <p>Loading…</p>;
  if (gate === "signedOut") {
    return (
      <SignInOffer title="Event catalogue">
        <p>Sign in to read the school's event catalogue.</p>
      </SignInOffer>
    );
  }
  if (gate === "refused") {
    return (
      <>
        <h1>Event catalogue</h1>
        <div className="alert error" role="alert">
          Administering the event catalogue needs the carnival administrator role.
        </div>
        <p>
          <a href={href("/carnivals")}>← Back to carnivals</a>
        </p>
      </>
    );
  }

  const all = definitions.data ?? [];
  const sports = [...new Set(all.map((d) => d.sport))].sort((a, b) => a.localeCompare(b));
  const filtered = all.filter((definition) => {
    if (sport !== "" && definition.sport !== sport) return false;
    if (q.trim() === "") return true;
    const needle = q.trim().toLowerCase();
    return `${definition.name} ${definition.code}`.toLowerCase().includes(needle);
  });
  const groups = groupBySport(filtered);

  return (
    <>
      <p>
        <a href={href("/school")}>← School</a>
      </p>
      <h1>Event catalogue</h1>
      <p className="row-sub">
        Every event the school holds, across every sport. A carnival schedules divisions OF these
        events — editing one here changes it for every carnival that has ever run it.
        <Freshness updatedAt={definitions.updatedAt} isRefetching={definitions.isRefetching} />
      </p>
      {definitions.error && (
        <div className="alert error" role="alert">
          {errorText(definitions.error, "Can’t reach the server, so the catalogue couldn’t be read.")}
        </div>
      )}
      <div className="button-row">
        {connectivity === "connected" ? (
          <a className="button primary-link" href={href("/school/events/new")}>
            New definition
          </a>
        ) : (
          <span className="row-sub">
            Can’t reach the server, so an event can’t be added right now.
          </span>
        )}
      </div>

      {definitions.loading && <p>Loading the event catalogue…</p>}
      {definitions.data && all.length === 0 && <p>The event catalogue is empty.</p>}

      {all.length > 0 && (
        <>
          <div className="filter-bar">
            <input
              className="filter-search"
              placeholder="Filter events by name…"
              value={q}
              onChange={(e) => setQ(e.target.value)}
              aria-label="Filter events by name"
            />
            <select value={sport} onChange={(e) => setSport(e.target.value)} aria-label="Sport">
              <option value="">All sports</option>
              {sports.map((name) => (
                <option key={name} value={name}>
                  {name}
                </option>
              ))}
            </select>
            {(q.trim() !== "" || sport !== "") && (
              <button
                type="button"
                onClick={() => {
                  setQ("");
                  setSport("");
                }}
              >
                Clear
              </button>
            )}
          </div>
          <p className="row-sub" aria-live="polite">
            Showing {filtered.length} of {all.length} events
          </p>
        </>
      )}

      {all.length > 0 && filtered.length === 0 && <p>No events match these filters.</p>}

      {groups.map(([name, rows]) => (
        <section className="subsection" key={name}>
          <h2>{name}</h2>
          <ul className="row-list card">
            {rows.map((definition) => (
              <CatalogueRow key={definition.id} definition={definition} />
            ))}
          </ul>
        </section>
      ))}
    </>
  );
}

function CatalogueRow({ definition }: { definition: EventDefinition }) {
  return (
    <li>
      <div className="entry-row">
        <a className="row-link row-grow" href={href(`/school/events/${definition.id}`)}>
          <span className="row-main">
            <span className="row-title">{definition.name}</span>
            <span className="row-sub">{definitionSummary(definition)}</span>
            <span className="row-sub">{usageSummary(definition.usage)}</span>
          </span>
        </a>
        {!definition.isActive && <span className="badge muted">Retired</span>}
      </div>
    </li>
  );
}
