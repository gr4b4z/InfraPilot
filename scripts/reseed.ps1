#Requires -Version 7
<#
.SYNOPSIS
    Throws the local database away and rebuilds it: drop, create, migrate, re-seed the demo data.

.DESCRIPTION
    There is no separate seeding command — the API seeds on startup, and every seeder is guarded on
    "only if this table is empty" (see PromotionSeedData.Seed and friends in
    src/Platform.Api/Infrastructure/Persistence). So the only way back to a clean demo dataset is an
    empty database and a restart, which is what this does:

      1. stop the API, so nothing is writing while the database goes
      2. DROP DATABASE ... WITH (FORCE), then CREATE DATABASE
      3. start the API, which applies the migrations and re-seeds
      4. report the row counts it ended up with

    The web dev server is left alone throughout — it only proxies, so it picks the new data up on the
    next request.

    Destructive by definition: local promotions, sign-offs, comments and requests are all gone
    afterwards. Prompts first unless -Force.

.PARAMETER Force
    Skip the confirmation prompt.

.PARAMETER NoStart
    Drop and recreate, but don't start the API afterwards. Leaves an empty, unmigrated database — only
    useful if the next thing you run is `dotnet ef` or the API from an IDE.

.EXAMPLE
    .\scripts\reseed.ps1

.EXAMPLE
    .\scripts\reseed.ps1 -Force
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$NoStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not $Force) {
    Write-Note "This drops the '$DbName' database on localhost:$DbPort and rebuilds it from the seed data."
    Write-Note 'Every local promotion, sign-off, comment and service request is deleted.'
    $answer = Read-Host "Reseed the local database? [y/N]"
    if ($answer -notmatch '^(y|yes)$') {
        Write-Detail 'Nothing changed.'
        return
    }
}

# The container has to be up to run psql in it — and if it wasn't, the database is on a volume that
# still holds the old data, so this isn't a no-op even then.
if ((Get-ContainerState $PgContainer) -ne 'running') {
    Start-Database
} else {
    Assert-Docker
    Write-Step "Postgres already running on localhost:$DbPort"
}

# The API holds a connection pool against the database being dropped, and — more to the point — the
# seeding only happens as it starts up. So it goes down here and comes back at the end.
Write-Step 'Stopping the API'
Stop-LocalService -Name 'api' -Label 'API' -Port $ApiPort | Out-Null

Write-Step "Recreating the '$DbName' database"
# Connected to `postgres`, because you cannot drop the database you are connected to. WITH (FORCE)
# (Postgres 13+, and the image is 16) terminates any leftover backends instead of failing on them.
Invoke-Psql -Database 'postgres' -Sql "DROP DATABASE IF EXISTS $DbName WITH (FORCE);" | Out-Null
Invoke-Psql -Database 'postgres' -Sql "CREATE DATABASE $DbName OWNER $DbUser;" | Out-Null
Write-Ok 'Database recreated (empty, no schema yet)'

if ($NoStart) {
    Write-Note 'API not started (-NoStart) — the database has no schema until something migrates it.'
    Write-Detail "Migrate with:  dotnet ef database update --project $ApiProject --configuration Release"
    return
}

# ── Migrate + seed by starting the API ───────────────────────────────────────────────────────
Write-Step 'Starting the API (applies migrations, then seeds)'
New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
$apiOut = Join-Path $StateDir 'api.log'
$apiErr = Join-Path $StateDir 'api.err.log'
$api = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', $ApiProject, '--', '--ConnectionStrings:Platform', $ConnectionString) `
    -WorkingDirectory $RepoRoot `
    -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr `
    -WindowStyle Hidden -PassThru
Save-ServicePid -Name 'api' -ProcessId $api.Id
Write-Detail "pid $($api.Id), log $apiOut"

if (-not (Wait-Until -TimeoutSec 300 -Condition { Test-ApiHealthy } `
                     -Message 'Waiting for the API to migrate and seed…')) {
    Write-Note "The API didn't answer /health in time. Last lines of ${apiErr}:"
    if (Test-Path $apiErr) { Get-Content $apiErr -Tail 20 | ForEach-Object { Write-Detail $_ } }
    throw "Reseed didn't finish. Full log: $apiOut"
}
Write-Ok "API healthy on http://localhost:$ApiPort"

# ── Report what landed ───────────────────────────────────────────────────────────────────────
# A row count is the honest answer to "did it seed?" — /health says nothing about data, and the demo
# seeders only run when ASPNETCORE_ENVIRONMENT is Development (the launch profile sets it).
Write-Step 'Seeded'
$counts = Invoke-Psql -Sql @"
select 'local_users', count(*) from local_users
union all select 'deploy_events', count(*) from deploy_events
union all select 'promotion_candidates', count(*) from promotion_candidates
union all select 'promotion_work_items', count(*) from promotion_work_items;
"@
foreach ($line in $counts) {
    $parts = "$line".Split('|')
    if ($parts.Count -eq 2) { Write-Detail ("{0,-22} {1}" -f $parts[0], $parts[1]) }
}
if ($counts -match 'promotion_candidates\|0') {
    Write-Note 'No promotion candidates were seeded — PromotionSeedData needs deploy events to derive them from.'
}

Write-Host ''
Write-Detail 'Sign in with:  admin@localhost / admin123'
Write-Detail 'The web dev server (if it was running) needs no restart — reload the page.'
