<#
    Shared plumbing for the local-dev scripts (start.ps1 / stop.ps1 / reseed.ps1).

    Dot-source it; it isn't an entry point:

        . (Join-Path $PSScriptRoot '_common.ps1')

    Everything the three scripts disagreeing about would break — ports, the container name, and above
    all the connection string — is defined here once.
#>

Set-StrictMode -Version Latest

# ── Layout ───────────────────────────────────────────────────────────────────────────────────
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$ComposeFile = Join-Path $RepoRoot 'docker-compose.yml'
$ApiProject = Join-Path $RepoRoot 'src/Platform.Api/Platform.Api.csproj'
$WebDir     = Join-Path $RepoRoot 'src/Platform.Web'
# Logs and pid files for the two servers we start detached. Gitignored — see .gitignore.
$StateDir   = Join-Path $RepoRoot '.local'

# ── Ports and database ───────────────────────────────────────────────────────────────────────
# 5259 comes from src/Platform.Api/Properties/launchSettings.json, 5173 from vite.config.ts, and
# 5433 from the postgres service's port mapping in docker-compose.yml.
$ApiPort = 5259
$WebPort = 5173
$DbPort  = 5433

$PgService   = 'postgres'             # service name in docker-compose.yml
$PgContainer = 'infrapilot-postgres'  # container_name in the same file
$DbName      = 'swo_platform'
$DbUser      = 'postgres'
$DbPassword  = 'postgres'

<#
    The connection string is passed on the API's command line rather than left to configuration,
    because the committed default doesn't point at this database: appsettings.json has
    `Host=localhost;Database=platform` — port 5432, database `platform` — while docker-compose serves
    `swo_platform` on 5433. appsettings.Development.json would override it but is gitignored, so every
    clone would have to recreate it. .claude/launch.json passes the same string for the same reason.
#>
$ConnectionString = "Host=localhost;Port=$DbPort;Database=$DbName;Username=$DbUser;Password=$DbPassword"

# ── Console output ───────────────────────────────────────────────────────────────────────────

