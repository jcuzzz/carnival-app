import { hasRole, useSession } from "../auth/session";
import { SignInOffer, useAccessGate } from "../features/ui/SignInOffer";
import RolesSection from "../features/roles/RolesSection";
import { href } from "../router";
import { useConnectivity } from "../shell/useOnline";

export default function SchoolRolesScreen() {
  const session = useSession();
  const isAdmin = hasRole(session, "SystemAdministrator");
  const offline = useConnectivity() !== "connected";
  const gate = useAccessGate(isAdmin);

  if (gate === "loading") return <p>Loading…</p>;
  if (gate === "signedOut") {
    return (
      <SignInOffer title="Roles">
        <p>Sign in to see who holds which role.</p>
      </SignInOffer>
    );
  }
  if (gate === "refused") {
    return (
      <>
        <h1>Roles</h1>
        <div className="alert error" role="alert">
          Administering roles needs the system administrator role.
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
      <h1>Roles</h1>
      <RolesSection offline={offline} />
    </>
  );
}
