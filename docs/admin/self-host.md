# Self-hosting Carnival App

One Linux VM (or mini PC) runs one school's stack with Docker Compose: `api` + `db` (PostgreSQL 17) + `backup` + `proxy` (Caddy, serving the PWA and the API on 443). Written for a tech-capable teacher or a school's IT contact.

## 1. The machine

| Setting | Value |
|---|---|
| OS | Debian 12 or Ubuntu Server 24.04 LTS |
| CPU / RAM | 2 vCPU / 4 GB (8 GB comfortable) |
| Disk | 40 GB SSD-backed (database and backups live here) |
| Network | Reachable from the devices that will record results |
| Virtualisation | A full VM, not an LXC container (Docker inside LXC fights AppArmor/nesting) |

**Give it a DNS name, not just an IP.** The PWA's install identity, the identity provider's redirect URI and the TLS certificate all key off the hostname. One IP is enough: devices only ever reach Caddy on 80/443; Postgres and the backup job stay on an internal Docker network.

## 2. Install Docker

```bash
sudo apt update && sudo apt install -y git curl
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER   # log out and back in
docker --version && docker compose version
```

## 3. Configure

```bash
git clone https://github.com/<owner>/carnival-app.git ~/work/carnival-app
cd ~/work/carnival-app
cp .env.example .env && chmod 600 .env
nano .env
```

The settings that matter on day one (every variable is documented in `.env.example`):

