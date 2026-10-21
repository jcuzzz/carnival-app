# Frontend deployment — single-origin serving via one Caddy

How the React PWA (`app/`) is containerised and served alongside the API on
the per-school compose stack (`docker-compose.yml`), and what the demo-mode
toggles do. Companion docs: `docs/admin/self-host.md` (VM stand-up),
`docs/development/local-dev.md` (local loop — unaffected by any of this).

## Design: one Caddy, one origin, one port

The `proxy` service is no longer the stock `caddy:2` image with a bind-mounted
Caddyfile. It is a custom image (`deploy/Dockerfile.web`), **built in CI and
pulled by digest** (ADR-0049) — the VM builds nothing. It has two stages, from a
build context of the repo root:

1. **Node stage** — `npm ci && npm run build` in a copy of `app/`. The
   production Vite build does **not** load `app/.env.development`, so
   `VITE_API_BASE` is unset and the app calls the API at a **relative**
   `/api/v1/...` base (see `app/src/api/client.ts::apiBase()`, default `""`).
2. **Caddy stage** — the built `dist/` is copied to `/srv` and
   `deploy/Caddyfile` to `/etc/caddy/Caddyfile` (both baked in; changing either
   means a new image, which means a commit to `main` and the CI build — or, for a
   local loop, the build overlay below).

The Caddyfile keeps `{$SITE_ADDRESS}` host matching (runtime env var, no
rebuild needed to change the hostname) and splits one origin by path:

| Path | Destination |
|---|---|
| `/api/*` | `reverse_proxy api:8080` — the whole versioned API surface |
| `/health` | `reverse_proxy api:8080` — ops/compose health probe |
| `/signin-oidc`, `/signout-callback-oidc` | `reverse_proxy api:8080` — Entra OIDC callback paths (`appsettings.School.example.json`) |
| everything else | `file_server` from `/srv` with `try_files {path} /index.html` — static assets served as-is, unknown paths fall back to `index.html` (history-API/SPA fallback, so deep links like `/carnivals/<id>` and installed-PWA navigation work) |

Result: `https://<host>/` is the app and `https://<host>/api/...` is the API —
**same origin, port 443 only**. The VM sits in a managed DMZ where opening
ports needs manual firewall changes; this design adds none. Same-origin also
means no CORS configuration and the API session cookie rides every app request
naturally (`credentials: "include"` in the client).

TLS is unchanged from ADR-0001: public DNS → automatic ACME; otherwise
ICT-supplied certificates or Caddy's internal CA (`SITE_ADDRESS=localhost`
serves the self-signed internal cert).

### Why the frontend never needs an absolute API URL

- **Deployed build:** `VITE_API_BASE` unset → `apiBase()` returns `""` →
  requests go to `/api/v1/...` on the serving origin, which Caddy routes to
  the API container. Nothing to configure per school.
- **Local dev loop:** `VITE_API_BASE` is unset there too (2026-07-31), so the
  dev server sees the same relative `/api/v1/...` and Vite's `/api` proxy
  forwards it to `http://localhost:5080`. Setting it would make the browser
  call the API cross-origin, and — per the paragraph above — the API serves no
  CORS headers, so every read fails with "Failed to fetch". See the note in
  `app/.env.development`.

## Demo-mode toggles (default OFF — both of them)

Two independent switches, meant to be flipped **together, only for
synthetic-data demos**:

| Toggle | Where | Kind | Effect |
|---|---|---|---|
| `DEV_AUTH` | `.env` → `Identity__DevAuth__Enabled` on the `api` service | **Runtime** (container restart applies it) | API honours `X-Dev-Subject` / `X-Dev-Roles` headers as authentication |
| `VITE_DEMO_MODE` | `.env` — on the VM it selects **which image** (`carnival-app-web-demo` vs `carnival-app-web`); locally it is the build overlay's build arg | **Build-time** (Vite inlines it) | PWA shows a persistent, high-visibility **"DEMO MODE — no real authentication, synthetic data only"** banner on every screen, and the built app is allowed to send the dev identity headers (otherwise a production build never sends them) |

