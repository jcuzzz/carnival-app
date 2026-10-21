# dev-run.ps1 — one-command local dev loop (docs/development/local-dev.md).
# Starts the API (http://localhost:5080, Development environment, throwaway
# SQLite + synthetic seed) and the Vite dev server (http://localhost:5173)
# in two new PowerShell windows, then prints the URLs.
#
# No Docker, no Postgres, no Entra tenant, no dev certs (plain http) — this is
# the throwaway SQLite loop, NOT production parity (that is Postgres/Proxmox).
#
#   .\scripts\dev-run.ps1           # API + app
#   .\scripts\dev-run.ps1 -ApiOnly
#   .\scripts\dev-run.ps1 -AppOnly

param(
    [switch]$ApiOnly,
    [switch]$AppOnly
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $root "api\src\CarnivalApp.Api"
$appDir = Join-Path $root "app"

if (-not $AppOnly) {
    Write-Host "Starting API (SQLite dev mode, seeds synthetic data on first run)..."
    Start-Process powershell -WorkingDirectory $apiProject -ArgumentList @(
        "-NoExit", "-Command", "dotnet run --launch-profile http"
    )
}

if (-not $ApiOnly) {
    # Node lives at C:\Program Files\nodejs on this machine but is not always on PATH.
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
        $env:Path += ";C:\Program Files\nodejs"
    }
    if (-not (Test-Path (Join-Path $appDir "node_modules"))) {
        Write-Host "Installing app dependencies (first run)..."
        Push-Location $appDir
        npm install
        Pop-Location
    }
    Write-Host "Starting Vite dev server..."
    Start-Process powershell -WorkingDirectory $appDir -ArgumentList @(
        "-NoExit", "-Command", "`$env:Path += ';C:\Program Files\nodejs'; npm run dev"
    )
}

Write-Host ""
Write-Host "Local dev environment starting:"
if (-not $AppOnly) {
    Write-Host "  API     http://localhost:5080          (health: /health, version: /api/v1/version)"
}
if (-not $ApiOnly) {
    Write-Host "  App     http://localhost:5173          (Vite; /api proxied to the API)"
}
Write-Host ""
Write-Host "Dev auth: send X-Dev-Subject / X-Dev-Roles headers (see docs/development/local-dev.md)."
Write-Host "Reset the database with scripts\dev-reset.ps1 (reseeds on next API start)."