| Variable | Set to |
|---|---|
| `COMPOSE_PROJECT_NAME` | a short instance name, e.g. `carnival-app-prod` |
| `SCHOOL_NAME`, `SCHOOL_BRAND_COLOUR` | first-start defaults; edited in the app afterwards (School → School settings) |
| `SCHOOL_TIMEZONE` | the school's IANA timezone, e.g. `Australia/Melbourne`; read from configuration whenever it is needed (not editable in the app) |
| `SITE_ADDRESS` | the machine's DNS name |
| `TLS_MODE` | the certificate source (see [TLS](#tls)); `internal` by default |
| `POSTGRES_PASSWORD`, `APP_DB_PASSWORD` | two different long random passwords |
| `APPLY_MIGRATIONS` | `true` for the first start (see §6) |
| `BACKUP_AGE_RECIPIENT` | the public key from §8 |
| identity variables | see §4 |
| `DEV_AUTH`, `VITE_DEMO_MODE` | `false` on any real instance |

Images are pulled, not built (ADR-0049). If the packages are private, log in once with a read-only token: `printf %s '<token>' | docker login ghcr.io -u <user> --password-stdin`.

### TLS

The proxy's certificate source is `TLS_MODE` in `.env` (default `internal`); compose passes it to Caddy as the Caddyfile's `tls` argument.

- **Internal network only (the default):** `TLS_MODE=internal`, Caddy's own CA. Install its root certificate on every recording device.
- **A certificate from your school's IT:** mount the files into the proxy and set `TLS_MODE="/etc/caddy/certs/cert.pem /etc/caddy/certs/key.pem"`. `docker-compose.yml` carries the mount commented out (`./certs:/etc/caddy/certs:ro`). Add it in `docker-compose.override.yml` under `services: proxy: volumes:` rather than uncommenting it: the deploy script refuses a tree with local edits. The files can sit in the clone's `certs/` folder, which git ignores, or outside the clone (for example `/etc/carnival-app/certs`). The key must be readable by the proxy's UID 10001.
- **Reachable from the internet:** set `TLS_MODE` to an admin email address and Caddy obtains a Let's Encrypt certificate for `SITE_ADDRESS` automatically.
- HSTS is not sent in any mode yet.
- Caddy matches on the host name: requests to the raw IP get no site, and `curl https://localhost` fails the handshake. Test with `curl -sk --resolve <name>:443:127.0.0.1 https://<name>/health`.

## 4. Sign-in

Today the API supports **Microsoft Entra ID** or **ADFS** (ADR-0007). Built-in accounts and generic OIDC (Google Workspace, Okta, Keycloak…) are planned; see `docs/HANDOFF.md`.

**Entra:**
1. App registration in your tenant: single-tenant, Web platform, redirect URI `https://<name>/signin-oidc`, front-channel logout `https://<name>/signout-callback-oidc`, a client secret, ID tokens enabled.
2. **Token configuration → groups claim:** return **Security groups**, with the **ID** token emitting **Group ID**. A wrong setting here gives users zero roles. After any change, users must sign out and back in; the session cookie holds the claims from sign-in.
3. `.env`: `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID`, `ENTRA_CLIENT_SECRET` (the secret's *Value*, not its ID). Diarise the secret's expiry. Leave `ENTRA_INSTANCE` empty unless the tenant is in a sovereign cloud.
4. `BOOTSTRAP_ADMIN_UPN`: the first System administrator, or several, comma-separated. Further roles can be granted in the app (School → Roles, ADR-0048) or by group (below).
5. Group → role mapping (optional): copy `docker-compose.override.example.yml` to `docker-compose.override.yml` and map each security-group **object ID** to a role. Object IDs are case-sensitive; a mis-cased ID silently grants nothing.

**ADFS** (2019 or later) has no `.env` variables; wire it in `docker-compose.override.yml` and leave the `ENTRA_*` variables empty. Register an application group with a server application: redirect URI `https://<name>/signin-oidc`, post-logout `https://<name>/signout-callback-oidc`, and a client secret. ADFS emits whatever its claim rules say, usually group *names*, so name the claim and map names (ADR-0007).

`docker-compose.override.example.yml` carries the ADFS block commented out. Copy the file to `docker-compose.override.yml`, delete the Entra group mappings, uncomment the `Identity__Provider: Adfs` and `Adfs__*` lines, and fill in:

- `Adfs__Authority` (and `Adfs__MetadataAddress` if discovery is not at the default path) and `Adfs__ClientId`;
- `ADFS_CLIENT_SECRET` in `.env` (the block reads it as `${ADFS_CLIENT_SECRET}`);
- `Identity__GroupsClaimType`: the claim type your rule emits;
- one `Identity__GroupToRoleMappings__<group-name>` line per group.

A group name containing a space cannot be an environment variable name; put those mappings in a mounted `appsettings.School.json`. Every `Adfs:*` key is listed in the [configuration reference](configuration.md#adfs-or-another-oidc-provider-adfs).

### Roles

| Role | Can |
|---|---|
| `SystemAdministrator` | everything, including roster import (the only role that sees staged roster rows) |
| `CarnivalAdministrator` | run carnivals: lifecycle and finalisation, events, schedule, settings, house-point adjustments, category overrides, records, reports |
| `Recorder` | marshal entries and relays, capture and correct results, on any event |
| `Viewer` | read only |

Everything is enforced by the server; when the UI and an HTTP status disagree, the status is right.

## 5. First start

The API connects as `APP_DB_USER`, a role that `deploy/db/init-roles.sql` creates, so a bare `docker compose up -d` cannot connect. Start the database, then run the deploy script (§6), which creates the role, starts the stack and re-grants after the migrations:

```bash
docker compose up -d db
bash scripts/deploy/vm-deploy.sh                  # pre-flight pass: stops at the gate
PREFLIGHT_OK=1 bash scripts/deploy/vm-deploy.sh
curl -sk --resolve <name>:443:127.0.0.1 https://<name>/health   # Healthy
```

Then, in the browser: sign in as the bootstrap admin → School settings (houses, colours) → import the roster (a CSV; see `config/`) → create a carnival from a template. Set `APPLY_MIGRATIONS=false` again afterwards (§6).

## 6. Upgrades

`scripts/deploy/vm-deploy.sh` is the deploy: run it once without `PREFLIGHT_OK` (it dumps the database, prints counts and pending migrations, and stops), then with `PREFLIGHT_OK=1`. It refuses a dirty tree, pins image **digests** in `.env`, re-grants database roles before and after start, smoke-tests, and keeps the previous pins for rollback (`~/pins-<date>-<sha>.env`). See `scripts/deploy/README.md`.

**Image pins.** For the commit being deployed, the script resolves each image's `sha-<short>` tag in GHCR (`carnival-app-api`, `carnival-app-backup`, and `carnival-app-web`, or `carnival-app-web-demo` when `VITE_DEMO_MODE=true`) to its digest, and rewrites `API_IMAGE`, `WEB_IMAGE` and `BACKUP_IMAGE` at the end of `.env` as `repository@sha256:…` under a comment naming the commit. The comment reads `PENDING` until every smoke check has passed, including a check that each container runs exactly the pinned image. CI publishes images only when its Images workflow is run for a commit; if any tag is missing, the script stops and nothing is deployed.

**`LOCAL_BUILD=1`** is the fallback when CI has published no images: the script builds the three images on the host with `docker-compose.build.yml` (after the override file, if there is one), tags them `carnival-app-local/{api,web,backup}:<short-sha>` and writes those tags to `.env`. They are not the CI-tested images and are not pinned by digest.

**Image retention.** No retention policy is configured: nothing prunes old images on the host or in the registry. A digest pin can be pulled again for a rollback; a local build cannot, so roll back to one only while its images are still on the host (or re-run with `LOCAL_BUILD=1` at the old commit). The deploy's own pre-deploy dumps (`~/pre-*.dump`, unencrypted) are pruned to the newest five.

**Migrations do not apply themselves** (`APPLY_MIGRATIONS` defaults to false). Before an upgrade, check with git whether the span carries one:

```bash
git diff --name-only <deployed-commit> <new-commit> -- api/src/CarnivalApp.Api/Migrations/
```

If it does, take a dump, start once with `APPLY_MIGRATIONS=true`, check `/health`, and set it back.

The `OneAgePerDivision` migration refuses to run while any division, event or school record holds a merged age key (a hyphenated code such as `15-16`); its error lists them. Remove those divisions and the records they set, then start again.

**Rollback** is the previous pins: check out the old commit, restore the pin lines from the saved file into `.env`, `docker compose up -d db api proxy backup`.

After an upgrade, an installed PWA picks up the new build once all its windows are closed and reopened.

## 7. What the stack does for you

- **Two database roles** (ADR-0047): `POSTGRES_USER` owns the schema and runs migrations only; requests run as `APP_DB_USER` with DML only, and `UPDATE`/`DELETE` revoked on the audit table. `deploy/db/init-roles.sql` creates and re-grants the role; the deploy script runs it.
- **Timeouts on the application role**, set by the same script: `statement_timeout` 60 s, `idle_in_transaction_session_timeout` 60 s, `lock_timeout` 10 s. A runaway query or an abandoned transaction cannot hold locks for long. The owner role has none, so migrations are not cut short.
- **Two networks:** `edge` (proxy, api) and an internal `data` network (api, db, backup). The proxy has no route to Postgres; the database has no route off the host.
- **Unprivileged containers:** `api` runs as UID 1654, `proxy` as UID 10001, both with all capabilities dropped. Caddy listens on 8080/8443 inside the container, published as 80/443. If you deploy by hand, chown the `dp-keys` (1654) and `caddy-data`/`caddy-config` (10001) volumes first, as the script does; otherwise every official is signed out.
- **A daily retention sweep** in the API, at start-up and then every 24 hours: idempotency keys (which hold response bodies with student names) are deleted 30 days after creation, and a roster upload that was never promoted loses its staged rows 7 days after upload ([ADR-0062](../adr/0062-retention-windows-for-operational-data.md)). It runs only against PostgreSQL on a school instance, never touches results, records or the audit trail, and logs one line per run (`Retention sweep: …`).
- **Log and memory caps** per service, sized for a 4 GB machine. Exit code 137 means a container hit its memory limit.
- **State that cannot be recreated:** the `db-data` and `db-backups` volumes. `dp-keys` can be rebuilt, but losing it signs everyone out mid-carnival.

## 8. Backups

The `backup` service writes an encrypted `pg_dump` (`age`) on an interval (`BACKUP_INTERVAL_SECONDS`) and prunes by `BACKUP_RETENTION_DAYS`, always keeping the newest three.

**Key custody is the part that matters.** Generate the key pair on a trusted machine, not the server:

```bash
age-keygen -o school-backup.key     # prints "Public key: age1..."
```

The public key goes in `.env` as `BACKUP_AGE_RECIPIENT`. The private key stays off the server, held by the school with at least two custodians. Lose it and every backup is unreadable.

**Off-host copy:** set `BACKUP_PUSH_TARGET` (rsync over ssh) and cron `scripts/deploy/push-backups.sh` nightly. It pushes encrypted dumps only and fails rather than pushing a partial copy.

**Restore drill, once a term and before every carnival season:**

```bash
scp school-backup.key <server>:~/
ssh <server> 'bash ~/work/carnival-app/scripts/deploy/restore-drill.sh ~/school-backup.key'
ssh <server> 'shred -u ~/school-backup.key'
```

It restores the newest dump into a scratch database, compares row counts with the live tables, and fails if the dump is stale or a key table is empty. Record each drill in the instance record below.

## 9. Encryption at rest

Backups are encrypted; the live database volume is not, unless the host encrypts the disk. On a hypervisor, put the VM's disk on encrypted storage (ZFS native encryption or LUKS under the storage pool). The trade-off: after a host reboot someone must unlock the storage before the VM can start.

## 10. Instance record (fill in, keep with your own documentation)

| Item | Value |
|---|---|
| Host name / IP | |
| OS and version | |
| Who patches the OS and Docker | |
| TLS certificate source and expiry | |
| Identity provider, client secret expiry | |
| Backup key custodians | |
| Off-host backup target | |
| Restore drills (date, result) | |
