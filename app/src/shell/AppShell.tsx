import type { ReactNode } from "react";
import {
  BarChart3,
  ClipboardList,
  Flag,
  School,
  Settings2,
  type LucideIcon,
} from "lucide-react";
import { isDemoBuild } from "../api/client";
import {
  entraAuthEnabled,
  loginHref,
  logoutHref,
  serverSessionEnabled,
  signOutAndClear,
} from "../auth/entra";
import { useIdleGuard } from "../auth/idleGuard";
import { clearDeviceCaches } from "../auth/session";
import { hasRole, useSession } from "../auth/session";
import { PRODUCT_NAME, PRODUCT_SHORT_NAME, useBranding } from "../branding/branding";
import { activeTab, tabPath, type CarnivalTab } from "../carnivals/landing";
import { useSelectedCarnival } from "../carnivals/selectedCarnival";
import { href, usePath } from "../router";
import type { OutboxHealth } from "../sync/stuckOutbox";
import IdleOverlay from "./IdleOverlay";
import OutboxIndicator from "./OutboxIndicator";
import OutdoorToggle from "./OutdoorToggle";
import StuckOutboxBanner, { useOutboxHealth } from "./StuckOutboxBanner";
import Toaster from "./Toaster";
import { useConnectivity, type ConnectivityState } from "./useOnline";

const NAV_ICON = 20;

// The official's name stays visible: devices are shared. The Outbox keeps its own header chip so it
// is never behind the Manage role gate.
export default function AppShell({ children }: { children: ReactNode }) {
  const session = useSession();
  const path = usePath();
  const connectivity = useConnectivity();
  const health = useOutboxHealth();
  const entra = entraAuthEnabled();
  const demo = isDemoBuild() && serverSessionEnabled();
  const carnival = useSelectedCarnival();
  const branding = useBranding();
  const canManage = hasRole(session, "CarnivalAdministrator", "SystemAdministrator");
  const idle = useIdleGuard(session !== null);

  const tabs: { tab: CarnivalTab; label: string; Icon: LucideIcon }[] = [
    { tab: "deck", label: "Deck", Icon: ClipboardList },
    { tab: "dashboard", label: "Dashboard", Icon: BarChart3 },
    ...(canManage ? ([{ tab: "manage", label: "Manage", Icon: Settings2 }] as const) : []),
  ];
  const current = carnival ? activeTab(path, carnival.id) : null;
  // `active` marks the section; aria-current="page" only the tab's own address.
  const currentPage = (tab: CarnivalTab) => carnival && path === tabPath(carnival.id, tab);

  // In both nav rows: a selected carnival persists, so its row is often the only one shown.
  // /import counts as School: the hub owns that row. A nav entry, not access control.
  const inSchool = path === "/school" || path.startsWith("/school/") || path === "/import";
  const schoolLink = canManage ? (
    <a
      href={href("/school")}
      className={inSchool ? "active" : ""}
      aria-current={path === "/school" ? "page" : undefined}
    >
      <School size={NAV_ICON} aria-hidden="true" />
      School
    </a>
  ) : null;

  return (
    <>
      {isDemoBuild() && (
        <div className="demo-banner" role="alert">
          PUBLIC DEMO — synthetic data only, your copy is temporary
        </div>
      )}
      {/* The banner rides in the sticky block with the header, so a scrolled
          deck cannot leave the outbox chip visible and the fault behind it. */}
      <div className="shell-top">
        <header className="shell-header">
          {/* Not an <h1>: the one h1 is the screen's, the focus target on navigation. */}
          <div className="shell-identity">
            {branding.monogram && (
              <span className="shell-monogram" aria-hidden="true">
                {branding.monogram}
              </span>
            )}
            <div className="shell-names">
              <span className="shell-title">{branding.schoolName ?? PRODUCT_NAME}</span>
              {branding.schoolName && <span className="shell-product">{PRODUCT_SHORT_NAME}</span>}
            </div>
          </div>
          {carnival && (
            <a
              className="carnival-chip"
              href={href("/carnivals")}
              aria-label={`Carnival: ${carnival.name ?? "selected"} — switch carnival`}
            >
              <span className="carnival-chip-name">{carnival.name ?? "Carnival"}</span>
              {carnival.date && <span className="carnival-chip-meta">{carnival.date}</span>}
              {carnival.status && <span className="carnival-chip-meta">{carnival.status}</span>}
              <span aria-hidden="true">▾</span>
            </a>
          )}
          <OutboxIndicator />
          <div className="shell-actor">
            {session ? (
              <>
                <span aria-label={`Signed in as ${session.displayName}`}>
                  <strong>{session.displayName}</strong>{" "}
                  <span className="shell-roles">({session.roles.join(", ") || "no roles"})</span>
                </span>
                {/* Real auth: a full-page navigation, so the server clears the cookie and
                    IdP session. */}
                {demo ? null : entra ? (
                  <a
                    href={logoutHref()}
                    onClick={(e) => {
                      e.preventDefault();
                      void signOutAndClear(clearDeviceCaches, (href) => location.assign(href));
                    }}
                  >
                    Sign out
                  </a>
                ) : (
                  <a href={href("/login")}>Switch</a>
                )}
              </>
            ) : demo ? null : entra ? (
              <a href={loginHref()}>Sign in</a>
            ) : (
              <a href={href("/login")}>Sign in</a>
            )}
            <OutdoorToggle />
          </div>
        </header>
        <nav className="shell-nav" aria-label="Primary">
          {carnival ? (
            <>
              {tabs.map(({ tab, label, Icon }) => (
                <a
                  key={tab}
                  href={href(tabPath(carnival.id, tab))}
                  className={current === tab ? "active" : ""}
                  aria-current={currentPage(tab) ? "page" : undefined}
                >
                  <Icon size={NAV_ICON} aria-hidden="true" />
                  {label}
                </a>
              ))}
              {schoolLink}
            </>
          ) : (
            <>
              {/* "/" renders the switcher without navigating, so both addresses are Carnivals. */}
              <a
                href={href("/carnivals")}
                className={path === "/carnivals" || path === "/" ? "active" : ""}
                aria-current={path === "/carnivals" || path === "/" ? "page" : undefined}
              >
                <Flag size={NAV_ICON} aria-hidden="true" />
                Carnivals
              </a>
              {schoolLink}
            </>
          )}
        </nav>
        {health.cause && <StuckOutboxBanner key={health.cause} health={health} />}
      </div>
      {connectivity !== "connected" && (
        <div className={`connectivity-strip ${connectivity}`} role="status" aria-live="polite">
          {connectivityMessage(connectivity, health)}
        </div>
      )}
      <main className="screen">{children}</main>
      <Toaster />
      {idle.locked && <IdleOverlay onResume={idle.resume} />}
    </>
  );
}

// No recovery promise under a paused or parked banner: the network returning neither resumes a
// signed-out queue nor decides a refused capture.
function connectivityMessage(connectivity: ConnectivityState, health: OutboxHealth): string {
  const { pending } = health;
  const offline = connectivity === "offline";
  const lead = offline ? "Offline" : "Can't reach the server";
  const held = pending === 1 ? "1 capture" : `${pending} captures`;

  if (health.cause === "paused" || health.cause === "parked") {
    return pending > 0
      ? `${lead} — ${held} saved on this device.`
      : `${lead} — results are saved on this device.`;
  }
  const resumes = offline ? "when you're back" : "when it returns";
  return pending > 0
    ? `${lead} — ${held} ${pending === 1 ? "is" : "are"} waiting,` +
        ` saved on this device and syncing ${resumes}.`
    : `${lead} — results are saved on this device and sync ${resumes}.`;
}