function Write-Step   { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok     { param([string]$Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Detail { param([string]$Message) Write-Host "    $Message" -ForegroundColor DarkGray }
function Write-Note   { param([string]$Message) Write-Host "    $Message" -ForegroundColor Yellow }

# ── Waiting ──────────────────────────────────────────────────────────────────────────────────

<#
    Polls until $Condition returns true. Returns $false on timeout rather than throwing, so callers
    can attach an error message that says which log to read.
#>
function Wait-Until {
    param(
        [Parameter(Mandatory)][scriptblock]$Condition,
        [int]$TimeoutSec = 60,
        [string]$Message
    )
    if ($Message) { Write-Detail $Message }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

<#
    True when something is listening on the port locally. A bare TcpClient connect rather than
    Test-NetConnection, which adds DNS and ICMP work we don't need and takes about a second per call.
#>
function Test-Port {
    param([Parameter(Mandatory)][int]$Port)
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        return $client.ConnectAsync('127.0.0.1', $Port).Wait(300) -and $client.Connected
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}

function Test-ApiHealthy {
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:$ApiPort/health" -TimeoutSec 3 -SkipHttpErrorCheck
        return $response.StatusCode -eq 200
    } catch {
        return $false
    }
}

# ── Docker / Postgres ────────────────────────────────────────────────────────────────────────

function Assert-Docker {
    $version = & docker version --format '{{.Server.Version}}' 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $version) {
        throw "Docker isn't responding. Start Docker Desktop and try again — the local Postgres runs in the '$PgContainer' container (docker-compose.yml)."
    }
    Write-Detail "Docker engine $version"
}

<# Container status ('running', 'exited', …), or '' when no such container exists. #>
function Get-ContainerState {
    param([Parameter(Mandatory)][string]$Name)
    $state = & docker inspect --format '{{.State.Status}}' $Name 2>$null
    if ($LASTEXITCODE -ne 0) { return '' }
    return "$state".Trim()
}

<# Healthcheck status ('healthy', 'starting', …), 'none' when the image declares no healthcheck. #>
function Get-ContainerHealth {
    param([Parameter(Mandatory)][string]$Name)
    $health = & docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' $Name 2>$null
    if ($LASTEXITCODE -ne 0) { return '' }
    return "$health".Trim()
}

<#
    Brings up only the postgres service. The compose file also defines `api` and `frontend`
    containers, but those are the all-in-Docker path — these scripts run the API and the web dev
    server natively so they rebuild on save and can be debugged from an IDE.

    The data lives in the `postgres_data` volume, so stopping the container keeps the database.
#>
function Start-Database {
    Assert-Docker
    Write-Step 'Starting Postgres'
    & docker compose --file $ComposeFile up --detach $PgService
    if ($LASTEXITCODE -ne 0) { throw 'docker compose up failed for the postgres service.' }

    if (-not (Wait-Until -TimeoutSec 90 -Message "Waiting for $PgContainer to report healthy…" `
                         -Condition { (Get-ContainerHealth $PgContainer) -eq 'healthy' })) {
        throw "Postgres never became healthy. Check: docker logs $PgContainer"
    }
    Write-Ok "Postgres ready on localhost:$DbPort (database '$DbName')"
}

<#
    Runs SQL in the container's own psql, which keeps the scripts free of a local psql dependency.
    Output comes back unaligned and without headers — these are one-line answers read by the caller,
    not tables shown to a human.
#>
function Invoke-Psql {
    param(
        [Parameter(Mandatory)][string]$Sql,
        [string]$Database = $DbName
    )
    $output = & docker exec $PgContainer psql `
        --username $DbUser --dbname $Database `
        --no-psqlrc --quiet --tuples-only --no-align --command $Sql 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "psql failed: $output"
    }
    return $output
}

# ── Detached servers (pid files) ─────────────────────────────────────────────────────────────

function Get-PidFilePath {
    param([Parameter(Mandatory)][string]$Name)
    Join-Path $StateDir "$Name.pid"
}

function Save-ServicePid {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][int]$ProcessId)
    New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
    Set-Content -Path (Get-PidFilePath $Name) -Value $ProcessId
}

<# The recorded pid if that process is still alive, otherwise 0 — a stale file reads as "not running". #>
function Get-ServicePid {
    param([Parameter(Mandatory)][string]$Name)
    $file = Get-PidFilePath $Name
    if (-not (Test-Path $file)) { return 0 }
    $parsed = 0
    if (-not [int]::TryParse((Get-Content $file -Raw).Trim(), [ref]$parsed)) { return 0 }
    if (-not (Get-Process -Id $parsed -ErrorAction SilentlyContinue)) { return 0 }
    return $parsed
}

<#
    Kills the process and its children. `/T` is the point: `dotnet run` and `npm run dev` are
    supervisors, so stopping just the parent leaves the actual server alive and still holding the port.
#>
function Stop-ProcessTree {
    param([Parameter(Mandatory)][int]$ProcessId)
    & taskkill /PID $ProcessId /T /F *> $null
}

<# Pids listening on a port. Used as a fallback for servers this script didn't start. #>
function Get-PortOwner {
    param([Parameter(Mandatory)][int]$Port)
    try {
        return @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop |
            Select-Object -ExpandProperty OwningProcess -Unique |
            Where-Object { $_ -gt 4 })
    } catch {
        return @()
    }
}

<#
    Stops a server started by start.ps1, then anything still holding its port. The second half
    matters: a server started from an IDE, from the Claude Code preview pane, or by a previous session
    whose pid file was lost is exactly what makes the next start.ps1 fail, and it is invisible to a
    pid-file-only stop.
#>
function Stop-LocalService {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][int]$Port
    )
    $stopped = $false

    $recorded = Get-ServicePid $Name
    if ($recorded) {
        Stop-ProcessTree $recorded
        Write-Ok "$Label stopped (pid $recorded)"
        $stopped = $true
    }
    Remove-Item (Get-PidFilePath $Name) -ErrorAction SilentlyContinue

    foreach ($owner in Get-PortOwner $Port) {
        Stop-ProcessTree $owner
        Write-Ok "$Label stopped (pid $owner, found listening on $Port)"
        $stopped = $true
    }

    if (-not $stopped) { Write-Detail "$Label wasn't running" }
    return $stopped
}
