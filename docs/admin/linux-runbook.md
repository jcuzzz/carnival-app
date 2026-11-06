# Linux runbook: from a fresh clone to a running carnival

Two ways to run Carnival App on a Linux machine, both with synthetic data only:

- **A. From source**: the API on SQLite and the app on the Vite dev server. No Docker, no identity provider; you sign in by picking a persona. Use it to try the product or to work on the code.
- **B. The full stack**: Docker Compose with PostgreSQL, backups and Caddy, as a school runs it. Sign-in uses the built-in local accounts; Microsoft Entra ID or ADFS is optional.

Tested target: Debian 13, Debian 12 or Ubuntu Server 24.04 LTS on a VM with 2 vCPU, 4 GB RAM and 40 GB of disk. Run every command as a normal user with `sudo`, not as root.

## 0. Get the code

```bash
sudo apt update && sudo apt install -y git curl ca-certificates python3
git clone https://github.com/jcuzzz/carnival-app.git ~/work/carnival-app
cd ~/work/carnival-app
```

While the repository is private, `git clone` needs a credential. For a VM, a read-only deploy key is simplest: `ssh-keygen -t ed25519 -f ~/.ssh/carnival-app -N ""`, add the `.pub` file under the repository's **Settings → Deploy keys**, then clone with `GIT_SSH_COMMAND="ssh -i ~/.ssh/carnival-app" git clone git@github.com:jcuzzz/carnival-app.git ~/work/carnival-app` and run `git config core.sshCommand "ssh -i ~/.ssh/carnival-app"` inside the clone. Part B's deploy script runs `git fetch`, so the credential must keep working.

## A. From source

### A1. Install .NET 10 and Node 22

.NET needs the ICU library, which minimal and cloud images leave out: `sudo apt install -y libicu76` on Debian 13, `libicu74` on Ubuntu 24.04, `libicu72` on Debian 12. Without it every `dotnet` command aborts with "Couldn't find a valid ICU package".

```bash
curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
echo 'export DOTNET_ROOT=$HOME/.dotnet; export PATH=$HOME/.dotnet:$HOME/.dotnet/tools:$PATH' >> ~/.bashrc
curl -fsSL https://deb.nodesource.com/setup_22.x | sudo -E bash -
sudo apt install -y nodejs
exec bash
dotnet --version   # 10.0.x
node --version     # v22.x
```

### A2. Start the API

```bash
cd ~/work/carnival-app/api/src/CarnivalApp.Api
dotnet run --launch-profile http
```

The first start creates a SQLite database at `~/.local/share/CarnivalApp/dev.db` and seeds it: a school with four houses, 40 synthetic students imported through the real pipeline, and an open swimming carnival. Wait for `Now listening on: http://localhost:5080`. Leave it running.

### A3. Start the app

In a second terminal:

```bash
cd ~/work/carnival-app/app
npm ci
npm run dev
```

Vite serves the app on `http://localhost:5173` and forwards `/api` to the API.

### A4. Open it

From your own computer, tunnel the port so the browser sees `localhost`. Browsers only run the service worker and offline outbox on `localhost` or HTTPS.

```bash
ssh -L 5173:localhost:5173 <user>@<vm>
```

Browse to `http://localhost:5173`, pick **Alex Admin** (system and carnival administrator), and you are in. The other personas show what a Recorder or a Viewer sees. Sign out to switch.

Never expose this mode beyond your own machine: anyone who can reach the API can claim any role.

### A5. Run the tests

```bash
cd ~/work/carnival-app/api && dotnet test
cd ~/work/carnival-app/app && npx tsc -b && npm run lint && npm test && npm run build
```

The API suite takes about 15 minutes on 2 vCPU, and its test host peaks near 2.5 GB, so give the VM 4 GB of RAM and some swap if the API and app stay running alongside it. Six API tests use Testcontainers and skip without Docker. With Docker installed (B1) and your user in the `docker` group, they run against a real PostgreSQL, and nothing should be skipped.

### A6. Reset

Stop the API, then `rm -f ~/.local/share/CarnivalApp/dev.db*`. The next start recreates and reseeds it. Reset after pulling a change to the data model: the SQLite schema is never migrated.

