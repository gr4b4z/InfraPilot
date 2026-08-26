#Requires -Version 7
<#
.SYNOPSIS
    Shuts the local stack down: the web dev server, the API, and the Postgres container.

.DESCRIPTION
    Stops what start.ps1 started, and then anything still listening on 5173 / 5259 — a server launched
    from an IDE or the Claude Code preview pane is exactly what makes the next start.ps1 fail, and a
    pid-file-only stop would walk straight past it.

    The database is stopped, not deleted: the compose volume survives, so the next start.ps1 comes back
    with the same data. Use reseed.ps1 to throw the data away, or -RemoveData here.

.PARAMETER KeepDb
    Leave Postgres running. Useful when only the servers are being restarted.

.PARAMETER RemoveData
    Also delete the Postgres volume, so the next start.ps1 migrates and seeds from nothing. Prompts
    first unless -Force is given.

.PARAMETER Force
    Skip the confirmation prompt for -RemoveData.

.EXAMPLE
    .\scripts\stop.ps1

.EXAMPLE
    .\scripts\stop.ps1 -KeepDb
#>
[CmdletBinding()]
param(
    [switch]$KeepDb,
    [switch]$RemoveData,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

# Web first, then the API: the dev server proxies /api, and stopping the thing that answers requests
# before the thing that makes them just puts proxy errors in the log.
Write-Step 'Stopping the web dev server'
Stop-LocalService -Name 'web' -Label 'Web dev server' -Port $WebPort | Out-Null

Write-Step 'Stopping the API'
Stop-LocalService -Name 'api' -Label 'API' -Port $ApiPort | Out-Null

if ($KeepDb) {
    Write-Step 'Leaving Postgres running (-KeepDb)'
    return
}

$state = ''
try {
    Assert-Docker
    $state = Get-ContainerState $PgContainer
} catch {
    # Docker being down is a perfectly good end state for a script whose job is to shut things down.
    Write-Note $_.Exception.Message
    return
}

if (-not $state) {
    Write-Step 'Postgres container does not exist — nothing to stop'
} elseif ($state -ne 'running') {
    Write-Step "Postgres already stopped (state: $state)"
} else {
    Write-Step 'Stopping Postgres'
    & docker compose --file $ComposeFile stop $PgService
    if ($LASTEXITCODE -ne 0) { throw 'docker compose stop failed for the postgres service.' }
    Write-Ok "Postgres stopped — data kept in the postgres_data volume"
}

if ($RemoveData) {
    if (-not $Force) {
        Write-Note "This deletes the local database volume. Every local promotion, deployment and sign-off goes with it."
        $answer = Read-Host "Delete the postgres_data volume? [y/N]"
        if ($answer -notmatch '^(y|yes)$') {
            Write-Detail 'Left the volume alone.'
            return
        }
    }
    Write-Step 'Removing the Postgres volume'
    # `down --volumes` rather than `volume rm`: it removes the containers first, so the volume isn't
    # still attached, and it uses the same project name the volume was created under.
    & docker compose --file $ComposeFile down --volumes
    if ($LASTEXITCODE -ne 0) { throw 'docker compose down --volumes failed.' }
    Write-Ok 'Volume removed — the next start.ps1 will migrate and seed from scratch'
}
