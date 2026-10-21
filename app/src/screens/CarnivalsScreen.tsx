import { useEffect, useMemo, useState } from "react";
import { ApiError } from "../api/client";
import { keys } from "../api/queryKeys";
import { listCarnivals } from "../api/resources";
import { useApiQuery } from "../api/useApiQuery";
import { hasRole, useSession } from "../auth/session";
import { landingPath } from "../carnivals/landing";
import { getSelectedCarnival, selectCarnival } from "../carnivals/selectedCarnival";
import { SWITCHER_ROW_LIMIT, switcherList } from "../carnivals/switcherModel";
import { NewCarnivalForm } from "../features/carnivals/NewCarnivalForm";
import { SignInOffer, useAccessGate } from "../features/ui/SignInOffer";
import { href } from "../router";
import { Freshness } from "../shell/Freshness";

// `autoSelect` only from "/": an official who came here to switch must not be bounced out. It reads
// the whole list, never the bounded rows, so the search box cannot change what it picks.
export default function CarnivalsScreen({ autoSelect = false }: { autoSelect?: boolean }) {
  const session = useSession();
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator");
  const gate = useAccessGate(true);
  const carnivals = useApiQuery(keys.carnivals(), listCarnivals, { enabled: gate === "allowed" });
  const [showCreate, setShowCreate] = useState(false);
  const [search, setSearch] = useState("");

  const list = carnivals.data;
  useEffect(() => {
    if (!autoSelect || !list || getSelectedCarnival()) return;
    const open = list.filter((c) => c.status === "Open");
    if (open.length === 1) selectCarnival(open[0]!);
  }, [autoSelect, list]);

  const view = useMemo(() => switcherList(list ?? [], search), [list, search]);
  const searchable = view.total > SWITCHER_ROW_LIMIT;
  const lapsed = carnivals.error instanceof ApiError && carnivals.error.status === 401;

  if (gate === "loading") return <p>Loading…</p>;
  if (gate === "signedOut" || lapsed)
    return (
      <SignInOffer title="Carnivals">
        <p>Sign in to see the school's carnivals and record results.</p>
      </SignInOffer>
    );

  return (
    <>
      <h1>Carnivals</h1>
      {list && (
        <p className="row-sub">
          {view.total} carnival{view.total === 1 ? "" : "s"}
          <Freshness updatedAt={carnivals.updatedAt} isRefetching={carnivals.isRefetching} />
        </p>
      )}
      {carnivals.loading && <p>Loading…</p>}
      {carnivals.error && (
        <div className="alert error" role="alert">
          {carnivals.error.message}
        </div>
      )}
      {searchable && (
        <form className="entry-form" onSubmit={(e) => e.preventDefault()}>
          <label className="visually-hidden" htmlFor="carnival-search">
            Search carnivals
          </label>
          <input
            id="carnival-search"
            type="search"
            className="search-grow"
            placeholder="Search by name, year or venue"
            enterKeyHint="search"
            autoComplete="off"
            autoCapitalize="off"
            spellCheck={false}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </form>
      )}
      {view.truncated && (
        <p className="row-sub">
          Showing the first {SWITCHER_ROW_LIMIT} of {view.matched} — type to narrow.
        </p>
      )}
      {list && view.total === 0 && <p>No carnivals yet.</p>}
      {list && view.total > 0 && view.matched === 0 && <p>No carnivals match that search.</p>}
      {view.rows.length > 0 && (
        <ul className="row-list card">
          {view.rows.map((carnival) => (
            <li key={carnival.id}>
              <a
                className="row-link"
                href={href(landingPath(carnival.id, carnival.status, canManage))}
              >
                <span className="row-main">
                  <span className="row-title">{carnival.name}</span>
                  <span className="row-sub">
                    {carnival.type} · {carnival.date}
                    {carnival.venue ? ` · ${carnival.venue}` : ""}
                  </span>
                </span>
                <span className={`badge ${carnival.status === "Open" ? "ok" : "muted"}`}>
                  {carnival.status}
                </span>
              </a>
            </li>
          ))}
        </ul>
      )}

      {canManage && !showCreate && (
        <div className="button-row">
          <button className="primary" onClick={() => setShowCreate(true)}>
            New carnival
          </button>
        </div>
      )}
      {canManage && showCreate && <NewCarnivalForm onCancel={() => setShowCreate(false)} />}
    </>
  );
}
