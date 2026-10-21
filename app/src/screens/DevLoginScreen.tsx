import { useState, type FormEvent } from "react";
import type { PlatformRole } from "../api/types";
import { signIn, signOut, useSession, type Session } from "../auth/session";
import { resumeAfterSignIn } from "../api/syncSetup";
import { navigate } from "../router";

const ALL_ROLES: PlatformRole[] = [
  "CarnivalAdministrator",
  "Recorder",
  "Viewer",
  "SystemAdministrator",
];

const PRESETS: Session[] = [
  // "Alex Admin" is load-bearing: the Playwright helpers key on this display
  // name. Rename the persona and every e2e journey fails at sign-in.
  {
    subject: "dev-admin",
    displayName: "Alex Admin",
    roles: ["SystemAdministrator", "CarnivalAdministrator"],
  },
  { subject: "dev-official-1", displayName: "Olivia Official", roles: ["Recorder"] },
  { subject: "dev-official-2", displayName: "Omar Official", roles: ["Recorder"] },
  {
    subject: "dev-carnival-admin",
    displayName: "Cass Carnival",
    roles: ["CarnivalAdministrator"],
  },
  // Deliberately without CarnivalAdministrator: the only persona that tests SystemAdministrator
  // alone holding every carnival policy.
  { subject: "dev-sysadmin", displayName: "Sam Sysadmin", roles: ["SystemAdministrator"] },
  { subject: "dev-viewer", displayName: "Riley Readonly", roles: ["Viewer"] },
];

export default function DevLoginScreen() {
  const session = useSession();
  const [subject, setSubject] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [roles, setRoles] = useState<PlatformRole[]>(["Recorder"]);
  const [error, setError] = useState<string | null>(null);

  function complete(next: Session) {
    signIn(next);
    // Pause-never-drop: a 401-paused queue resumes under the new session.
    void resumeAfterSignIn();
    navigate("/carnivals");
  }

  function submitCustom(e: FormEvent) {
    e.preventDefault();
    if (subject.trim() === "" || displayName.trim() === "") {
      setError("Subject and display name are required.");
      return;
    }
    complete({ subject: subject.trim(), displayName: displayName.trim(), roles });
  }

  function toggleRole(role: PlatformRole) {
    setRoles((current) =>
      current.includes(role) ? current.filter((r) => r !== role) : [...current, role],
    );
  }

  return (
    <>
      <h1>Sign in (development)</h1>
      <p className="row-sub">
        Dev-auth only: the picked official is sent as X-Dev-Subject / X-Dev-Roles and honoured by
        the API only when its dev authentication is enabled. Real sign-in is Entra ID (ADR-0001).
      </p>

      {session && (
        <div className="card">
          Signed in as <strong>{session.displayName}</strong> ({session.subject}) —{" "}
          {session.roles.join(", ") || "no roles"}
          <div className="button-row">
            <button onClick={() => signOut()}>Sign out</button>
          </div>
        </div>
      )}

      <h2>Pick an official</h2>
      <ul className="row-list card">
        {PRESETS.map((preset) => (
          <li key={preset.subject}>
            <button className="row-link" onClick={() => complete(preset)}>
              <span className="row-main">
                <span className="row-title">{preset.displayName}</span>
                <span className="row-sub">{preset.roles.join(", ")}</span>
              </span>
            </button>
          </li>
        ))}
      </ul>

      <h2>Or custom</h2>
      <form className="card" onSubmit={submitCustom}>
        {error && (
          <div className="alert error" role="alert">
            {error}
          </div>
        )}
        <div className="form-grid">
          <label className="field">
            <span>Subject id</span>
            <input value={subject} onChange={(e) => setSubject(e.target.value)} />
          </label>
          <label className="field">
            <span>Display name</span>
            <input value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
          </label>
        </div>
        <fieldset>
          <legend className="row-sub">Roles</legend>
          <div className="form-grid">
            {ALL_ROLES.map((role) => (
              <label key={role} className="check-row">
                <input
                  type="checkbox"
                  checked={roles.includes(role)}
                  onChange={() => toggleRole(role)}
                />
                {role}
              </label>
            ))}
          </div>
        </fieldset>
        <div className="button-row">
          <button className="primary" type="submit">
            Sign in
          </button>
        </div>
      </form>
    </>
  );
}
