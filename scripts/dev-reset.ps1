# dev-reset.ps1 — wipe the local throwaway SQLite dev database
# (docs/development/local-dev.md). The next API start recreates the schema
# (EnsureCreated) and reseeds the synthetic data. Stop the API first —
# Windows will refuse to delete the file while it is open.

param(
    # Matches the Database:SqlitePath default in Program.cs.
    [string]$Path = (Join-Path $env:LOCALAPPDATA "CarnivalApp\dev.db")
)

$ErrorActionPreference = "Stop"
$removed = $false
foreach ($file in @($Path, "$Path-wal", "$Path-shm")) {
    if (Test-Path $file) {
        Remove-Item $file -Force -Confirm:$false
        Write-Host "Deleted $file"
        $removed = $true
    }
}
if ($removed) {
    Write-Host "Dev database reset. Start the API (scripts\dev-run.ps1) to recreate and reseed."
} else {
    Write-Host "Nothing to delete ($Path not found) - already clean."
}
