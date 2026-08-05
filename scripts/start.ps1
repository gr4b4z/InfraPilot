#Requires -Version 7
<#
.SYNOPSIS
    Brings up the whole local stack for manual testing: Postgres in Docker, the API, and the web dev
    server.

.DESCRIPTION
    Postgres runs in the compose container; the API and the web dev server run natively and detached,
    so they rebuild on save and their output goes to .local/*.log instead of tying up this terminal.

    Re-running is safe: anything already listening on its port is left alone, so this doubles as
    "start whatever isn't up". Migrations are applied by the API on startup, and in Development it
    also seeds demo data into an empty database — so a first run on a fresh volume comes up populated
    without any extra step. Use reseed.ps1 to get back to that state later.

.PARAMETER DbOnly
    Start only Postgres. For when the API and the web app are launched from an IDE or the Claude Code
    preview pane, which is the usual setup while debugging.

.PARAMETER SkipWeb
    Start Postgres and the API but not the web dev server — e.g. when testing the API directly.

.EXAMPLE
    .\scripts\start.ps1

.EXAMPLE
    .\scripts\start.ps1 -DbOnly
#>
[CmdletBinding()]
param(
    [switch]$DbOnly,
    [switch]$SkipWeb
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

New-Item -ItemType Directory -Force -Path $StateDir | Out-Null

Start-Database

if ($DbOnly) {
    Write-Host ''
    Write-Step 'Postgres only — API and web not started'
    Write-Detail "Connection string the API needs:"
    Write-Detail "  $ConnectionString"
    Write-Detail "psql: docker exec -it $PgContainer psql -U $DbUser -d $DbName"
    return
}

# ── API ──────────────────────────────────────────────────────────────────────────────────────
# The default launch profile supplies the URL (5259) and ASPNETCORE_ENVIRONMENT=Development, and
# Development is what enables demo seeding — so the profile is deliberately left in play and only the
# connection string is overridden. See $ConnectionString in _common.ps1 for why that override exists.
if (Test-Port $ApiPort) {
    Write-Step "API already listening on $ApiPort — leaving it alone"
} else {
    Write-Step 'Starting the API'
    $apiOut = Join-Path $StateDir 'api.log'
    $apiErr = Join-Path $StateDir 'api.err.log'
    $api = Start-Process -FilePath 'dotnet' `
        -ArgumentList @('run', '--project', $ApiProject, '--', '--ConnectionStrings:Platform', $ConnectionString) `
        -WorkingDirectory $RepoRoot `
        -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr `
        -WindowStyle Hidden -PassThru
    Save-ServicePid -Name 'api' -ProcessId $api.Id
    Write-Detail "pid $($api.Id), log $apiOut"

    # Generous timeout: a cold build plus migrations on a fresh volume is minutes, not seconds.
    if (-not (Wait-Until -TimeoutSec 300 -Condition { Test-ApiHealthy } `
                         -Message 'Waiting for the API (building, migrating, seeding)…')) {
        Write-Note "The API didn't answer /health in time. Last lines of ${apiErr}:"
        if (Test-Path $apiErr) { Get-Content $apiErr -Tail 20 | ForEach-Object { Write-Detail $_ } }
        throw "API not healthy. Full log: $apiOut"
    }
    Write-Ok "API healthy on http://localhost:$ApiPort"
}

# ── Web dev server ───────────────────────────────────────────────────────────────────────────
$webUrl = "http://localhost:$WebPort"
if ($SkipWeb) {
    Write-Step 'Skipping the web dev server (-SkipWeb)'
} elseif (Test-Port $WebPort) {
    Write-Step "Web dev server already listening on $WebPort — leaving it alone"
} else {
    if (-not (Test-Path (Join-Path $WebDir 'node_modules'))) {
        Write-Step 'Installing web dependencies (first run)'
        Push-Location $WebDir
        try {
            & npm install
            if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }
        } finally {
            Pop-Location
        }
    }

    Write-Step 'Starting the web dev server'
    $webOut = Join-Path $StateDir 'web.log'
    $webErr = Join-Path $StateDir 'web.err.log'
    $web = Start-Process -FilePath 'npm.cmd' -ArgumentList @('run', 'dev') `
        -WorkingDirectory $WebDir `
        -RedirectStandardOutput $webOut -RedirectStandardError $webErr `
        -WindowStyle Hidden -PassThru
    Save-ServicePid -Name 'web' -ProcessId $web.Id
    Write-Detail "pid $($web.Id), log $webOut"

    # Vite treats 5173 as preferred, not required, and falls through to the next free port. Read the
    # port back out of its own output rather than assuming, or the URL printed below can be wrong.
    $found = Wait-Until -TimeoutSec 120 -Message 'Waiting for Vite…' -Condition {
        (Test-Path $webOut) -and ((Get-Content $webOut -Raw) -match 'Local:\s+(http://localhost:\d+)')
    }
    if (-not $found) {
        Write-Note "The dev server didn't report a URL in time. Last lines of ${webOut}:"
        if (Test-Path $webOut) { Get-Content $webOut -Tail 20 | ForEach-Object { Write-Detail $_ } }
        throw "Web dev server didn't start. Full log: $webOut"
    }
    if ((Get-Content $webOut -Raw) -match 'Local:\s+(http://localhost:\d+)') { $webUrl = $Matches[1] }
    Write-Ok "Web dev server on $webUrl"
}

# ── Summary ──────────────────────────────────────────────────────────────────────────────────
# The dev accounts are seeded by SeedData.SeedLocalUsers whenever MSAL isn't configured, which is the
# case locally; the login page lists them too.
Write-Host ''
Write-Step 'Ready'
Write-Host "    App          $webUrl" -ForegroundColor White
Write-Detail "API          http://localhost:$ApiPort  (health: /health, spec: /openapi/v1.json)"
Write-Detail "Postgres     localhost:$DbPort  database '$DbName'"
Write-Host ''
Write-Detail 'Sign in with:  admin@localhost / admin123   (also user@localhost, viewer@localhost)'
Write-Detail "Logs:          Get-Content $(Join-Path $StateDir 'api.log') -Wait -Tail 20"
Write-Detail 'Reset data:    .\scripts\reseed.ps1'
Write-Detail 'Shut down:     .\scripts\stop.ps1'