CI publishes **two web images** rather than one with a runtime flag
(ADR-0049 §2): `ghcr.io/jcuzzz/carnival-app-web` (`VITE_DEMO_MODE=false`) and
`ghcr.io/jcuzzz/carnival-app-web-demo` (`true`). A runtime flag would be a flag that
can be turned *off* on a live instance, which is the opposite of what the banner
is for. `scripts/deploy/vm-deploy.sh` reads `VITE_DEMO_MODE` from `.env` and pins
the matching image; it says which one in its output.

Mechanism detail (`app/src/api/client.ts`): `isDemoBuild()` returns
`import.meta.env.VITE_DEMO_MODE === "true"`. The banner (`app/src/shell/AppShell.tsx`)
and `devAuthHeaders()` are gated on it (dev server builds, `import.meta.env.DEV`,
behave as before). A build-time flag was chosen over runtime detection
(e.g. exposing the dev-auth state via `/api/v1/version`): it cannot be wrong
while offline or mid-request, adds no API surface that advertises the
authentication mode, and the pairing rule below makes misconfiguration
self-evidencing.

Pairing rule of thumb:

- `DEV_AUTH=true` + `VITE_DEMO_MODE=true` → working demo, loudly bannered.
- `DEV_AUTH=true` + normal build → **misconfiguration**: the backend is wide
  open but the app can't even use it (no headers sent). Fix the env.
- `DEV_AUTH=false` + `VITE_DEMO_MODE=true` → banner shows, every call 401s —
  harmless but rebuild without the flag.
- Both false (default) → the real path: Entra ID OIDC per ADR-0001.

## SECURITY CAVEAT — read before touching DEV_AUTH

Dev header-auth on a networked deployment means **anyone who can reach the
site can assume any role** — including `SystemAdministrator` — by sending two
plain HTTP headers. There is no credential of any kind. It exists solely so a
synthetic-data demo can be clicked through before the Entra app registration
lands.

- `DEV_AUTH` **MUST be `false`** before any real student data touches the
  instance and in any production use. Same for `VITE_DEMO_MODE`.
- Only ever enable it on the synthetic test VM, with only `data/synthetic/`
  content loaded (repo hard rule: never real student data).
- The defaults are safe: both compose interpolations default to `false`
  (`${DEV_AUTH:-false}`, `${VITE_DEMO_MODE:-false}`), a missing `.env` entry
  disables them, and a normal frontend build shows no banner and sends no
  dev headers.

## Deploying / updating on the VM

**The VM does not build the frontend.** A green push to `main` makes CI build
`carnival-app-web` and `carnival-app-web-demo` and push them to GHCR; the deploy pins the
digest for that commit and pulls (runbook §5.0):

```bash
ssh deploy 'PREFLIGHT_OK=1 bash ~/work/carnival-app/scripts/deploy/vm-deploy.sh'
```

Which is why "I deployed and the UI did not change" has a different answer now.
It used to mean a `build proxy` had failed on the VM and the old image kept
serving; that failure mode is gone — a failed build publishes no image, and the
deploy refuses a commit with no image. Check the pin instead:

```bash
docker inspect -f '{{.Config.Image}}' "$(docker compose ps -q proxy)"   # must be the …@sha256:… in .env
```

To build the frontend image locally (no GHCR credential needed):

```bash
docker compose -f docker-compose.yml -f docker-compose.build.yml build proxy
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d proxy
```

The proxy build downloads `node:22-alpine` once, runs `npm ci` (cached until the
lockfile changes) and `vite build`, then assembles the Caddy image — a few
minutes cold, seconds warm. On a school box that work now happens on GitHub's
runners instead, which is also why the DMZ host's flaky egress to Docker Hub is
no longer a deploy-time failure mode.

Smoke test:

```bash
curl -k https://<host>/health          # API health via Caddy
curl -k https://<host>/api/v1/version  # 401 expected — endpoint requires auth (2026-07-31); a JSON 401 still proves API+Caddy routing
curl -k https://<host>/                # index.html
curl -k https://<host>/carnivals       # deep link → index.html (SPA fallback)
```

Note for existing stacks: the Caddyfile is baked into the image (the
`./deploy/Caddyfile` bind mount is gone), so a Caddyfile change ships like any
other code change — commit, let CI publish, deploy.
