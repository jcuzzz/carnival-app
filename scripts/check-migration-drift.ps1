#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Fails when the EF migration history builds a database that differs from the model.

.DESCRIPTION
    `dotnet ef migrations has-pending-model-changes` cannot see a hand-edited migration:
    it compares the model to a snapshot that was itself generated from the model. This
    compares the two SQL scripts instead --

        dotnet ef dbcontext script   model   -> SQL (what EnsureCreated builds: tests, dev loop)
        dotnet ef migrations script  history -> SQL (what production gets)

    -- by replaying the migration script into a final schema state and diffing the
    comparable classes against the model script. Both commands run offline against the
    design-time factory, so no database is needed.

    Compared: tables, foreign keys (referenced table/columns plus ON DELETE and ON UPDATE
    actions), indexes (uniqueness, columns, filter), check constraints, column DDL DEFAULTs.
    Not compared: column types, nullability, ordering, identity/sequence details.
#>
[CmdletBinding()]
param(
    # Defaults to api/src/CarnivalApp.Api relative to the repo root.
    [string] $Project,
    # Keep the generated .sql files instead of deleting them (debugging aid).
    [switch] $KeepScripts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# pwsh 7.4+ turns a non-zero native exit code into a terminating error under
# ErrorActionPreference=Stop; this script checks $LASTEXITCODE itself.
if (Test-Path variable:PSNativeCommandUseErrorActionPreference) {
    $PSNativeCommandUseErrorActionPreference = $false
}

# EF's AddColumn(defaultValue:) backfill scaffolding emits a DDL DEFAULT and never a
# matching DROP DEFAULT, so a migrations-built database keeps defaults an EnsureCreated
# one lacks. Stock EF behaviour, not drift -- see docs/BACKLOG.md, "Six columns carry a
# residual DDL DEFAULT in a migrations-built database". Tolerated in that direction only:
# a default appearing on the MODEL side, or one of these losing its migrations-side
# default, is still reported.
$KnownDefaultResiduals = @(
    'Carnivals.Version'
    'CarnivalEvents.ScoresPoints'
    'CarnivalEvents.Tier'
    'CarnivalEvents.Version'
    'EventDefinitions.Format'
    'EventDefinitions.ScoringWeight'
)

# Created by `dotnet ef migrations script` itself, never by the model.
$IgnoredTables = @('__EFMigrationsHistory')

function Split-SqlStatements {
    param([string] $Sql)

    $statements = [System.Collections.Generic.List[string]]::new()
    $sb = [System.Text.StringBuilder]::new()
    $i = 0
    $n = $Sql.Length

    while ($i -lt $n) {
        $c = $Sql[$i]

        if ($c -eq "'") {
            $j = $i + 1
            while ($j -lt $n) {
                if ($Sql[$j] -eq "'") {
                    if ($j + 1 -lt $n -and $Sql[$j + 1] -eq "'") { $j += 2; continue }
                    break
                }
                $j++
            }
            [void]$sb.Append($Sql.Substring($i, [Math]::Min($j + 1, $n) - $i))
            $i = $j + 1
            continue
        }

        if ($c -eq '"') {
            $j = $i + 1
            while ($j -lt $n -and $Sql[$j] -ne '"') { $j++ }
            [void]$sb.Append($Sql.Substring($i, [Math]::Min($j + 1, $n) - $i))
            $i = $j + 1
            continue
        }

        # Dollar quoting ($$...$$, $tag$...$tag$) -- migrations use DO blocks whose bodies
        # contain both semicolons and -- comments.
        if ($c -eq '$') {
            $m = [regex]::Match($Sql.Substring($i), '^\$[A-Za-z_0-9]*\$')
            if ($m.Success) {
                $tag = $m.Value
                $close = $Sql.IndexOf($tag, $i + $tag.Length, [StringComparison]::Ordinal)
                if ($close -lt 0) { $close = $n - $tag.Length }
                $end = $close + $tag.Length
                [void]$sb.Append($Sql.Substring($i, $end - $i))
                $i = $end
                continue
            }
        }

        if ($c -eq '-' -and $i + 1 -lt $n -and $Sql[$i + 1] -eq '-') {
            $j = $Sql.IndexOfAny([char[]]("`n", "`r"), $i)
            if ($j -lt 0) { $j = $n }
            [void]$sb.Append(' ')
            $i = $j
            continue
        }

        if ($c -eq '/' -and $i + 1 -lt $n -and $Sql[$i + 1] -eq '*') {
            $j = $Sql.IndexOf('*/', $i + 2, [StringComparison]::Ordinal)
            if ($j -lt 0) { $j = $n - 2 }
            [void]$sb.Append(' ')
            $i = $j + 2
            continue
        }

        if ($c -eq ';') {
            $statements.Add($sb.ToString())
            [void]$sb.Clear()
            $i++
            continue
        }

        [void]$sb.Append($c)
        $i++
    }

    $statements.Add($sb.ToString())
    return @($statements | ForEach-Object { ($_ -replace '\s+', ' ').Trim() } | Where-Object { $_ })
}

function Split-TopLevelCommas {
    param([string] $Text)

    $parts = [System.Collections.Generic.List[string]]::new()
    $sb = [System.Text.StringBuilder]::new()
    $depth = 0
    $i = 0
    $n = $Text.Length

    while ($i -lt $n) {
        $c = $Text[$i]
        if ($c -eq "'") {
            $j = $i + 1
            while ($j -lt $n) {
                if ($Text[$j] -eq "'") {
                    if ($j + 1 -lt $n -and $Text[$j + 1] -eq "'") { $j += 2; continue }
                    break
                }
                $j++
            }
            [void]$sb.Append($Text.Substring($i, [Math]::Min($j + 1, $n) - $i))
            $i = $j + 1
            continue
        }
        if ($c -eq '"') {
            $j = $i + 1
            while ($j -lt $n -and $Text[$j] -ne '"') { $j++ }
            [void]$sb.Append($Text.Substring($i, [Math]::Min($j + 1, $n) - $i))
            $i = $j + 1
            continue
        }
        if ($c -eq '(') { $depth++ }
        elseif ($c -eq ')') { $depth-- }
        elseif ($c -eq ',' -and $depth -eq 0) {
            $parts.Add($sb.ToString().Trim())
            [void]$sb.Clear()
            $i++
            continue
        }
        [void]$sb.Append($c)
        $i++
    }
    $parts.Add($sb.ToString().Trim())
    return @($parts | Where-Object { $_ })
}

function Get-ParenBody {
    param([string] $Text, [int] $From = 0)

    $open = $Text.IndexOf('(', $From)
    if ($open -lt 0) { return $null }
    $depth = 0
    $i = $open
    $n = $Text.Length
    while ($i -lt $n) {
        $c = $Text[$i]
        if ($c -eq "'") {
            $j = $i + 1
            while ($j -lt $n) {
                if ($Text[$j] -eq "'") {
                    if ($j + 1 -lt $n -and $Text[$j + 1] -eq "'") { $j += 2; continue }
                    break
                }
                $j++
            }
            $i = $j + 1
            continue
        }
        if ($c -eq '"') {
            $j = $i + 1
            while ($j -lt $n -and $Text[$j] -ne '"') { $j++ }
            $i = $j + 1
            continue
        }
        if ($c -eq '(') { $depth++ }
        elseif ($c -eq ')') {
            $depth--
            if ($depth -eq 0) {
                return [pscustomobject]@{
                    Body  = $Text.Substring($open + 1, $i - $open - 1).Trim()
                    After = $Text.Substring($i + 1).Trim()
                }
            }
        }
        $i++
    }
    return $null
}

function New-SchemaState {
    return [pscustomobject]@{
        Tables       = [System.Collections.Generic.HashSet[string]]::new()
        ForeignKeys  = @{}
        Checks       = @{}
        Indexes      = @{}
        Defaults     = @{}
    }
}

function ConvertTo-ColumnList {
    param([string] $Text)
    return (($Text -replace '\s+', ' ').Trim())
}

function Get-ForeignKeyDescriptor {
    param([string] $Clause)

    $cols = Get-ParenBody -Text $Clause
    if (-not $cols) { return $null }
    $rest = $cols.After
    $m = [regex]::Match($rest, '^REFERENCES\s+("(?<t>[^"]+)"|(?<t2>[^\s(]+))\s*', 'IgnoreCase')
    if (-not $m.Success) { return $null }
    $target = if ($m.Groups['t'].Success) { $m.Groups['t'].Value } else { $m.Groups['t2'].Value }
    $targetCols = Get-ParenBody -Text $rest.Substring($m.Length)
    $targetColText = if ($targetCols) { ConvertTo-ColumnList $targetCols.Body } else { '' }
    $tail = if ($targetCols) { $targetCols.After } else { $rest.Substring($m.Length) }

    $onDelete = 'NO ACTION'
    $onUpdate = 'NO ACTION'
    $d = [regex]::Match($tail, 'ON\s+DELETE\s+(?<a>CASCADE|RESTRICT|SET\s+NULL|SET\s+DEFAULT|NO\s+ACTION)', 'IgnoreCase')
    if ($d.Success) { $onDelete = ($d.Groups['a'].Value -replace '\s+', ' ').ToUpperInvariant() }
    $u = [regex]::Match($tail, 'ON\s+UPDATE\s+(?<a>CASCADE|RESTRICT|SET\s+NULL|SET\s+DEFAULT|NO\s+ACTION)', 'IgnoreCase')
    if ($u.Success) { $onUpdate = ($u.Groups['a'].Value -replace '\s+', ' ').ToUpperInvariant() }

    return "($(ConvertTo-ColumnList $cols.Body)) -> `"$target`" ($targetColText) ON DELETE $onDelete ON UPDATE $onUpdate"
}

function Get-ColumnDefault {
    param([string] $Definition)

    # GENERATED BY DEFAULT AS IDENTITY is not a DDL DEFAULT.
    $d = [regex]::Replace($Definition, 'GENERATED\s+(BY\s+DEFAULT|ALWAYS)\s+AS\s+IDENTITY', ' ', 'IgnoreCase')
    $m = [regex]::Match($d, '\bDEFAULT\b\s+(?<v>.+)$', 'IgnoreCase')
    if (-not $m.Success) { return $null }
    return ($m.Groups['v'].Value -replace '\s+', ' ').Trim()
}

function Remove-TableFromState {
    param($State, [string] $Table)
    [void]$State.Tables.Remove($Table)
    foreach ($k in @($State.ForeignKeys.Keys)) { if ($k -like "$Table.*") { $State.ForeignKeys.Remove($k) } }
    foreach ($k in @($State.Checks.Keys))      { if ($k -like "$Table.*") { $State.Checks.Remove($k) } }
    foreach ($k in @($State.Defaults.Keys))    { if ($k -like "$Table.*") { $State.Defaults.Remove($k) } }
    foreach ($k in @($State.Indexes.Keys))     { if ($State.Indexes[$k].Table -eq $Table) { $State.Indexes.Remove($k) } }
}

function Add-TableConstraint {
    param($State, [string] $Table, [string] $Item)

    $m = [regex]::Match($Item, '^CONSTRAINT\s+"(?<n>[^"]+)"\s+(?<kind>PRIMARY\s+KEY|FOREIGN\s+KEY|UNIQUE|CHECK)\s*(?<rest>.*)$', 'IgnoreCase')
    if (-not $m.Success) { return $false }
    $name = $m.Groups['n'].Value
    $kind = ($m.Groups['kind'].Value -replace '\s+', ' ').ToUpperInvariant()
    $rest = $m.Groups['rest'].Value

    switch ($kind) {
        'FOREIGN KEY' {
            $desc = Get-ForeignKeyDescriptor -Clause $rest
            if ($null -eq $desc) { return $false }
            $State.ForeignKeys["$Table.$name"] = $desc
        }
        'CHECK' {
            $body = Get-ParenBody -Text $rest
            if (-not $body) { return $false }
            $State.Checks["$Table.$name"] = ($body.Body -replace '\s+', ' ').Trim()
        }
        default { }   # PRIMARY KEY / UNIQUE constraints: not compared
    }
    return $true
}

function Invoke-SqlStatement {
    param($State, [string] $Statement)

    $s = $Statement

    if ($s -match '^(START\s+TRANSACTION|COMMIT|ROLLBACK|BEGIN|SET\s|DO\s|INSERT\s|UPDATE\s|DELETE\s|SELECT\s|WITH\s|--)') { return $true }
    if ($s -match '^(CREATE|ALTER|DROP)\s+(SEQUENCE|SCHEMA|EXTENSION|TYPE|FUNCTION|TRIGGER|VIEW|COLLATION)\b') { return $true }
    if ($s -match '^COMMENT\s+ON\b') { return $true }

    $m = [regex]::Match($s, '^CREATE\s+TABLE\s+(IF\s+NOT\s+EXISTS\s+)?"(?<t>[^"]+)"\s*(?<rest>\(.*)$', 'IgnoreCase')
    if ($m.Success) {
        $table = $m.Groups['t'].Value
        if ($IgnoredTables -contains $table) { return $true }
        [void]$State.Tables.Add($table)
        $body = Get-ParenBody -Text $m.Groups['rest'].Value
        if (-not $body) { return $false }
        foreach ($item in Split-TopLevelCommas $body.Body) {
            if ($item -match '^CONSTRAINT\s') {
                if (-not (Add-TableConstraint -State $State -Table $table -Item $item)) { return $false }
                continue
            }
            $c = [regex]::Match($item, '^"(?<c>[^"]+)"\s+(?<def>.*)$')
            if (-not $c.Success) { continue }
            $def = Get-ColumnDefault -Definition $c.Groups['def'].Value
            if ($null -ne $def) { $State.Defaults["$table.$($c.Groups['c'].Value)"] = $def }
        }
        return $true
    }

    $m = [regex]::Match($s, '^DROP\s+TABLE\s+(IF\s+EXISTS\s+)?"(?<t>[^"]+)"', 'IgnoreCase')
    if ($m.Success) { Remove-TableFromState -State $State -Table $m.Groups['t'].Value; return $true }

    $m = [regex]::Match($s, '^CREATE\s+(?<u>UNIQUE\s+)?INDEX\s+(IF\s+NOT\s+EXISTS\s+)?"(?<n>[^"]+)"\s+ON\s+"(?<t>[^"]+)"\s*(?<rest>\(.*)$', 'IgnoreCase')
    if ($m.Success) {
        $body = Get-ParenBody -Text $m.Groups['rest'].Value
        if (-not $body) { return $false }
        $filter = ''
        $f = [regex]::Match($body.After, '\bWHERE\b\s+(?<w>.+)$', 'IgnoreCase')
        if ($f.Success) { $filter = ($f.Groups['w'].Value -replace '\s+', ' ').Trim() }
        $State.Indexes[$m.Groups['n'].Value] = [pscustomobject]@{
            Table   = $m.Groups['t'].Value
            Unique  = [bool]$m.Groups['u'].Success
            Columns = ConvertTo-ColumnList $body.Body
            Filter  = $filter
        }
        return $true
    }

    $m = [regex]::Match($s, '^DROP\s+INDEX\s+(CONCURRENTLY\s+)?(IF\s+EXISTS\s+)?"(?<n>[^"]+)"', 'IgnoreCase')
    if ($m.Success) { $State.Indexes.Remove($m.Groups['n'].Value); return $true }

    $m = [regex]::Match($s, '^ALTER\s+INDEX\s+"(?<n>[^"]+)"\s+RENAME\s+TO\s+"(?<m>[^"]+)"', 'IgnoreCase')
    if ($m.Success) {
        $old = $m.Groups['n'].Value
        if ($State.Indexes.ContainsKey($old)) {
            $State.Indexes[$m.Groups['m'].Value] = $State.Indexes[$old]
            $State.Indexes.Remove($old)
        }
        return $true
    }

    $m = [regex]::Match($s, '^ALTER\s+TABLE\s+(ONLY\s+)?"(?<t>[^"]+)"\s+(?<rest>.*)$', 'IgnoreCase')
    if ($m.Success) {
        $table = $m.Groups['t'].Value
        $rest = $m.Groups['rest'].Value
        if ($IgnoredTables -contains $table) { return $true }

        if ($rest -match '^ADD\s+CONSTRAINT\s') {
            return (Add-TableConstraint -State $State -Table $table -Item ($rest -replace '^ADD\s+', ''))
        }
        $c = [regex]::Match($rest, '^DROP\s+CONSTRAINT\s+(IF\s+EXISTS\s+)?"(?<n>[^"]+)"', 'IgnoreCase')
        if ($c.Success) {
            $State.ForeignKeys.Remove("$table.$($c.Groups['n'].Value)")
            $State.Checks.Remove("$table.$($c.Groups['n'].Value)")
            return $true
        }
        $c = [regex]::Match($rest, '^ADD\s+(COLUMN\s+)?(IF\s+NOT\s+EXISTS\s+)?"(?<c>[^"]+)"\s+(?<def>.*)$', 'IgnoreCase')
        if ($c.Success) {
            $def = Get-ColumnDefault -Definition $c.Groups['def'].Value
            $key = "$table.$($c.Groups['c'].Value)"
            if ($null -ne $def) { $State.Defaults[$key] = $def } else { $State.Defaults.Remove($key) }
            return $true
        }
        $c = [regex]::Match($rest, '^DROP\s+COLUMN\s+(IF\s+EXISTS\s+)?"(?<c>[^"]+)"', 'IgnoreCase')
        if ($c.Success) { $State.Defaults.Remove("$table.$($c.Groups['c'].Value)"); return $true }
        $c = [regex]::Match($rest, '^ALTER\s+COLUMN\s+"(?<c>[^"]+)"\s+(?<action>.*)$', 'IgnoreCase')
        if ($c.Success) {
            $key = "$table.$($c.Groups['c'].Value)"
            $action = $c.Groups['action'].Value
            if ($action -match '^DROP\s+DEFAULT') { $State.Defaults.Remove($key); return $true }
            $d = [regex]::Match($action, '^SET\s+DEFAULT\s+(?<v>.+)$', 'IgnoreCase')
            if ($d.Success) { $State.Defaults[$key] = ($d.Groups['v'].Value -replace '\s+', ' ').Trim(); return $true }
            # TYPE / SET NOT NULL / DROP NOT NULL / SET STATISTICS: no tracked effect.
            if ($action -match '^(TYPE|SET\s+NOT\s+NULL|DROP\s+NOT\s+NULL|SET\s+STATISTICS|SET\s+STORAGE|ADD\s+GENERATED|DROP\s+IDENTITY)') { return $true }
            return $false
        }
        $c = [regex]::Match($rest, '^RENAME\s+COLUMN\s+"(?<a>[^"]+)"\s+TO\s+"(?<b>[^"]+)"', 'IgnoreCase')
        if ($c.Success) {
            $from = "$table.$($c.Groups['a'].Value)"
            if ($State.Defaults.ContainsKey($from)) {
                $State.Defaults["$table.$($c.Groups['b'].Value)"] = $State.Defaults[$from]
                $State.Defaults.Remove($from)
            }
            return $true
        }
        $c = [regex]::Match($rest, '^RENAME\s+TO\s+"(?<b>[^"]+)"', 'IgnoreCase')
        if ($c.Success) { return $false }   # table rename: not modelled, fail loudly rather than diff wrongly
        return $false
    }

    if ($s -match '^(CREATE|ALTER|DROP)\b') { return $false }
    return $true
}

function Build-Schema {
    param([string] $Sql, [string] $Label)

    $state = New-SchemaState
    $unrecognised = [System.Collections.Generic.List[string]]::new()
    foreach ($statement in Split-SqlStatements $Sql) {
        if (-not (Invoke-SqlStatement -State $state -Statement $statement)) {
            $trimmed = if ($statement.Length -gt 200) { $statement.Substring(0, 200) + ' ...' } else { $statement }
            $unrecognised.Add("[$Label] $trimmed")
        }
    }
    return [pscustomobject]@{ State = $state; Unrecognised = $unrecognised }
}

function Compare-Map {
    param(
        [string] $ClassName,
        [hashtable] $Model,
        [hashtable] $Migrations,
        [scriptblock] $Format = { param($v) [string]$v },
        [string[]] $Allow = @()
    )

    $findings = [System.Collections.Generic.List[string]]::new()
    $matched = [System.Collections.Generic.List[string]]::new()
    $keys = @(@($Model.Keys) + @($Migrations.Keys) | Sort-Object -Unique)
    foreach ($key in $keys) {
        $inModel = $Model.ContainsKey($key)
        $inMig = $Migrations.ContainsKey($key)
        $mv = if ($inModel) { & $Format $Model[$key] } else { $null }
        $gv = if ($inMig) { & $Format $Migrations[$key] } else { $null }
        if ($inModel -and $inMig -and $mv -eq $gv) { continue }

        # The documented residual shape only: present in migrations, absent from the model.
        if ((-not $inModel) -and $inMig -and ($Allow -contains $key)) { $matched.Add($key); continue }

        $findings.Add("$ClassName  $key")
        $findings.Add("    model      : $(if ($inModel) { $mv } else { '(absent)' })")
        $findings.Add("    migrations : $(if ($inMig) { $gv } else { '(absent)' })")
    }
    return [pscustomobject]@{ Findings = $findings; AllowlistHits = $matched }
}

# ---------------------------------------------------------------------------

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Project) { $Project = Join-Path $repoRoot 'api/src/CarnivalApp.Api' }
if (-not (Test-Path $Project)) { throw "Project directory not found: $Project" }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet was not found on PATH.' }

$work = Join-Path ([System.IO.Path]::GetTempPath()) ("migration-drift-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$modelSql = Join-Path $work 'model.sql'
$migrationsSql = Join-Path $work 'migrations.sql'

try {
    Push-Location $Project
    try {
        # Probed HERE, not from the repo root: api/dotnet-tools.json pins dotnet-ef as a
        # local tool, and manifest resolution walks up from the CURRENT directory — a probe
        # from the root sees only a global install while the scripting calls below see the
        # manifest, which is exactly the split that broke CI (exit 2, 2026-08-22).
        & dotnet ef --version *> $null
        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet-ef is not available here. Run: dotnet tool restore (from api/, ' +
                'where dotnet-tools.json pins it).'
        }

        Write-Host 'Building the API project...'
        & dotnet build --nologo --verbosity quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed in $Project." }

        Write-Host 'Scripting the model (dotnet ef dbcontext script)...'
        & dotnet ef dbcontext script --no-build --context SportsDbContext --output $modelSql | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'dotnet ef dbcontext script failed.' }

        Write-Host 'Scripting the migration history (dotnet ef migrations script)...'
        & dotnet ef migrations script --no-build --context SportsDbContext --output $migrationsSql | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'dotnet ef migrations script failed.' }
    }
    finally { Pop-Location }

    $modelText = (Get-Content -Path $modelSql -Raw).TrimStart([char]0xFEFF)
    $migrationsText = (Get-Content -Path $migrationsSql -Raw).TrimStart([char]0xFEFF)

    $model = Build-Schema -Sql $modelText -Label 'model'
    $migrations = Build-Schema -Sql $migrationsText -Label 'migrations'

    $problems = [System.Collections.Generic.List[string]]::new()

    $unrecognised = @($model.Unrecognised) + @($migrations.Unrecognised)
    if ($unrecognised.Count -gt 0) {
        $problems.Add('UNRECOGNISED SQL -- the drift checker cannot account for these statements,')
        $problems.Add('so it cannot certify the comparison. Teach scripts/check-migration-drift.ps1')
        $problems.Add('to handle them (Invoke-SqlStatement), then re-run.')
        foreach ($u in $unrecognised) { $problems.Add("    $u") }
        $problems.Add('')
    }

    $modelTables = @{}; foreach ($t in $model.State.Tables) { $modelTables[$t] = 'present' }
    $migrationTables = @{}; foreach ($t in $migrations.State.Tables) { $migrationTables[$t] = 'present' }

    $comparisons = @(
        (Compare-Map -ClassName 'TABLE            ' -Model $modelTables -Migrations $migrationTables)
        (Compare-Map -ClassName 'FOREIGN KEY      ' -Model $model.State.ForeignKeys -Migrations $migrations.State.ForeignKeys)
        (Compare-Map -ClassName 'INDEX            ' -Model $model.State.Indexes -Migrations $migrations.State.Indexes `
            -Format { param($v) "$(if ($v.Unique) { 'UNIQUE ' } else { '' })ON `"$($v.Table)`" ($($v.Columns))$(if ($v.Filter) { " WHERE $($v.Filter)" } else { '' })" })
        (Compare-Map -ClassName 'CHECK CONSTRAINT ' -Model $model.State.Checks -Migrations $migrations.State.Checks)
        (Compare-Map -ClassName 'COLUMN DEFAULT   ' -Model $model.State.Defaults -Migrations $migrations.State.Defaults `
            -Allow $KnownDefaultResiduals)
    )

    foreach ($c in $comparisons) { foreach ($f in $c.Findings) { $problems.Add($f) } }

    $allowHits = @($comparisons | ForEach-Object { $_.AllowlistHits })
    $stale = @($KnownDefaultResiduals | Where-Object { $allowHits -notcontains $_ })

    Write-Host ''
    Write-Host ("Compared: {0} tables, {1} foreign keys, {2} indexes, {3} check constraints, {4} column defaults." -f `
        $modelTables.Count, $model.State.ForeignKeys.Count, $model.State.Indexes.Count,
        $model.State.Checks.Count, $migrations.State.Defaults.Count)
    Write-Host ("Tolerated: {0} known EF backfill DEFAULT residuals (docs/BACKLOG.md)." -f $allowHits.Count)

    if ($stale.Count -gt 0) {
        $problems.Add('STALE ALLOWLIST -- these no longer diverge, so `$KnownDefaultResiduals in')
        $problems.Add('scripts/check-migration-drift.ps1 must drop them (and docs/BACKLOG.md updated):')
        foreach ($e in $stale) { $problems.Add("    $e") }
    }

    if ($problems.Count -gt 0) {
        Write-Host ''
        Write-Host 'MIGRATION DRIFT: the migration history builds a database that differs from the model.' -ForegroundColor Red
        Write-Host 'Fix by adding a migration that reconciles the difference -- never by editing a migration' -ForegroundColor Red
        Write-Host 'that has already been applied (see commit 80ae244).' -ForegroundColor Red
        Write-Host ''
        foreach ($p in $problems) { Write-Host $p }
        Write-Host ''
        if ($env:GITHUB_ACTIONS) {
            Write-Host '::error file=api/src/CarnivalApp.Api/Migrations::Migration/model drift detected -- see the report in this step.'
        }
        exit 1
    }

    Write-Host 'No drift: the migration history and the model build the same schema.' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}
finally {
    if ($KeepScripts) { Write-Host "Generated SQL kept in $work" }
    elseif (Test-Path $work) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}
