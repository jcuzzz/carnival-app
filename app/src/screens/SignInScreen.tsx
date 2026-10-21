import { loginHref, logoutHref, signOutAndClear } from "../auth/entra";
import { clearDeviceCaches } from "../auth/session";
import { useSession } from "../auth/session";

// A full-page navigation to /auth/login: the browser does the OIDC redirect.
export default function SignInScreen() {
  const session = useSession();

  return (
    <>
      <h1>Sign in</h1>
      <p className="row-sub">
        You'll be redirected to your school's Microsoft sign-in, then brought
        back here. Your name and permissions come from your account — there is
        nothing to pick.
      </p>

      {session ? (
        <div className="card">
          Signed in as <strong>{session.displayName}</strong> —{" "}
          {session.roles.join(", ") || "no roles"}
          <div className="button-row">
            <a
                  href={logoutHref()}
                  onClick={(e) => {
                    e.preventDefault();
                    void signOutAndClear(clearDeviceCaches, (href) => location.assign(href));
                  }}
                >
                  Sign out
                </a>
          </div>
        </div>
      ) : (
        <div className="card">
          <div className="button-row">
            <a className="button primary-link" href={loginHref()}>
              Sign in with Microsoft
            </a>
          </div>
        </div>
      )}
    </>
  );
}