## B. The full stack

This is the shape in [Self-hosting](self-host.md), which explains each setting; this section is the shortest path through it on a test machine.

### B1. Install Docker

```bash
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker $USER
exec su - $USER          # or log out and back in
docker --version && docker compose version
```

Use a full VM, not an LXC container.

### B2. Give the machine a name

Pick a host name such as `carnival.home.arpa` and make every device that will open the app resolve it to the VM: a record in your router or local DNS, or a line in each device's hosts file. The TLS certificate, the first-administrator link and any provider's sign-in redirects depend on the name. The VM's IP address also works as `SITE_ADDRESS`, with no DNS or hosts entry, as long as it never changes.

### B3. Register the app with Entra (optional)

Skip this to sign in with local accounts only. To try Entra as well, create an app registration in Microsoft Entra ID:

1. Single-tenant, **Web** platform, redirect URI `https://<name>/signin-oidc`, front-channel logout URL `https://<name>/signout-callback-oidc`, ID tokens enabled.
2. **Certificates & secrets**: create a client secret and copy its **Value**.
3. Note the tenant ID and the application (client) ID.

Use a test registration, not the one a school instance uses. ADFS works too; see [Self-hosting §4](self-host.md#4-sign-in).

### B4. Configure

```bash
cd ~/work/carnival-app
cp .env.example .env && chmod 600 .env
nano .env
```

| Variable | Test value |
|---|---|
| `COMPOSE_PROJECT_NAME` | `carnival-app-test` |
| `SITE_ADDRESS` | the name (or IP address) from B2 |
| `TLS_MODE` | `internal` |
| `POSTGRES_PASSWORD`, `APP_DB_PASSWORD` | two different values from `openssl rand -base64 24` |
| `APPLY_MIGRATIONS` | `true` |
| `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID`, `ENTRA_CLIENT_SECRET` | from B3, or empty for local accounts only |
| `BOOTSTRAP_ADMIN_UPN` | empty; with Entra, optionally your Entra sign-in address |
| `BACKUP_ENCRYPTION` | `off` (synthetic data only; for real data set `BACKUP_AGE_RECIPIENT` instead, see [Self-hosting §8](self-host.md#8-backups)) |

Leave `DEV_AUTH=false` and `VITE_DEMO_MODE=false`.

### B5. Build and start

No release images are published yet, so the deploy script builds them on the machine (`LOCAL_BUILD=1`; the first build takes several minutes):

```bash
docker compose up -d db
bash scripts/deploy/vm-deploy.sh                              # pre-flight: dumps the database and stops at the gate
PREFLIGHT_OK=1 LOCAL_BUILD=1 bash scripts/deploy/vm-deploy.sh
```

The database is new, so the script reports a fresh database with no migration history, skips the data gates, and the API applies every migration as it starts (`APPLY_MIGRATIONS=true` from B4; without it the script stops before changing anything). The script ends with its smoke checks. Check it yourself:

```bash
curl -sk --resolve <name>:443:127.0.0.1 https://<name>/health      # Healthy
docker compose logs api | grep "Retention sweep"                  # one line per run, the first at start-up
```

Last, after the summary, the script prints the first-administrator link (`>>> First administrator: open https://<name>/#/setup/<token>  (valid 24 hours)`). It is single use and is printed only this once; do not paste it into notes. If `SITE_ADDRESS` is still `localhost` it prints no link: set the name and run the deploy again.

Then set `APPLY_MIGRATIONS=false` in `.env`.

### B6. Trust the certificate

`TLS_MODE=internal` uses Caddy's own certificate authority. Copy its root certificate off the machine and install it on each device that opens the app, or browsers warn and the app cannot install or work offline:

```bash
docker compose cp proxy:/data/caddy/pki/authorities/local/root.crt ./carnival-root.crt
```

### B7. First carnival

Open the first-administrator link on a device that trusts the certificate (B6). Choose a username such as `backup-admin`, a display name and a password of at least 15 characters, and set up an authenticator app or skip it. You are signed in as a System administrator. Then:

1. **School → School settings**: name and colours. The houses default to BANKSIA, WATTLE, MOUNTAIN ASH and GREVILLEA (`SCHOOL_HOUSE_1`…`4`), which the synthetic rosters use.
2. **School → Roster import**: upload `data/synthetic/ST_SYNTH.csv` (120 students, a few with deliberate errors for the review to flag). Review, approve, promote.
3. **Carnivals → New carnival**, then **Manage** to apply the swimming or athletics event template, open the carnival and record some results.

### B8. Update

```bash
cd ~/work/carnival-app && git pull --ff-only
bash scripts/deploy/vm-deploy.sh
PREFLIGHT_OK=1 LOCAL_BUILD=1 bash scripts/deploy/vm-deploy.sh
```

The script deploys whatever is checked out. A branch with an upstream is fetched and fast-forwarded; a branch with no upstream, a tag or a detached commit is deployed as it is, with no pull (skip the `git pull` above too). Running it again with nothing new passes as well.

Once releases are published, check out a release tag instead ([Self-hosting §6](self-host.md#6-upgrades)) and drop `LOCAL_BUILD=1`: the script pulls the CI images by digest.

### B9. Recover access

When nobody can sign in, run on the host from the clone's directory:

```bash
docker compose exec -T api dotnet CarnivalApp.Api.dll accounts setup-link <username>
```

It prints `Setup link for <username>: open https://<name>/#/setup/<token>  (valid 24 hours)`. An existing account loses its password and authenticator, is enabled and has its lockout cleared; a new name becomes a System administrator account. `accounts first-admin` prints a first-administrator link while no local account exists. Exit codes: `0` done, `2` nothing to do (the reason on stderr), `1` failure. On a database with pending migrations both refuse with `database not migrated yet: deploy the API first`. See [Self-hosting §4](self-host.md#recovering-access-from-the-host).

### B10. After a restore

A restore brings local accounts back as they were at the dump. Accounts added, passwords changed and authenticators set up since then are lost: the person signs in with their old password, or an administrator adds or resets them again. Check the audit trail and the school's own records for changes made since the dump, and redo these:

- **Disables and resets**: disable or reset those accounts again under School → People.
- **Used recovery codes**: a code used since the dump works again. Reset any account with a `local-account.recovery-code-redeem` audit row after the dump time, so it enrols again with fresh codes.
- **Setup links**: a link issued in the 24 hours before the dump and unused at the dump is live again, even if it was used or replaced since. Reset every account whose link was issued in that window; a reset issues a new link and revokes the old one.

A local session signed out after the dump is valid again, for what remains of its 12 hours, on any device that still holds its cookie. Changing the account's password or resetting it ends every session of the account.

### B11. Remove

```bash
docker compose down -v      # -v deletes the database and backup volumes
```

## When something fails

| Symptom | Likely cause |
|---|---|
| `curl` fails the TLS handshake | Requests must use `SITE_ADDRESS` from B2; with a name set, Caddy answers neither `localhost` nor the IP |
| Sign-in loops or returns an error page | Redirect URI does not match `https://<name>/signin-oidc` exactly, or the secret's ID was pasted instead of its Value |
| Signed in but every screen says you lack access | A local account: no role granted yet under School → Roles. An Entra account: `BOOTSTRAP_ADMIN_UPN` differs from its sign-in address and no group or in-app grant applies |
| No first-administrator link after the deploy | `SITE_ADDRESS` is `localhost`, a smoke check failed, or a local account already exists (the script says which); see B9 |
| "This setup link has expired or has already been used" | Links are single use and last 24 hours; an administrator resets the account, or run the host command (B9) |
| `backup` keeps restarting | `BACKUP_AGE_RECIPIENT` empty and `BACKUP_ENCRYPTION` not `off` |
| The deploy script stops with "vm-deploy.sh changed on origin/…" | The upstream has a newer copy of the script: `git pull --ff-only`, then run it again |
| The deploy script stops with "A fresh database needs APPLY_MIGRATIONS=true" | Set `APPLY_MIGRATIONS=true` in `.env` for the first start, run it again, then set it back (B5) |
| The deploy script stops with "Working tree is dirty" | A file in the clone was edited; configuration belongs in `.env` and `docker-compose.override.yml` only |
| A container exits with code 137 | It hit its memory limit; give the VM more RAM |
