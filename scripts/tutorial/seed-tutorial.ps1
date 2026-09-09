#Requires -Version 7
<#
.SYNOPSIS
    Builds the local InfraPortal tutorial environment on demand: a fresh database holding a copy of the
    live (not historical) data of a real instance, three demo accounts, and a scripted "storyline" of
    promotions, sign-offs, a failed deploy, a rollback, a release note and a webhook — one of each thing
    the presentation walks through.

.DESCRIPTION
    Steps, in order:

      1. Snapshot   — scripts/tutorial/snapshot/ is filled by export-snapshot.ps1 (needs DEPLOYMENTS_URL
                      and DEPLOYMENTS_API_KEY). An existing snapshot is reused unless -RefreshSnapshot.
      2. Database   — the local Postgres database is dropped and recreated (asks first unless -Force).
      3. API config — src/Platform.Api/appsettings.Development.json gets the tutorial API key, the demo
                      deployment seeder switched off and the ingest rate limit lifted. Merged into an
                      existing file, backed up first.
      4. API        — started detached (migrates + seeds catalog/users), then everything below goes in
                      through the public API exactly as pipelines and users would put it there.
      5. Accounts   — admin@localhost (Admin), qa@localhost (QA), user@localhost (plain User).
      6. Settings   — environments (order, colours, production flag, aliases) derived from the snapshot.
      7. Replay     — current version matrix + a short recent history, registered builds, promotion
                      policies per edge, and the open promotions. Timestamps are shifted so the newest
                      event is "half an hour ago" (disable with -NoDateShift).
      8. Storyline  — on the hero product (default mpt): QA assigned to work items, one promotion fully
                      signed off and waiting for the admin, one with an issue raised, one rejected, one
                      approved and waiting for its deploy, a failed deploy with logs, a rollback request
                      awaiting approval, a release note, a webhook subscription.
      9. Cheat sheet — written to .local/tutorial-cheatsheet.md with the ids, links and curl commands
                      the live parts of the demo need.

    Re-run any time to get back to the same starting point. docs/tutorial/presentation-guide.md is the
    matching presenter's script.

.PARAMETER Force
    Skip the "this drops the database" confirmation.

.PARAMETER RefreshSnapshot
    Re-export from the source instance even if a snapshot exists.

.PARAMETER Products
    Only load these products from the snapshot (default: all of it).

.PARAMETER HeroProduct
    The product the storyline is built on (default mpt). Falls back to the product with the most open
    promotions carrying work items when it isn't in the snapshot.

.PARAMETER HistoryDays
    Passed to export-snapshot.ps1 when exporting (default 7).

.PARAMETER NoDateShift
    Keep the snapshot's real timestamps instead of sliding them up to "now".

.PARAMETER WebhookUrl
    Where the seeded webhook subscription posts. Defaults to the address webhook-listener.ps1 listens on.

.PARAMETER Parallel
    Concurrent deploy-event importers (default 6). Events of one service always go in order.

.EXAMPLE
    .\scripts\tutorial\seed-tutorial.ps1

.EXAMPLE
    .\scripts\tutorial\seed-tutorial.ps1 -Force -Products mpt,mpt-extensions -RefreshSnapshot
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$RefreshSnapshot,
    [string[]]$Products = @(),
    [string]$HeroProduct = 'mpt',
    [int]$HistoryDays = 7,
    [switch]$NoDateShift,
    [string]$WebhookUrl = 'http://localhost:8787/infraportal',
    [int]$Parallel = 6
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot '..' '_common.ps1')

# ── Tutorial constants ───────────────────────────────────────────────────────────────────────
$SnapshotDir = Join-Path $PSScriptRoot 'snapshot'
$DevSettingsPath = Join-Path $RepoRoot 'src/Platform.Api/appsettings.Development.json'
$CheatSheetPath = Join-Path $StateDir 'tutorial-cheatsheet.md'
$ApiBase = "http://localhost:$ApiPort"
$WebBase = "http://localhost:$WebPort"

# The key pipelines (and the presenter's curl) use against the local API. Plain text is fine here — it
# only ever exists in the gitignored appsettings.Development.json of a laptop.
$TutorialApiKeyName = 'tutorial-pipeline'
$TutorialApiKey = 'tutorial-pipeline-key'

# The three accounts. Passwords are the ones SeedData.SeedLocalUsers hashes; this script only renames
# the users and narrows user@localhost to a plain user (the dev seed gives it QA so the work-items
# queue is visible there — the tutorial wants the restricted view instead).
$Accounts = @(
    [pscustomobject]@{ Key = 'admin'; Email = 'admin@localhost'; Password = 'admin123'; Name = 'Anna Admin'; Role = 'Admin' }
    [pscustomobject]@{ Key = 'qa';    Email = 'qa@localhost';    Password = 'qa123';    Name = 'Karol QA';   Role = 'QA' }
    [pscustomobject]@{ Key = 'user';  Email = 'user@localhost';  Password = 'user123';  Name = 'Ula User';   Role = 'User' }
)
$Admin = $Accounts[0]; $Qa = $Accounts[1]; $User = $Accounts[2]

# ── Small helpers ────────────────────────────────────────────────────────────────────────────

