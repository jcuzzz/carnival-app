import { Suspense, lazy, useEffect, type ReactNode } from "react";
import AppShell from "./shell/AppShell";
import { isDemoBuild } from "./api/client";
import {
  entraAuthEnabled,
  previousSessionHasEnded,
  serverSessionEnabled,
  useEntraSessionSettled,
} from "./auth/entra";
import { hasRole, useSession } from "./auth/session";
import { PRODUCT_NAME, useBranding } from "./branding/branding";
import { landingPath } from "./carnivals/landing";
import { adoptCarnival, useSelectedCarnival } from "./carnivals/selectedCarnival";
import { useCarnivalContext } from "./carnivals/useCarnivalContext";
import { SignInOffer } from "./features/ui/SignInOffer";
import { href, matchPath, navigate, usePath, useRouteQuery } from "./router";

// One chunk per screen, precached by the service worker, so the split costs nothing offline.
// Works only while no screen imports another.
const CarnivalsScreen = lazy(() => import("./screens/CarnivalsScreen"));
const CarnivalDetailScreen = lazy(() => import("./screens/CarnivalDetailScreen"));
const DashboardScreen = lazy(() => import("./screens/DashboardScreen"));
const DemoLandingScreen = lazy(() => import("./screens/DemoLandingScreen"));
const DevLoginScreen = lazy(() => import("./screens/DevLoginScreen"));
const DivisionsProfileScreen = lazy(() => import("./screens/DivisionsProfileScreen"));
const SignInScreen = lazy(() => import("./screens/SignInScreen"));
const EventCatalogueScreen = lazy(() => import("./screens/EventCatalogueScreen"));
const EventEditorScreen = lazy(() => import("./screens/EventEditorScreen"));
const EventResultsScreen = lazy(() => import("./screens/EventResultsScreen"));
const EventSetupScreen = lazy(() => import("./screens/EventSetupScreen"));
const ImportReviewScreen = lazy(() => import("./screens/ImportReviewScreen"));
const ManageScreen = lazy(() => import("./screens/ManageScreen"));
const NewEventScreen = lazy(() => import("./screens/NewEventScreen"));
const OutboxScreen = lazy(() => import("./screens/OutboxScreen"));
const ProgramScreen = lazy(() => import("./screens/ProgramScreen"));
const RecordsAdminScreen = lazy(() => import("./screens/RecordsAdminScreen"));
const ReportsScreen = lazy(() => import("./screens/ReportsScreen"));
const ScheduleBuilderScreen = lazy(() => import("./screens/ScheduleBuilderScreen"));
const SchoolRolesScreen = lazy(() => import("./screens/SchoolRolesScreen"));
const SchoolScreen = lazy(() => import("./screens/SchoolScreen"));
const SchoolSettingsScreen = lazy(() => import("./screens/SchoolSettingsScreen"));
const SchoolTemplatesScreen = lazy(() => import("./screens/SchoolTemplatesScreen"));

