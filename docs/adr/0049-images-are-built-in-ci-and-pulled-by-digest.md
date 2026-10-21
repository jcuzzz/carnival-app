# ADR-0049: Images are built in CI and pulled by digest

- **Status:** Accepted (2026-09). Amends [ADR-0001](0001-technology-stack.md) (deployment) and [ADR-0047](0047-the-database-stays-on-the-host.md) (deploy ordering).

## Context

Deploys built the three images on the school's host, from a `git pull` of the full source. That had three costs. The host's egress to public registries can be unreliable, so a deploy could die on a base-image pull. Build tooling and the whole source tree lived on a server that may sit in a managed network zone, and a failed frontend build could leave the old frontend serving with nothing saying so. And "the code deployed is the code tested" rested on CI and the host both starting from the same commit, with nothing proving the bytes agreed.

One constraint is fixed: **deployment is pull-based only. CI must never hold credentials into school infrastructure.**

## Decision

1. **Images live in GitHub Container Registry (GHCR)**, under the repository owner: `carnival-app-api`, `-web`, `-web-demo` and `-backup`. If the packages are private, the host pulls with a **read-only** token held in its Docker credential store. That is the school holding a credential into GitHub, which the constraint allows; the reverse stays forbidden. There is no push-based deploy.
2. **CI builds images on demand, after the tests pass.** `.github/workflows/images.yml` is started by hand for a ref (`gh workflow run images.yml -f ref=main`) or by pushing a version tag `vX.Y.Z`; it re-runs the API and app tests against that ref and only then builds and pushes all four images, so an image is never published from untested code. Images are not built on every push, to keep CI within a free-tier minute budget.
   - **amd64 only:** every target is an x86-64 guest.
   - **Two web images, not a runtime flag.** `VITE_DEMO_MODE` is inlined by Vite at build time, so the demo build (and its banner) cannot be switched off on a live instance. `-web-demo` is for demo deployments only ([demo mode](../architecture/demo-mode.md)).
   - Tags are `sha-<short>` and, when the ref is `main`, `main`. A version tag adds `X.Y.Z` and `X.Y` (a pre-release such as `X.Y.Z-rc.1` gets no `X.Y`), and a release job creates the GitHub release with generated notes and the image names. Builds publish without attestations (`provenance: false`), so the digest the host reports is the digest CI pushed.
3. **The registry is the gate; the manifest is evidence.** The workflow writes `release.json` (commit, build time, the four digests) to the run summary and as a workflow artifact. It is not committed back to `main`: a workflow commit would itself be a commit without images, and the deploy would refuse it. The deploy instead resolves `sha-<short>` for the commit it is deploying, straight from the registry that the build job alone writes.
4. **Compose names images, and keeps builds in an overlay.** `docker-compose.yml` sets `image:` from `API_IMAGE`, `WEB_IMAGE` and `BACKUP_IMAGE` (defaulting to the `main` tag), so a plain `docker compose up -d` pulls. The `build:` blocks live in `docker-compose.build.yml` for local development.
5. **The deploy script builds nothing and runs digests, never tags.** `scripts/deploy/vm-deploy.sh` resolves the commit's three digests, **refuses before anything moves if any is missing** (no images means the tests have not passed for that commit), records the previous pins for rollback, writes digest pins into `.env`, pulls, starts, and checks that each running container is the pinned digest. Rollback is the previous digests: no rebuild, no dependence on the source tree.
6. **A loud local-build fallback.** When CI minutes are exhausted, `LOCAL_BUILD=1` builds on the host through the overlay, tags the images `carnival-app-local/<name>:<short-sha>`, writes those tags (not digests) into `.env` and prints a warning. Digest pulls stay the norm; the fallback exists so a spent allowance never blocks a fix.

Procedures: [self-hosting § upgrades](../admin/self-host.md#6-upgrades) and `scripts/deploy/README.md`.

## Consequences

- No build tooling, build cache or compile step on the school's server; base-image pulls happen on CI runners.
- "Deployed equals tested" is a statement about a digest. A failed build publishes nothing, so the deploy refuses rather than serving a stale frontend.
- A commit on `main` is not deployable until someone runs the images workflow for it, docs-only commits included.
- A fresh install is pull-only: clone (for Compose, `config/` and scripts), `.env`, optional `docker login`, `docker compose up -d`.
- Contributors need no registry credential to work on the code.
- **Residual risks:** a registry outage blocks a deploy but not a restart (already-pulled digests need no registry); the read-only token expires and must be rotated (it fails loudly at deploy); GHCR packages do not reliably inherit repository visibility, so private packages must be checked after the first push; the `main` tag is mutable, which is why hosts run digests. A locally built image is not byte-comparable with a CI-built one.

## Rejected alternatives

- **Building on every push:** exhausts a free CI allowance in a day of work; on-demand builds keep the same test gate.
- **Committing the manifest to `main` from the workflow:** the new commit has no images, so the gate would refuse every deploy; it also needs write access from CI.
- **Docker Hub:** public by default; a private plan is a second billing relationship for no gain.
- **A registry on the host:** inside the failure domain being left, and still needs something to build.
- **Building on the host from a release tag:** the old flow with extra ceremony.
- **Push-based deploy from CI:** forbidden by the constraint; it puts a credential into school infrastructure.
- **Image signing (cosign) now:** with one publisher and digest-pinned deploys it adds key lifecycle without changing who can push; wanted if a third party publishes or the images go public.