<# Property value or $null — strict mode turns a missing property into an error, and API payloads evolve. #>
function Get-Prop {
    param([Parameter(Mandatory)][AllowNull()]$Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

<#
    Everything ConvertFrom-Json hands back as a date is a [datetime] converted to local time; anything it
    left alone is an ISO string. Both become a UTC DateTimeOffset here.
#>
function ConvertTo-Instant {
    param([AllowNull()]$Value)
    if ($null -eq $Value -or $Value -eq '') { return $null }
    if ($Value -is [DateTimeOffset]) { return $Value.ToUniversalTime() }
    if ($Value -is [datetime]) { return ([DateTimeOffset]$Value).ToUniversalTime() }
    return [DateTimeOffset]::Parse([string]$Value, [cultureinfo]::InvariantCulture).ToUniversalTime()
}

# Set once the snapshot is loaded: how far every timestamp slides so the newest deploy is recent.
$script:DateShift = [timespan]::Zero

function Shift-Instant {
    param([AllowNull()]$Value)
    $instant = ConvertTo-Instant $Value
    if ($null -eq $instant) { return $null }
    return ($instant + $script:DateShift).ToString('o')
}

function Write-Json {
    param([Parameter(Mandatory)][AllowNull()]$Object)
    return (ConvertTo-Json -InputObject $Object -Depth 40 -Compress)
}

<#
    One call against the local API. Returns @{ Status; Body } and never throws on HTTP errors, so callers
    decide what a 409 or a 422 means for them.
#>
function Invoke-Local {
    param(
        [string]$Method = 'GET',
        [Parameter(Mandatory)][string]$Path,
        [AllowNull()]$Body = $null,
        [string]$Token = '',
        [string]$ApiKey = '',
        [hashtable]$Query = $null
    )
    $headers = @{}
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    if ($ApiKey) { $headers['X-Api-Key'] = $ApiKey }
    $uri = "$ApiBase$Path"
    if ($Query -and $Query.Count -gt 0) {
        $pairs = foreach ($k in $Query.Keys) { "$k=$([uri]::EscapeDataString([string]$Query[$k]))" }
        $uri += '?' + ($pairs -join '&')
    }
    $status = 0
    $params = @{
        Method = $Method; Uri = $uri; Headers = $headers
        SkipHttpErrorCheck = $true; StatusCodeVariable = 'status'; TimeoutSec = 120
    }
    if ($null -ne $Body) {
        $params.Body = Write-Json $Body
        $params.ContentType = 'application/json; charset=utf-8'
    }
    $response = Invoke-RestMethod @params
    return [pscustomobject]@{ Status = $status; Body = $response }
}

<# Like Invoke-Local, but a non-2xx is fatal — for the steps nothing further makes sense without. #>
function Invoke-LocalOrThrow {
    param([string]$Method = 'GET', [Parameter(Mandatory)][string]$Path, [AllowNull()]$Body = $null,
          [string]$Token = '', [string]$ApiKey = '', [hashtable]$Query = $null, [string]$What = '')
    $r = Invoke-Local -Method $Method -Path $Path -Body $Body -Token $Token -ApiKey $ApiKey -Query $Query
    if ($r.Status -lt 200 -or $r.Status -ge 300) {
        $label = if ($What) { $What } else { "$Method $Path" }
        throw "$label failed with HTTP $($r.Status): $(Write-Json $r.Body)"
    }
    return $r.Body
}

function Get-Token {
    param([Parameter(Mandatory)]$Account)
    $login = Invoke-LocalOrThrow -Method POST -Path '/api/auth/login' `
        -Body @{ email = $Account.Email; password = $Account.Password } -What "login as $($Account.Email)"
    return $login.token
}

function Test-ProdLike {
    param([Parameter(Mandatory)][string]$Environment)
    return $Environment -in @('prod', 'production', 'prd', 'live')
}

<# Pre-production rings a QA signs off into: staging and the release-candidate style environments. #>
function Test-StagingLike {
    param([Parameter(Mandatory)][string]$Environment)
    return $Environment -in @('staging', 'uat', 'rc', 'stable', 'preprod', 'pre-prod')
}

# ── 1. Snapshot ──────────────────────────────────────────────────────────────────────────────
Write-Step 'Snapshot'
$manifestPath = Join-Path $SnapshotDir 'manifest.json'
if ($RefreshSnapshot -or -not (Test-Path $manifestPath)) {
    if (-not $env:DEPLOYMENTS_URL -or -not $env:DEPLOYMENTS_API_KEY) {
        throw "No snapshot in $SnapshotDir and DEPLOYMENTS_URL / DEPLOYMENTS_API_KEY are not set. Set them (the InfraPortal instance to copy and its API key) or drop a snapshot directory in place."
    }
    $exportArgs = @{ HistoryDays = $HistoryDays }
    if ($Products.Count -gt 0) { $exportArgs.Products = $Products }
    & (Join-Path $PSScriptRoot 'export-snapshot.ps1') @exportArgs
}
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
Write-Detail "exported $($manifest.exportedAt) from $($manifest.source)"

$snapshotProducts = @((Get-Content (Join-Path $SnapshotDir 'products.json') -Raw | ConvertFrom-Json) | ForEach-Object { $_.product })
$selectedProducts = if ($Products.Count -gt 0) {
    @($snapshotProducts | Where-Object { $Products -contains $_ })
} else { $snapshotProducts }
if ($selectedProducts.Count -eq 0) { throw "None of $($Products -join ', ') is in the snapshot ($($snapshotProducts -join ', '))." }

# Current cells first: they define what "now" looks like per (service, environment). Recent history is
# what the timeline and analytics pages draw from.
$stateCells = [System.Collections.Generic.List[object]]::new()
$recentEvents = [System.Collections.Generic.List[object]]::new()
foreach ($product in $selectedProducts) {
    $statePath = Join-Path $SnapshotDir 'state' "$product.json"
    if (Test-Path $statePath) {
        foreach ($cell in @(Get-Content $statePath -Raw | ConvertFrom-Json)) { $stateCells.Add($cell) }
    }
    $recentPath = Join-Path $SnapshotDir 'recent' "$product.json"
    if (Test-Path $recentPath) {
        foreach ($ev in @(Get-Content $recentPath -Raw | ConvertFrom-Json)) { $recentEvents.Add($ev) }
    }
}
$pendingPromotions = @((Get-Content (Join-Path $SnapshotDir 'promotions-pending.json') -Raw | ConvertFrom-Json) |
    Where-Object { $selectedProducts -contains $_.product })
$builds = @((Get-Content (Join-Path $SnapshotDir 'builds.json') -Raw | ConvertFrom-Json) |
    Where-Object { $selectedProducts -contains $_.product })
Write-Ok "$($selectedProducts.Count) products, $($stateCells.Count) current cells, $($recentEvents.Count) recent deploys, $($pendingPromotions.Count) open promotions, $($builds.Count) builds"

# Slide time so the snapshot reads as "this morning" however old it is. Half an hour of headroom keeps
# the storyline events written below (which use real "now") newer than anything replayed.
if (-not $NoDateShift) {
    $newest = $null
    foreach ($ev in $stateCells) { $t = ConvertTo-Instant $ev.deployedAt; if ($null -eq $newest -or $t -gt $newest) { $newest = $t } }
    foreach ($ev in $recentEvents) { $t = ConvertTo-Instant $ev.deployedAt; if ($null -eq $newest -or $t -gt $newest) { $newest = $t } }
    if ($null -ne $newest) {
        $script:DateShift = [DateTimeOffset]::UtcNow.AddMinutes(-30) - $newest
        Write-Detail ("timestamps shifted by {0:d\d\ h\h\ m\m} (newest deploy → 30 minutes ago)" -f $script:DateShift)
    }
}

# Lookups used by the replay and the storyline.
$cellByKey = @{}
foreach ($cell in $stateCells) { $cellByKey["$($cell.product)|$($cell.service)|$($cell.environment)"] = $cell }

# Hero product: the storyline needs open promotions that carry work items.
$withWorkItems = @($pendingPromotions | Where-Object {
    @((Get-Prop $_ 'sourceEventReferences') | Where-Object { $_ -and $_.type -eq 'work-item' }).Count -gt 0 })
if ($selectedProducts -notcontains $HeroProduct -or -not ($withWorkItems | Where-Object { $_.product -eq $HeroProduct })) {
    $best = $withWorkItems | Group-Object product | Sort-Object Count -Descending | Select-Object -First 1
    if ($best) {
        Write-Note "'$HeroProduct' has no open promotions with work items in this snapshot — storyline moves to '$($best.Name)'"
        $HeroProduct = $best.Name
    } else {
        Write-Note "No open promotion carries work items — the promotion storyline will be limited to what exists"
        if ($selectedProducts -notcontains $HeroProduct) { $HeroProduct = $selectedProducts[0] }
    }
}
Write-Detail "hero product: $HeroProduct"

# ── 2. Database ──────────────────────────────────────────────────────────────────────────────
if (-not $Force) {
    Write-Host ''
    Write-Note "This drops the '$DbName' database on localhost:$DbPort and rebuilds it from the snapshot."
    Write-Note 'Every local promotion, sign-off, comment and service request is deleted.'
    $answer = Read-Host 'Build the tutorial database? [y/N]'
    if ($answer -notmatch '^(y|yes)$') { Write-Detail 'Nothing changed.'; return }
}

if ((Get-ContainerState $PgContainer) -ne 'running') { Start-Database } else { Assert-Docker; Write-Step "Postgres already running on localhost:$DbPort" }

Write-Step 'Stopping the API'
Stop-LocalService -Name 'api' -Label 'API' -Port $ApiPort | Out-Null

Write-Step "Recreating the '$DbName' database"
Invoke-Psql -Database 'postgres' -Sql "DROP DATABASE IF EXISTS $DbName WITH (FORCE);" | Out-Null
Invoke-Psql -Database 'postgres' -Sql "CREATE DATABASE $DbName OWNER $DbUser;" | Out-Null
Write-Ok 'Database recreated'

# ── 3. API configuration ─────────────────────────────────────────────────────────────────────
# appsettings.Development.json is gitignored and only read when ASPNETCORE_ENVIRONMENT=Development —
# which is what the launch profile (and so start.ps1, reseed.ps1 and .claude/launch.json) sets. The
# tutorial needs three things from it, and they have to survive API restarts: the API key the demo's
# curl commands use, the demo deployment seeder off (the snapshot replaces it), and the ingest rate
# limit lifted (a thousand replayed events would otherwise take ten minutes at 120/min).
Write-Step 'Writing src/Platform.Api/appsettings.Development.json'
$settings = [ordered]@{}
if (Test-Path $DevSettingsPath) {
    Copy-Item $DevSettingsPath "$DevSettingsPath.bak" -Force
    $settings = Get-Content $DevSettingsPath -Raw | ConvertFrom-Json -AsHashtable
    Write-Detail "existing file kept (backup: appsettings.Development.json.bak)"
}
if (-not $settings.Contains('Seed')) { $settings['Seed'] = @{} }
$settings['Seed']['DemoDeployments'] = $false
if (-not $settings.Contains('Deployments')) { $settings['Deployments'] = @{} }
$deployments = $settings['Deployments']
$keys = @()
if ($deployments.Contains('ApiKeys') -and $deployments['ApiKeys']) { $keys = @($deployments['ApiKeys'] | Where-Object { $_['Name'] -ne $TutorialApiKeyName }) }
$keys += @{ Name = $TutorialApiKeyName; Key = $TutorialApiKey; Roles = @('InfraPortal.User') }
$deployments['ApiKeys'] = $keys
if (-not $deployments.Contains('IngestionRateLimit')) { $deployments['IngestionRateLimit'] = @{} }
$deployments['IngestionRateLimit']['AuthenticatedPermitLimit'] = 100000
$settings | ConvertTo-Json -Depth 20 | Set-Content -Path $DevSettingsPath -Encoding utf8
Write-Ok "API key '$TutorialApiKeyName' configured, demo deployment seed off, ingest rate limit lifted"

# ── 4. API ───────────────────────────────────────────────────────────────────────────────────
Write-Step 'Starting the API (migrates, seeds catalog and users)'
New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
$apiOut = Join-Path $StateDir 'api.log'
$apiErr = Join-Path $StateDir 'api.err.log'
$api = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', $ApiProject, '--', '--ConnectionStrings:Platform', $ConnectionString) `
    -WorkingDirectory $RepoRoot `
    -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr `
    -WindowStyle Hidden -PassThru
Save-ServicePid -Name 'api' -ProcessId $api.Id
if (-not (Wait-Until -TimeoutSec 300 -Condition { Test-ApiHealthy } -Message 'Waiting for the API…')) {
    if (Test-Path $apiErr) { Get-Content $apiErr -Tail 20 | ForEach-Object { Write-Detail $_ } }
    throw "API not healthy. Full log: $apiOut"
}
Write-Ok "API healthy on $ApiBase"

# ── 5. Accounts ──────────────────────────────────────────────────────────────────────────────
Write-Step 'Accounts'
Invoke-Psql -Sql @"
update local_users set "Name" = '$($Admin.Name)' where "Email" = '$($Admin.Email)';
update local_users set "Name" = '$($Qa.Name)' where "Email" = '$($Qa.Email)';
update local_users set "Name" = '$($User.Name)', "Roles" = '["InfraPortal.User"]'::jsonb where "Email" = '$($User.Email)';
"@ | Out-Null
foreach ($a in $Accounts) { Write-Detail ("{0,-6} {1,-18} {2,-10} {3}" -f $a.Role, $a.Email, $a.Password, $a.Name) }
$adminToken = Get-Token $Admin
$qaToken = Get-Token $Qa
Get-Token $User | Out-Null   # the plain user only has to be able to sign in
Write-Ok 'All three can sign in'

# ── 6. Environments ──────────────────────────────────────────────────────────────────────────
# Derived from what the snapshot actually deploys to, ordered as a pipeline reads (dev first, prod
# last), with the production flag analytics and release notes key off and the aliases producers use.
Write-Step 'Environment settings'
$envRank = @{ dev = 0; development = 0; test = 1; testing = 1; qa = 1; stable = 2; rc = 2; staging = 3; uat = 3; sandbox = 4; demo = 4; prod = 6; production = 6; live = 6 }
$envPalette = @{ dev = '#2563eb'; test = '#0891b2'; stable = '#7c3aed'; rc = '#7c3aed'; staging = '#d97706'; sandbox = '#65a30d'; demo = '#0d9488'; prod = '#dc2626'; production = '#dc2626' }
$envAliases = @{ prod = @('production', 'prd', 'live'); dev = @('development', 'develop'); test = @('testing'); staging = @('stage', 'stg') }
$environmentKeys = @($stateCells | ForEach-Object { $_.environment } | Sort-Object -Unique |
    Sort-Object { if ($envRank.ContainsKey($_)) { $envRank[$_] } else { 5 } }, { $_ })
$environments = foreach ($key in $environmentKeys) {
    [ordered]@{
        key          = $key
        displayName  = if ($key -in @('rc', 'qa', 'uat')) { $key.ToUpperInvariant() } else { (Get-Culture).TextInfo.ToTitleCase($key) }
        color        = if ($envPalette.ContainsKey($key)) { $envPalette[$key] } else { $null }
        isProduction = ($key -in @('prod', 'production', 'live'))
        # @() around the whole expression: a one-element result would otherwise unroll to a scalar.
        aliases      = @(if ($envAliases.ContainsKey($key)) { $envAliases[$key] } else { @() })
    }
}
$current = Invoke-LocalOrThrow -Path '/api/settings' -Token $adminToken -What 'read settings'
Invoke-LocalOrThrow -Method PUT -Path '/api/settings' -Token $adminToken -What 'save settings' -Body @{
    environments     = @($environments)
    roles            = @($current.roles)
    activityTemplate = @($current.activityTemplate)
} | Out-Null
Write-Ok "environments: $($environmentKeys -join ' → ')"

# ── 7a. Deploy events ────────────────────────────────────────────────────────────────────────
Write-Step 'Replaying deploy events'

function ConvertTo-ParticipantBody {
    param([AllowNull()]$P)
    if ($null -eq $P) { return $null }
    return [ordered]@{ role = $P.role; displayName = Get-Prop $P 'displayName'; email = Get-Prop $P 'email' }
}

function ConvertTo-ReferenceBody {
    param([Parameter(Mandatory)]$R)
    $body = [ordered]@{
        type       = $R.type
        url        = Get-Prop $R 'url'
        provider   = Get-Prop $R 'provider'
        key        = Get-Prop $R 'key'
        revision   = Get-Prop $R 'revision'
        title      = Get-Prop $R 'title'
        subTitle   = Get-Prop $R 'subTitle'
        content    = Get-Prop $R 'content'
        occurredAt = Shift-Instant (Get-Prop $R 'occurredAt')
    }
    # Assigned separately, from a variable: any other spelling (Get-Prop's output, an if-expression)
    # lets PowerShell unroll a one-hash list into a bare string, which the API rejects as a list.
    $commits = @(Get-Prop $R 'commits' | Where-Object { $null -ne $_ })
    if ($commits.Count -gt 0) { $body.commits = $commits }
    $participants = Get-Prop $R 'participants'
    if ($participants) { $body.participants = @($participants | ForEach-Object { ConvertTo-ParticipantBody $_ }) }
    $resolution = Get-Prop $R 'resolution'
    if ($resolution) {
        $by = Get-Prop $resolution 'by'
        $body.resolution = [ordered]@{
            resolved = [bool](Get-Prop $resolution 'resolved')
            status   = Get-Prop $resolution 'status'
            at       = Shift-Instant (Get-Prop $resolution 'at')
            by       = if ($by) { @{ displayName = Get-Prop $by 'displayName'; email = Get-Prop $by 'email' } } else { $null }
        }
    }
    return $body
}

<# A snapshot deploy event (state cell or recent row) → the ingest payload. #>
function ConvertTo-IngestBody {
    param([Parameter(Mandatory)]$E)
    $run = Get-Prop $E 'run'
    $body = [ordered]@{
        product         = $E.product
        service         = $E.service
        environment     = $E.environment
        version         = $E.version
        source          = if ($E.source) { $E.source } else { 'snapshot' }
        deployedAt      = Shift-Instant $E.deployedAt
        status          = if ($E.status) { $E.status } else { 'succeeded' }
        isRollback      = [bool](Get-Prop $E 'isRollback')
        previousVersion = Get-Prop $E 'previousVersion'
        references      = @(@(Get-Prop $E 'references') | Where-Object { $_ } | ForEach-Object { ConvertTo-ReferenceBody $_ })
        participants    = @(@(Get-Prop $E 'participants') | Where-Object { $_ } | ForEach-Object { ConvertTo-ParticipantBody $_ })
    }
    if ($run) {
        $body.run = [ordered]@{
            provider = Get-Prop $run 'provider'; runId = Get-Prop $run 'runId'; runNumber = Get-Prop $run 'runNumber'
            attempt = Get-Prop $run 'attempt'; workflowName = Get-Prop $run 'workflowName'; jobName = Get-Prop $run 'jobName'
            runUrl = Get-Prop $run 'runUrl'; jobUrl = Get-Prop $run 'jobUrl'; triggeredBy = Get-Prop $run 'triggeredBy'
            startedAt = Shift-Instant (Get-Prop $run 'startedAt'); completedAt = Shift-Instant (Get-Prop $run 'completedAt')
            failureReason = Get-Prop $run 'failureReason'
        }
    }
    return $body
}

# Dedupe on the ingest natural key (a state cell is usually also in the recent window), then group by
# service so each service's events go in oldest-first while services run side by side.
$seen = [System.Collections.Generic.HashSet[string]]::new()
$byService = @{}
foreach ($ev in @($recentEvents) + @($stateCells)) {
    $key = "$($ev.product)|$($ev.service)|$($ev.environment)|$($ev.version)|$($ev.source)|$((ConvertTo-Instant $ev.deployedAt).ToString('o'))"
    if (-not $seen.Add($key)) { continue }
    $group = "$($ev.product)|$($ev.service)"
    if (-not $byService.ContainsKey($group)) { $byService[$group] = [System.Collections.Generic.List[object]]::new() }
    $byService[$group].Add($ev)
}
$eventTotal = $seen.Count
$eventsUrl = "$ApiBase/api/deployments/events"
$imported = 0; $replayed = 0; $failed = 0
$failures = [System.Collections.Generic.List[string]]::new()
$groupBatches = @($byService.Keys | Sort-Object)
$batchSize = [Math]::Max($Parallel * 4, 8)
for ($i = 0; $i -lt $groupBatches.Count; $i += $batchSize) {
    $batch = foreach ($group in $groupBatches[$i..([Math]::Min($i + $batchSize, $groupBatches.Count) - 1)]) {
        $ordered = $byService[$group] | Sort-Object { ConvertTo-Instant $_.deployedAt }
        [pscustomobject]@{ Group = $group; Bodies = @($ordered | ForEach-Object { Write-Json (ConvertTo-IngestBody $_) }) }
    }
    $results = $batch | ForEach-Object -ThrottleLimit $Parallel -Parallel {
        $headers = @{ 'X-Api-Key' = $using:TutorialApiKey }
        $group = $_.Group
        foreach ($json in $_.Bodies) {
            $status = 0
            try {
                $r = Invoke-RestMethod -Method Post -Uri $using:eventsUrl -Headers $headers -ContentType 'application/json; charset=utf-8' `
                    -Body $json -SkipHttpErrorCheck -StatusCodeVariable status -TimeoutSec 120
                [pscustomobject]@{ Status = $status; Error = if ($status -ge 400) { "${group}: HTTP $status $($r | ConvertTo-Json -Compress -Depth 5)" } else { $null } }
            } catch {
                [pscustomobject]@{ Status = 0; Error = "${group}: $($_.Exception.Message)" }
            }
        }
    }
    foreach ($r in $results) {
        if ($r.Status -eq 201) { $imported++ }
        elseif ($r.Status -eq 200) { $replayed++ }
        else { $failed++; if ($failures.Count -lt 10) { $failures.Add($r.Error) } }
    }
    Write-Detail ("{0,5}/{1} events sent" -f ($imported + $replayed + $failed), $eventTotal)
}
Write-Ok "$imported deploy events imported ($replayed duplicates, $failed failed)"
foreach ($f in $failures) { Write-Note $f }

# ── 7b. Builds ───────────────────────────────────────────────────────────────────────────────
Write-Step 'Registering builds'
$buildCount = 0; $buildFailed = 0
foreach ($b in ($builds | Sort-Object { ConvertTo-Instant $_.createdAt })) {
    $r = Invoke-Local -Method POST -Path '/api/builds' -ApiKey $TutorialApiKey -Body @{
        product = $b.product; service = $b.service; version = $b.version; branch = $b.branch
        commitSha = Get-Prop $b 'commitSha'; buildId = Get-Prop $b 'buildId'; buildUrl = Get-Prop $b 'buildUrl'
        artifactRef = Get-Prop $b 'artifactRef'; artifactDigest = Get-Prop $b 'artifactDigest'
    }
    if ($r.Status -in 200, 201) { $buildCount++ } else { $buildFailed++; if ($buildFailed -le 3) { Write-Note "build $($b.service) $($b.version): HTTP $($r.Status) $(Write-Json $r.Body)" } }
}
Write-Ok "$buildCount builds registered ($buildFailed failed)"

# ── 7c. Promotion policies ───────────────────────────────────────────────────────────────────
# One policy per edge the open promotions use, plus a full ladder for the hero product so a build can
# be promoted from the registry. Production-like targets get the human gate (release manager = the
# admin account, every work item signed off by a QA); staging gets a QA approval; dev/test are automatic
# and track no work items.
Write-Step 'Promotion policies'
function New-PolicyBody {
    param([string]$Product, [string]$SourceEnv, [string]$TargetEnv)
    if (Test-ProdLike $TargetEnv) {
        return [ordered]@{
            product = $Product; service = $null; sourceEnv = $SourceEnv; targetEnv = $TargetEnv
            steps = @(@{ name = 'Release approval'; requirements = @(@{ name = 'Release manager'; groups = @(); users = @($Admin.Email); minApprovers = 1 }) })
            tracksWorkItems = $true
            requiredWorkItemRoles = @('qa')
            requireAllWorkItemsApproved = $true
            autoApproveWhenNoWorkItems = $false
            # marketplace's pipeline stops at an Azure DevOps environment check after the InfraPortal
            # gate; everything else deploys the moment the gate opens. Shows both wordings of the notice.
            deploysOnApproval = ($Product -ne 'marketplace')
        }
    }
    if (Test-StagingLike $TargetEnv) {
        return [ordered]@{
            product = $Product; service = $null; sourceEnv = $SourceEnv; targetEnv = $TargetEnv
            steps = @(@{ name = 'QA approval'; requirements = @(@{ name = 'QA lead'; groups = @(); users = @($Qa.Email); minApprovers = 1 }) })
            tracksWorkItems = $true
            requireAllWorkItemsApproved = $false
            deploysOnApproval = $true
        }
    }
    return [ordered]@{
        product = $Product; service = $null; sourceEnv = $SourceEnv; targetEnv = $TargetEnv
        steps = @(); tracksWorkItems = $false; autoApproveWhenNoWorkItems = $true; deploysOnApproval = $true
    }
}
$edges = [ordered]@{}
foreach ($c in $pendingPromotions) { $edges["$($c.product)|$($c.sourceEnv)|$($c.targetEnv)"] = $true }
# Every product that has both rings gets a staging → prod gate, so Settings → Promotions shows more than
# the hero product and the marketplace edge exists to carry its different "after approval" wording.
foreach ($product in $selectedProducts) {
    $envs = @($stateCells | Where-Object { $_.product -eq $product } | ForEach-Object { $_.environment } | Sort-Object -Unique)
    if ($envs -contains 'staging' -and $envs -contains 'prod') { $edges["$product|staging|prod"] = $true }
}
$heroEnvSet = [System.Collections.Generic.HashSet[string]]::new([string[]]@($stateCells | Where-Object { $_.product -eq $HeroProduct } | ForEach-Object { $_.environment }))
$heroEnvs = @($environmentKeys | Where-Object { $heroEnvSet.Contains($_) })
for ($i = 1; $i -lt $heroEnvs.Count; $i++) { $edges["$HeroProduct|$($heroEnvs[$i-1])|$($heroEnvs[$i])"] = $true }
# "build" is the registry's pseudo-source: a policy from it is what lets Artifacts → Promote target an env.
foreach ($target in @($heroEnvs | Where-Object { $_ -notin @('prod', 'production', 'live') } | Select-Object -First 2)) { $edges["$HeroProduct|build|$target"] = $true }
$policyCount = 0
foreach ($edge in $edges.Keys) {
    $product, $source, $target = $edge.Split('|')
    $r = Invoke-Local -Method POST -Path '/api/promotions/admin/policies' -Token $adminToken -Body (New-PolicyBody $product $source $target)
    if ($r.Status -in 200, 201) { $policyCount++ } else { Write-Note "policy ${edge}: HTTP $($r.Status) $(Write-Json $r.Body)" }
}
Write-Ok "$policyCount policies ($($edges.Keys -join ', '))"

# ── 7d. Open promotions ──────────────────────────────────────────────────────────────────────
# Posted the way mpt-release posts them. A promotion needs a succeeded deploy of its version in the
# source environment; when the source has moved on since the snapshot's candidate was opened, that
# deploy is synthesised just behind the source's current one so it never displaces it.
Write-Step 'Open promotions'
$promotionIds = @{}
$promoCreated = 0; $promoSkipped = 0
foreach ($c in ($pendingPromotions | Sort-Object { ConvertTo-Instant $_.createdAt })) {
    $references = @(@(Get-Prop $c 'sourceEventReferences') | Where-Object { $_ } | ForEach-Object { ConvertTo-ReferenceBody $_ })
    $participants = @(@(Get-Prop $c 'participants') | Where-Object { $_ } | ForEach-Object { ConvertTo-ParticipantBody $_ })
    $body = [ordered]@{
        product = $c.product; service = $c.service; sourceEnv = $c.sourceEnv; targetEnv = $c.targetEnv; version = $c.version
        fromRevision = Get-Prop $c 'fromRevision'; toRevision = Get-Prop $c 'toRevision'
        references = $references; participants = $participants
    }
    $r = Invoke-Local -Method POST -Path '/api/promotions' -ApiKey $TutorialApiKey -Body $body
    if ($r.Status -eq 422 -and (Get-Prop $r.Body 'code') -eq 'source_deploy_missing') {
        $sourceCell = $cellByKey["$($c.product)|$($c.service)|$($c.sourceEnv)"]
        $created = ConvertTo-Instant $c.createdAt
        $at = ($created + $script:DateShift).AddMinutes(-15)
        if ($sourceCell) {
            $cellAt = (ConvertTo-Instant $sourceCell.deployedAt) + $script:DateShift
            if ($at -ge $cellAt) { $at = $cellAt.AddMinutes(-1) }
        }
        Invoke-Local -Method POST -Path '/api/deployments/events' -ApiKey $TutorialApiKey -Body @{
            product = $c.product; service = $c.service; environment = $c.sourceEnv; version = $c.version
            source = 'helm-deploy'; deployedAt = $at.ToString('o'); status = 'succeeded'
            references = $references; participants = $participants
        } | Out-Null
        $r = Invoke-Local -Method POST -Path '/api/promotions' -ApiKey $TutorialApiKey -Body $body
    }
    if ($r.Status -eq 201) {
        $promoCreated++
        $promotionIds["$($c.product)|$($c.service)|$($c.sourceEnv)|$($c.targetEnv)|$($c.version)"] = $r.Body.id
    } else {
        $promoSkipped++
        Write-Note "promotion $($c.product)/$($c.service) $($c.version) $($c.sourceEnv)→$($c.targetEnv): HTTP $($r.Status) $(Write-Json $r.Body)"
    }
}
Write-Ok "$promoCreated promotions opened ($promoSkipped skipped)"

# ── 8. Storyline ─────────────────────────────────────────────────────────────────────────────
Write-Step "Storyline on '$HeroProduct'"
$story = [ordered]@{}

# The open promotions as the local API now sees them, hero product only, newest first, work items first.
$localPending = @((Invoke-LocalOrThrow -Path '/api/promotions' -Token $adminToken -Query @{ status = 'Pending'; product = $HeroProduct }).candidates)
function Get-WorkItemRefs { param($Candidate) return @(@(Get-Prop $Candidate 'sourceEventReferences') | Where-Object { $_ -and $_.type -eq 'work-item' -and $_.key }) }
# Production-targeted candidates first: their gate is the admin's to open (scenes B, D and E need an admin
# decision), whereas a staging edge is signed off by the QA lead and the admin has no standing there.
$heroCandidates = @($localPending | Where-Object { @(Get-WorkItemRefs $_).Count -gt 0 } |
    Sort-Object @{ Expression = { if (Test-ProdLike $_.targetEnv) { 0 } else { 1 } } }, @{ Expression = { ConvertTo-Instant $_.createdAt }; Descending = $true })
Write-Detail "$($localPending.Count) open promotions on $HeroProduct, $($heroCandidates.Count) with work items"

function Assign-QaToWorkItems {
    param([Parameter(Mandatory)]$Candidate)
    foreach ($ref in Get-WorkItemRefs $Candidate) {
        $r = Invoke-Local -Method PATCH -Path "/api/promotions/$($Candidate.id)/references/$([uri]::EscapeDataString($ref.key))/participants" `
            -Token $adminToken -Body @{ role = 'qa'; assignee = @{ email = $Qa.Email; displayName = $Qa.Name } }
        if ($r.Status -ge 300) { Write-Note "assign qa on $($ref.key): HTTP $($r.Status) $(Write-Json $r.Body)" }
    }
}

function Decide-WorkItem {
    param([Parameter(Mandatory)]$Candidate, [Parameter(Mandatory)]$Ref, [ValidateSet('approvals', 'issues', 'blocks')][string]$Decision, [string]$Comment)
    $r = Invoke-Local -Method POST -Path "/api/work-items/$([uri]::EscapeDataString($Ref.key))/$Decision" -Token $qaToken `
        -Body @{ product = $Candidate.product; service = $Candidate.service; targetEnv = $Candidate.targetEnv; comment = $Comment }
    if ($r.Status -ge 300) { Write-Note "$Decision on $($Ref.key): HTTP $($r.Status) $(Write-Json $r.Body)" }
}

function Describe-Candidate { param($C) return "$($C.service) $($C.version) ($($C.sourceEnv) → $($C.targetEnv))" }

# Roles in the story, in the order the presentation needs them; each takes the next candidate.
$queue = [System.Collections.Generic.Queue[object]]::new([object[]]$heroCandidates)
function Next-Candidate { if ($queue.Count -gt 0) { return $queue.Dequeue() } return $null }

# A — untouched: QA signs it off live during the demo.
$a = Next-Candidate
if ($a) {
    Assign-QaToWorkItems $a
    $story['A. Awaiting QA sign-off (demo: QA signs off live)'] = $a
}

# B — every work item signed off; the release manager's approval is what's missing. Admin approves live.
$b = Next-Candidate
if ($b) {
    Assign-QaToWorkItems $b
    foreach ($ref in Get-WorkItemRefs $b) { Decide-WorkItem $b $ref 'approvals' 'Tested on staging, behaves as described in the ticket.' }
    $story['B. Signed off by QA, awaiting release approval (demo: Admin approves live)'] = $b
}

# C — QA found a problem on one ticket: the gate is stalled, the thread says why.
$c = Next-Candidate
if ($c) {
    Assign-QaToWorkItems $c
    $refs = @(Get-WorkItemRefs $c)
    if ($refs.Count -gt 1) { Decide-WorkItem $c $refs[0] 'approvals' 'Acceptance criteria met.' }
    Decide-WorkItem $c $refs[-1] 'issues' 'Repro still happens on the second attempt — the fix does not cover the empty-list case. Needs another look before this goes out.'
    Invoke-Local -Method POST -Path "/api/promotions/$($c.id)/comments" -Token $qaToken -Body @{ body = "Holding this one: $($refs[-1].key) fails re-test on staging. Details on the work item." } | Out-Null
    $story['C. Issue raised on a work item (gate stalled)'] = $c
}

# D — rejected by the release manager, with the reason on the thread and in the audit feed.
$d = Next-Candidate
if ($d) {
    Assign-QaToWorkItems $d
    $r = Invoke-Local -Method POST -Path "/api/promotions/$($d.id)/reject" -Token $adminToken -Body @{ comment = 'Rejected: the release window closed and this needs the follow-up fix from the next build. Re-promote from the next version.' }
    if ($r.Status -ge 300) { Write-Note "reject $($d.id): HTTP $($r.Status) $(Write-Json $r.Body)" }
    $story['D. Rejected by the release manager'] = $d
}

# E — fully approved, waiting for the pipeline to deploy it. The cheat sheet has the curl that lands
# it and closes the promotion as Deployed on stage.
$e = Next-Candidate
if ($e) {
    Assign-QaToWorkItems $e
    foreach ($ref in Get-WorkItemRefs $e) { Decide-WorkItem $e $ref 'approvals' 'Regression suite green, no change to the public contract.' }
    $r = Invoke-Local -Method POST -Path "/api/promotions/$($e.id)/approve" -Token $adminToken -Body @{ comment = 'Approved — change window open, rollback plan documented.' }
    if ($r.Status -ge 300) { Write-Note "approve $($e.id): HTTP $($r.Status) $(Write-Json $r.Body)" }
    $story['E. Approved, awaiting deploy (demo: curl the deploy event, watch it close)'] = $e
}
# Everything else that carries work items gets the QA assigned too, so the queue has depth.
while ($queue.Count -gt 0) { Assign-QaToWorkItems (Next-Candidate) }
Write-Ok "$($story.Count) promotion scenes staged"

# Picks the hero service the rest of the storyline hangs off: the approved promotion's service if there is
# one, else any hero service that runs in a production-like environment.
$prodEnv = @($heroEnvs | Where-Object { $_ -in @('prod', 'production', 'live') } | Select-Object -First 1)
$prodEnv = if ($prodEnv) { $prodEnv[0] } else { $heroEnvs[-1] }
$heroService = if ($e) { $e.service } elseif ($a) { $a.service } else { ($stateCells | Where-Object { $_.product -eq $HeroProduct -and $_.environment -eq $prodEnv } | Select-Object -First 1).service }

# Failed deploy — a red cell on the matrix with the pipeline's own output attached.
$failEnv = @(@('test', 'staging', 'dev') | Where-Object { $heroEnvs -contains $_ -and $cellByKey.ContainsKey("$HeroProduct|$heroService|$_") } | Select-Object -First 1)
if ($failEnv) {
    $failEnv = $failEnv[0]
    $currentCell = $cellByKey["$HeroProduct|$heroService|$failEnv"]
    $failedVersion = if ($currentCell.version -match '^(\d+)\.(\d+)\.(\d+)') { "$($Matches[1]).$($Matches[2]).$([int]$Matches[3] + 1)-g0badc0de" } else { "$($currentCell.version)-next" }
    $failedAt = [DateTimeOffset]::UtcNow.AddMinutes(-20)
    $r = Invoke-Local -Method POST -Path '/api/deployments/events' -ApiKey $TutorialApiKey -Body @{
        product = $HeroProduct; service = $heroService; environment = $failEnv; version = $failedVersion
        source = 'helm-deploy'; deployedAt = $failedAt.ToString('o'); status = 'failed'; previousVersion = $currentCell.version
        participants = @(@{ role = 'triggered-by'; displayName = 'mpt-release-bot[bot]'; email = 'mpt-release-bot[bot]@users.noreply.github.com' })
        references = @(
            @{ type = 'work-item'; provider = 'jira'; key = 'TUT-101'; title = 'Add tenant column to invoice export'; url = 'https://example.atlassian.net/browse/TUT-101'; commits = @('0badc0de0badc0de0badc0de0badc0de0badc0de') }
            @{ type = 'commit'; provider = 'github'; key = '0badc0de0badc0de0badc0de0badc0de0badc0de'; revision = '0badc0de0badc0de0badc0de0badc0de0badc0de'; title = 'feat(export): add tenant column (TUT-101)'; occurredAt = $failedAt.AddHours(-2).ToString('o'); participants = @(@{ role = 'author'; displayName = $User.Name; email = $User.Email }) }
        )
        run = @{ provider = 'github-actions'; runNumber = '1042'; attempt = 1; workflowName = "Reconcile $failEnv"; jobName = "Deploy Helm ($heroService, $failedVersion)"; runUrl = 'https://github.com/example/mpt-release/actions/runs/1042'; triggeredBy = 'mpt-release-bot[bot]'; startedAt = $failedAt.AddMinutes(-4).ToString('o'); completedAt = $failedAt.ToString('o'); failureReason = 'Helm upgrade failed: pre-upgrade hook "db-migrate" exceeded its 3m deadline' }
        logs = @(@{ name = 'helm upgrade'; source = 'helm'; content = @"
Release "$heroService" does not exist. Installing it now.
NAME: $heroService
LAST DEPLOYED: $($failedAt.AddMinutes(-4).ToString('ddd MMM d HH:mm:ss yyyy'))
NAMESPACE: $failEnv
STATUS: pending-upgrade
Hook db-migrate: waiting for job $heroService-db-migrate to complete...
Job $heroService-db-migrate: pod $heroService-db-migrate-7f9c4 -> Running
  alembic: Running upgrade 4c1e -> 5d2f, add tenant column to invoice_export
  psycopg2.errors.LockNotAvailable: canceling statement due to lock timeout
  LINE 1: ALTER TABLE invoice_export ADD COLUMN tenant_id uuid
Job $heroService-db-migrate: pod $heroService-db-migrate-7f9c4 -> Error (exit code 1)
Error: UPGRADE FAILED: pre-upgrade hooks failed: 1 error occurred:
        * timed out waiting for the condition
"@ })
    }
    if ($r.Status -eq 201) { $story['F. Failed deploy with pipeline logs'] = [pscustomobject]@{ id = $r.Body.id; service = $heroService; version = $failedVersion; environment = $failEnv } }
    else { Write-Note "failed-deploy scene: HTTP $($r.Status) $(Write-Json $r.Body)" }
}

# Rollback — a policy (QA may raise, the release manager approves), a prior version to go back to, and an
# open request the admin decides on stage.
$rollbackTarget = $null
$rollbackCandidates = @($stateCells | Where-Object { $_.product -eq $HeroProduct -and $_.environment -eq $prodEnv -and (Get-Prop $_ 'previousVersion') -and $_.previousVersion -ne $_.version })
$preferred = @($rollbackCandidates | Where-Object { $_.service -eq $heroService })
$rollbackCell = if ($preferred) { $preferred[0] } elseif ($rollbackCandidates) { $rollbackCandidates[0] } else { $null }
if ($rollbackCell) {
    $r = Invoke-Local -Method POST -Path '/api/rollbacks/admin/policies' -Token $adminToken -Body @{
        product = $HeroProduct; targetEnv = $null
        creators = @{ groups = @(); users = @($Qa.Email, $Admin.Email) }
        steps = @(@{ name = 'Rollback approval'; requirements = @(@{ name = 'Release manager'; groups = @(); users = @($Admin.Email); minApprovers = 1 }) })
    }
    if ($r.Status -ge 300) { Write-Note "rollback policy: HTTP $($r.Status) $(Write-Json $r.Body)" }
    # The version to go back to has to have run in the environment; the snapshot only says what it was.
    $priorAt = ((ConvertTo-Instant $rollbackCell.deployedAt) + $script:DateShift).AddDays(-3)
    Invoke-Local -Method POST -Path '/api/deployments/events' -ApiKey $TutorialApiKey -Body @{
        product = $HeroProduct; service = $rollbackCell.service; environment = $prodEnv; version = $rollbackCell.previousVersion
        source = if ($rollbackCell.source) { $rollbackCell.source } else { 'helm-deploy' }; deployedAt = $priorAt.ToString('o'); status = 'succeeded'
    } | Out-Null
    $r = Invoke-Local -Method POST -Path '/api/rollbacks' -Token $qaToken -Body @{
        product = $HeroProduct; targetEnv = $prodEnv; mode = 'manual'
        items = @(@{ service = $rollbackCell.service; toVersion = $rollbackCell.previousVersion })
        reason = "Error rate on $($rollbackCell.service) doubled after $($rollbackCell.version) — going back to $($rollbackCell.previousVersion) while the fix is prepared."
    }
    if ($r.Status -in 200, 201) {
        $rollbackTarget = [pscustomobject]@{ id = $r.Body.id; service = $rollbackCell.service; fromVersion = $rollbackCell.version; toVersion = $rollbackCell.previousVersion; environment = $prodEnv }
        $story['G. Rollback request awaiting approval (demo: Admin approves live)'] = $rollbackTarget
    } else { Write-Note "rollback request: HTTP $($r.Status) $(Write-Json $r.Body)" }
}

# Release note — what shipped to production this week, rendered from the default template.
$r = Invoke-Local -Method POST -Path '/api/release-notes/generate' -Token $adminToken -Body @{
    product = $HeroProduct; environment = $prodEnv
    from = [DateTimeOffset]::UtcNow.AddDays(-7).ToString('o'); to = [DateTimeOffset]::UtcNow.ToString('o')
}
if ($r.Status -in 200, 201) { $story['H. Release note (last 7 days to production)'] = [pscustomobject]@{ id = $r.Body.id; environment = $prodEnv } }
else { Write-Note "release note: HTTP $($r.Status) $(Write-Json $r.Body)" }

# Webhook — a generic subscription pointed at webhook-listener.ps1, so an approval on stage shows up as a
# delivery in a terminal a second later.
$r = Invoke-Local -Method POST -Path '/api/webhooks' -Token $adminToken -Body @{
    name = 'Tutorial listener (scripts/tutorial/webhook-listener.ps1)'; url = $WebhookUrl; targetType = 'generic'
    events = @('deployment.created', 'promotion.created', 'promotion.approved', 'promotion.rejected', 'promotion.deployed', 'promotion.ticket.approved', 'promotion.ticket.issue-raised', 'rollback.approved', 'release_note.generated')
    filters = @{ products = @($HeroProduct) }
}
if ($r.Status -in 200, 201) { $story['I. Webhook subscription'] = [pscustomobject]@{ id = $r.Body.id; url = $WebhookUrl } }
else { Write-Note "webhook: HTTP $($r.Status) $(Write-Json $r.Body)" }

# ── 9. Cheat sheet ───────────────────────────────────────────────────────────────────────────
Write-Step 'Cheat sheet'
$counts = Invoke-Psql -Sql @"
select 'deploy_events', count(*) from deploy_events
union all select 'builds', count(*) from builds
union all select 'promotion_candidates', count(*) from promotion_candidates
union all select 'promotion_work_items', count(*) from promotion_work_items
union all select 'work_item_approvals', count(*) from work_item_approvals
union all select 'rollback_requests', count(*) from rollback_requests
union all select 'release_notes', count(*) from release_notes
union all select 'webhook_subscriptions', count(*) from webhook_subscriptions;
"@

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("# InfraPortal tutorial environment — cheat sheet")
[void]$sb.AppendLine()
[void]$sb.AppendLine("Built $([DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm')) from a snapshot of $($manifest.source) taken $($manifest.exportedAt). Hero product: **$HeroProduct**.")
[void]$sb.AppendLine()
[void]$sb.AppendLine("Web: $WebBase   API: $ApiBase   Presenter's script: docs/tutorial/presentation-guide.md")
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Accounts")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Role | E-mail | Password | Name |")
[void]$sb.AppendLine("|---|---|---|---|")
foreach ($acc in $Accounts) { [void]$sb.AppendLine("| $($acc.Role) | $($acc.Email) | $($acc.Password) | $($acc.Name) |") }
[void]$sb.AppendLine()
[void]$sb.AppendLine("Pipeline API key (header ``X-Api-Key``): ``$TutorialApiKey``")
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Scenes")
[void]$sb.AppendLine()
foreach ($entry in $story.GetEnumerator()) {
    $v = $entry.Value
    $link = switch -Wildcard ($entry.Key) {
        'F.*' { "$WebBase/deployments/events/$($v.id) — $($v.service) $($v.version) → $($v.environment)" }
        'G.*' { "$WebBase/rollbacks — $($v.service) $($v.fromVersion) → $($v.toVersion) in $($v.environment)" }
        'H.*' { "$WebBase/release-notes/$HeroProduct/$($v.id)" }
        'I.*' { "$WebBase/webhooks/$($v.id) → $($v.url)" }
        default { "$WebBase/promotions/$($v.id) — $(Describe-Candidate $v)" }
    }
    [void]$sb.AppendLine("- **$($entry.Key)**  $link")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Live commands")
[void]$sb.AppendLine()
if ($e) {
    [void]$sb.AppendLine("Land the approved promotion (scene E) — the deploy event closes it as Deployed:")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('```bash')
    [void]$sb.AppendLine("curl -s -X POST $ApiBase/api/deployments/events -H 'X-Api-Key: $TutorialApiKey' -H 'Content-Type: application/json' -d '{""product"":""$($e.product)"",""service"":""$($e.service)"",""environment"":""$($e.targetEnv)"",""version"":""$($e.version)"",""source"":""helm-deploy"",""status"":""succeeded"",""deployedAt"":""'`$(date -u +%Y-%m-%dT%H:%M:%SZ)'""}'")
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine()
}
if ($heroService) {
    $bumped = if ($e -and $e.version -match '^(\d+)\.(\d+)\.(\d+)') { "$($Matches[1]).$($Matches[2]).$([int]$Matches[3] + 1)-gfeedface" } else { '9.9.9-gfeedface' }
    $firstEnv = $heroEnvs[0]
    [void]$sb.AppendLine("Register a build, then deploy it to $firstEnv (shows up under Artifacts, then on the matrix):")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('```bash')
    [void]$sb.AppendLine("curl -s -X POST $ApiBase/api/builds -H 'X-Api-Key: $TutorialApiKey' -H 'Content-Type: application/json' -d '{""product"":""$HeroProduct"",""service"":""$heroService"",""version"":""$bumped"",""branch"":""refs/heads/master"",""commitSha"":""feedfacefeedfacefeedfacefeedfacefeedface"",""buildId"":""8800100"",""buildUrl"":""https://example.visualstudio.com/_build/results?buildId=8800100""}'")
    [void]$sb.AppendLine("curl -s -X POST $ApiBase/api/deployments/events -H 'X-Api-Key: $TutorialApiKey' -H 'Content-Type: application/json' -d '{""product"":""$HeroProduct"",""service"":""$heroService"",""environment"":""$firstEnv"",""version"":""$bumped"",""source"":""helm-deploy"",""deployedAt"":""'`$(date -u +%Y-%m-%dT%H:%M:%SZ)'"",""references"":[{""type"":""work-item"",""provider"":""jira"",""key"":""TUT-202"",""title"":""Tutorial: live deploy from the terminal""}]}'")
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine()
    if ($heroEnvs.Count -gt 1) {
        [void]$sb.AppendLine("Open a promotion for it ($firstEnv → $($heroEnvs[1])) the way a pipeline would:")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine('```bash')
        [void]$sb.AppendLine("curl -s -X POST $ApiBase/api/promotions -H 'X-Api-Key: $TutorialApiKey' -H 'Content-Type: application/json' -d '{""product"":""$HeroProduct"",""service"":""$heroService"",""sourceEnv"":""$firstEnv"",""targetEnv"":""$($heroEnvs[1])"",""version"":""$bumped"",""references"":[{""type"":""work-item"",""provider"":""jira"",""key"":""TUT-202"",""title"":""Tutorial: live deploy from the terminal""}]}'")
        [void]$sb.AppendLine('```')
        [void]$sb.AppendLine()
    }
}
[void]$sb.AppendLine("Webhook listener (run before approving anything on stage):")
[void]$sb.AppendLine()
[void]$sb.AppendLine('```powershell')
[void]$sb.AppendLine('.\scripts\tutorial\webhook-listener.ps1')
[void]$sb.AppendLine('```')
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Row counts")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Table | Rows |")
[void]$sb.AppendLine("|---|---|")
foreach ($line in $counts) { $parts = "$line".Split('|'); if ($parts.Count -eq 2) { [void]$sb.AppendLine("| $($parts[0]) | $($parts[1]) |") } }
New-Item -ItemType Directory -Force -Path $StateDir | Out-Null
$sb.ToString() | Set-Content -Path $CheatSheetPath -Encoding utf8

# The same facts, machine-readable, for the screenshot/walkthrough generator (scripts/tutorial/capture).
$scenes = [ordered]@{}
foreach ($entry in $story.GetEnumerator()) { $scenes[$entry.Key.Substring(0, 1)] = [ordered]@{ title = $entry.Key; data = $entry.Value } }
ConvertTo-Json -InputObject ([ordered]@{
    builtAt = [DateTimeOffset]::UtcNow.ToString('o'); source = $manifest.source; heroProduct = $HeroProduct
    heroService = $heroService; prodEnv = $prodEnv; heroEnvs = @($heroEnvs)
    webBase = $WebBase; apiBase = $ApiBase; apiKey = $TutorialApiKey
    accounts = @($Accounts | ForEach-Object { [ordered]@{ role = $_.Role; email = $_.Email; password = $_.Password; name = $_.Name } })
    scenes = $scenes
}) -Depth 10 | Set-Content -Path (Join-Path $StateDir 'tutorial-scenes.json') -Encoding utf8

# ── Report ───────────────────────────────────────────────────────────────────────────────────
Write-Host ''
Write-Step 'Tutorial environment ready'
foreach ($line in $counts) { $parts = "$line".Split('|'); if ($parts.Count -eq 2) { Write-Detail ("{0,-24} {1}" -f $parts[0], $parts[1]) } }
Write-Host ''
foreach ($entry in $story.GetEnumerator()) { Write-Detail $entry.Key }
Write-Host ''
Write-Ok "Cheat sheet with links, ids and curl commands: $CheatSheetPath"
Write-Detail "Presenter's script: docs/tutorial/presentation-guide.md"
Write-Detail "Sign in at $WebBase — admin@localhost / admin123, qa@localhost / qa123, user@localhost / user123"
if (-not (Test-Port $WebPort)) { Write-Note "The web dev server isn't running: .\scripts\start.ps1 starts it (the API is already up)." }