export default function App() {
  const path = usePath();
  const query = useRouteQuery();
  const session = useSession();
  const settled = useEntraSessionSettled();
  // ADR-0061 §5: a demo visitor without a sandbox sees only the landing page.
  const demoLanding = isDemoBuild() && serverSessionEnabled() && session === null;

  let screen: ReactNode;
  let screenName: string;
  let params: Record<string, string> | null;
  let carnivalId: string | null = null;
  if (demoLanding) {
    screen = settled ? <DemoLandingScreen expired={previousSessionHasEnded()} /> : <p>Loading…</p>;
    screenName = "Demo";
  } else if (import.meta.env.DEV && path === "/dev/demo-landing") {
    // The screenshot pass's view of the landing page; the dev server is never a demo build.
    screen = <DemoLandingScreen expired={new URLSearchParams(query).has("expired")} />;
    screenName = "Demo";
  } else if (path === "/") {
    screen = <Home />;
    screenName = "Carnivals";
  } else if (path === "/carnivals") {
    screen = <CarnivalsScreen />;
    screenName = "Carnivals";
  } else if (path === "/login") {
    // A demo build has one identity per visitor, so there is nobody to switch to.
    screen = entraAuthEnabled() ? (
      <SignInScreen />
    ) : isDemoBuild() && serverSessionEnabled() ? (
      <Redirect to="/" />
    ) : (
      <DevLoginScreen />
    );
    screenName = "Sign in";
  } else if (path === "/import") {
    screen = <ImportReviewScreen />;
    screenName = "Roster import";
  } else if (path === "/school") {
    screen = <SchoolScreen />;
    screenName = "School";
  } else if (path === "/school/settings") {
    // Not the gate: the API refuses anyone but a SystemAdministrator.
    screen = <SchoolSettingsScreen />;
    screenName = "School settings";
  } else if (path === "/school/roles") {
    screen = <SchoolRolesScreen />;
    screenName = "Roles";
  } else if (path === "/school/templates") {
    screen = <SchoolTemplatesScreen />;
    screenName = "Event templates";
  } else if (path === "/school/events/new") {
    // Before the :definitionId route: identical segment counts, so the literal must win.
    screen = <NewEventScreen carnivalId={null} />;
    screenName = "New definition";
  } else if ((params = matchPath("/school/events/:definitionId", path))) {
    screen = <EventEditorScreen carnivalId={null} definitionId={params.definitionId!} />;
    screenName = "Edit event";
  } else if (path === "/school/events") {
    screen = <EventCatalogueScreen />;
    screenName = "Event catalogue";
  } else if (path === "/school/records") {
    screen = <RecordsAdminRoute />;
    screenName = "School records";
  } else if (path === "/records-admin") {
    // The old address is in the runbook.
    screen = <Redirect to="/school/records" />;
    screenName = "School records";
  } else if (path === "/outbox") {
    screen = <OutboxRoute />;
    screenName = "Outbox";
  } else if ((params = matchPath("/carnivals/:carnivalId/setup/events/new", path))) {
    // Before the :definitionId route: identical segment counts, so the literal must win.
    carnivalId = params.carnivalId!;
    screen = <NewEventScreen carnivalId={carnivalId} />;
    screenName = "Add an event";
  } else if ((params = matchPath("/carnivals/:carnivalId/setup/events/:definitionId", path))) {
    // Under /setup because /carnivals/:id/events/:id is the capture screen, keyed by division id.
    carnivalId = params.carnivalId!;
    screen = <EventEditorScreen carnivalId={carnivalId} definitionId={params.definitionId!} />;
    screenName = "Edit event";
  } else if ((params = matchPath("/carnivals/:carnivalId/setup/events", path))) {
    carnivalId = params.carnivalId!;
    screen = <EventSetupScreen carnivalId={carnivalId} />;
    screenName = "Event setup";
  } else if ((params = matchPath("/carnivals/:carnivalId/setup/divisions", path))) {
    carnivalId = params.carnivalId!;
    screen = <DivisionsProfileScreen carnivalId={carnivalId} />;
    screenName = "Divisions profile";
  } else if ((params = matchPath("/carnivals/:carnivalId/events/:eventId", path))) {
    carnivalId = params.carnivalId!;
    screen = <EventResultsScreen carnivalId={carnivalId} eventId={params.eventId!} />;
    screenName = "Event results";
  } else if ((params = matchPath("/carnivals/:carnivalId/dashboard", path))) {
    carnivalId = params.carnivalId!;
    screen = <DashboardScreen carnivalId={carnivalId} />;
    screenName = "Dashboard";
  } else if ((params = matchPath("/carnivals/:carnivalId/standings", path))) {
    // The dashboard's old address, kept so bookmarks, printed links and history still land.
    carnivalId = params.carnivalId!;
    screen = <Redirect to={`/carnivals/${carnivalId}/dashboard`} />;
    screenName = "Dashboard";
  } else if ((params = matchPath("/carnivals/:carnivalId/manage", path))) {
    carnivalId = params.carnivalId!;
    screen = <ManageScreen carnivalId={carnivalId} />;
    screenName = "Manage carnival";
  } else if ((params = matchPath("/carnivals/:carnivalId/reports", path))) {
    carnivalId = params.carnivalId!;
    screen = <ReportsScreen carnivalId={carnivalId} />;
    screenName = "Reports";
  } else if ((params = matchPath("/carnivals/:carnivalId/schedule", path))) {
    carnivalId = params.carnivalId!;
    screen = <ScheduleBuilderScreen carnivalId={carnivalId} />;
    // Distinct from the print view's title, or a PWA task switcher shows both identically.
    screenName = "Program builder";
  } else if ((params = matchPath("/carnivals/:carnivalId/program", path))) {
    carnivalId = params.carnivalId!;
    screen = <ProgramScreen carnivalId={carnivalId} />;
    screenName = "Printable program";
  } else if ((params = matchPath("/carnivals/:carnivalId", path))) {
    carnivalId = params.carnivalId!;
    screen = <CarnivalDetailScreen carnivalId={carnivalId} />;
    screenName = "Carnival";
  } else {
    screen = (
      <>
        <h1>Not found</h1>
        <p>
          No screen at <code>{path}</code>.
        </p>
        <p>
          <a href={href("/carnivals")}>← Back to carnivals</a>
        </p>
      </>
    );
    screenName = "Not found";
  }

  useAdoptedCarnival(carnivalId);
  useCarnivalContext();

  // The query is part of the route: a page that changes with it and neither
  // scrolls nor moves focus reads as one that did nothing.
  useRouteChange(`${path}?${query}`, screenName);

  return (
    <AppShell>
      <Suspense fallback={<p>Loading…</p>}>{screen}</Suspense>
    </AppShell>
  );
}

