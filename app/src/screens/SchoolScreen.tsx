import { hasRole, useSession, type Session } from "../auth/session";
import { SignInOffer, useAccessGate } from "../features/ui/SignInOffer";
import { href } from "../router";

// Row visibility is a nav rule; every section refuses again.

interface HubRow {
  path: string;
  title: string;
  summary: string;
  visible: (session: Session | null) => boolean;
}

const schoolAdmin = (session: Session | null): boolean =>
  hasRole(session, "SystemAdministrator");

const carnivalAdmin = (session: Session | null): boolean =>
  hasRole(session, "CarnivalAdministrator", "SystemAdministrator");

const ROWS: readonly HubRow[] = [
  {
    path: "/school/settings",
    title: "School settings",
    summary: "The school's name, its brand colour and its houses",
    visible: schoolAdmin,
  },
  {
    path: "/school/roles",
    title: "Roles",
    summary: "Who holds which role, and the reason each was granted",
    visible: schoolAdmin,
  },
  {
    path: "/import",
    title: "Roster import",
    summary: "Load a student export, review what it would change, then promote it",
    visible: schoolAdmin,
  },
  {
    path: "/school/templates",
    title: "Event templates",
    summary: "Preview what a deployed template would change in the catalogue, then apply it",
    visible: schoolAdmin,
  },
  {
    path: "/school/events",
    title: "Event catalogue",
    summary: "Every event the school holds — add one, edit one, or retire one",
    visible: carnivalAdmin,
  },
  {
    path: "/school/records",
    title: "School records",
    summary: "The record book, and the one way to withdraw a record minted in error",
    visible: carnivalAdmin,
  },
];

export default function SchoolScreen() {
  const session = useSession();
  const gate = useAccessGate(true);
  const rows = ROWS.filter((row) => row.visible(session));

  if (gate === "loading") return <p>Loading…</p>;
  if (gate === "signedOut")
    return (
      <SignInOffer title="School">
        <p>
          Sign in to reach the school-wide screens — settings, roles, the event catalogue and the
          record book.
        </p>
      </SignInOffer>
    );

  return (
    <>
      <h1>School</h1>
      <p className="row-sub">
        Everything that belongs to the school rather than to one carnival. What a carnival owns —
        its divisions, its program, its results — stays on the carnival.
      </p>
      {rows.length === 0 ? (
        <p>
          Nothing here is available to your roles. Ask a system administrator if you need one of
          the school-wide screens.
        </p>
      ) : (
        <ul className="row-list card">
          {rows.map((row) => (
            <li key={row.path}>
              <a className="row-link" href={href(row.path)}>
                <span className="row-main">
                  <span className="row-title">{row.title}</span>
                  <span className="row-sub">{row.summary}</span>
                </span>
              </a>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}
