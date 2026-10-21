import { hasRole, useSession } from "../auth/session";
import { SignInOffer, useAccessGate } from "../features/ui/SignInOffer";
import TemplatesSection from "../features/templates/TemplatesSection";
import { href } from "../router";
import { useConnectivity } from "../shell/useOnline";

export default function SchoolTemplatesScreen() {
  const session = useSession();
  const isAdmin = hasRole(session, "SystemAdministrator");
  const offline = useConnectivity() !== "connected";
  const gate = useAccessGate(isAdmin);

  if (gate === "loading") return <p>Loading…</p>;
  if (gate === "signedOut") {
    return (
      <SignInOffer title="Event templates">
        <p>Sign in to preview and apply the school's event templates.</p>
      </SignInOffer>
    );
  }
  if (gate === "refused") {
    return (
      <>
        <h1>Event templates</h1>
        <div className="alert error" role="alert">
          Applying an event template needs the system administrator role.
        </div>
        <p>
          <a href={href("/carnivals")}>← Back to carnivals</a>
        </p>
      </>
    );
  }

  return (
    <>
      <p>
        <a href={href("/school")}>← School</a>
      </p>
      <h1>Event templates</h1>
      <TemplatesSection offline={offline} />
    </>
  );
}