function Home() {
  const selected = useSelectedCarnival();
  // A real build resolves /me after first paint; deciding now would decide against `roles: []`
  // and never re-decide.
  const settled = useEntraSessionSettled();
  const canManage = hasRole(useSession(), "CarnivalAdministrator", "SystemAdministrator");
  const target = selected && settled ? landingPath(selected.id, selected.status, canManage) : null;
  useEffect(() => {
    // Replace, not push: a Back onto "/" would bounce straight here again.
    if (target) navigate(target, { replace: true });
  }, [target]);
  if (!settled) return <p>Loading…</p>;
  return target ? <p>Loading…</p> : <CarnivalsScreen autoSelect />;
}

// Prints other officials' captured student names, so needs a named session; the chip and banner
// show counts only and stay visible signed out.
function OutboxRoute() {
  const session = useSession();
  if (session) return <OutboxScreen />;
  return (
    <SignInOffer title="Outbox">
      <p>
        Sign in to read this device's queue. It holds captures made by whoever has been using
        this device, including the students they name.
      </p>
    </SignInOffer>
  );
}

function RecordsAdminRoute() {
  const session = useSession();
  if (session) return <RecordsAdminScreen />;
  return (
    <SignInOffer title="School records">
      <p>Sign in to administer the school record book.</p>
    </SignInOffer>
  );
}

// The redirect runs after paint, never in render.
function Redirect({ to }: { to: string }) {
  useEffect(() => navigate(to, { replace: true }), [to]);
  return <p>Loading…</p>;
}

function useAdoptedCarnival(carnivalId: string | null): void {
  useEffect(() => {
    if (carnivalId) adoptCarnival(carnivalId);
  }, [carnivalId]);
}

// A hash router moves neither scroll nor focus; a screen still loading has no <h1>, so <main>
// takes focus.
function useRouteChange(path: string, screenName: string): void {
  const { schoolName } = useBranding();
  useEffect(() => {
    document.title = `${screenName} — ${schoolName ?? PRODUCT_NAME}`;
  }, [screenName, schoolName]);
  useEffect(() => {
    window.scrollTo(0, 0);
    const main = document.querySelector("main.screen");
    const target = main?.querySelector("h1") ?? main;
    if (target instanceof HTMLElement) {
      target.tabIndex = -1;
      target.focus();
    }
  }, [path, screenName]);
}
