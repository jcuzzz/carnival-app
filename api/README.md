# api/ — ASP.NET Core backend (skeleton)

Stack per [ADR-0001](../docs/adr/0001-technology-stack.md): ASP.NET Core 10 (LTS), EF Core 10 + Npgsql, versioned `/api/v1`, Microsoft.Identity.Web for Entra ID OIDC.

**Note:** this skeleton was authored by hand — the machine it was created on has only the .NET 8 SDK. Before first build, install the **.NET 10 SDK** and run:

```
cd api
dotnet restore
dotnet build
```

Package versions in the `.csproj` are best-effort pins; adjust to the latest 10.x patch on restore.

## Layout

- `src/CarnivalApp.Api/` — web API, organised as vertical slices: `Carnivals/`, `Imports/`, `Scoring/`, `Identity/`, `Auditing/`, `Idempotency/`, `Data/` (entities, configurations, migrations). `Program.cs` wires config layering, Npgsql DbContext, `/health`, and the `/api/v1` group.
- `appsettings.json` — shared defaults only.
- `appsettings.School.example.json` — the per-school configuration boundary (proposal §7.3). Real per-school files/secrets are never committed.

## Status

Phase 1 foundation and the Phase 2 MVP workflow are built (migrations, OIDC/dev-auth identity, audit, idempotency, carnival→results lifecycle, scoring, reports/exports and school records). Marshalling is server-side end to end: races carry heats and lanes (ADR-0015), a race is stamped with its event (`Race.CarnivalEventId`), an entry/team create may name the heat it joins, and the server places EVERY create — auto-filling the lane when the carnival has a lane count and `autoFillLanes` ticked (ADR-0038/0039). The Dashboard's progress aggregate (`/carnivals/{id}/progress`, ADR-0043), optional house colours (ADR-0044) and the first admin-GUI slice — the `SchoolConfiguration` row owns school name, brand colour and houses, edited through `GET/PUT /school/settings` (`school:administer`), with `School:*` configuration as the first-run seed only (ADR-0045) — are built. Current state of play: `docs/HANDOFF.md`; task tracker: `docs/BACKLOG.md`. `dotnet test` from `api/` — Testcontainers cases skip without Docker.
