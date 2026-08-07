#Requires -Version 7
<#
.SYNOPSIS
    Records manual deployments in InfraPilot from the MPT staging and production version manifests.

.DESCRIPTION
    Each MPT environment publishes what it is currently running as a public blob:

        staging     https://mptstagingr1data.blob.core.windows.net/public/manifest/versions.json
        production  https://mptprodr1data.blob.core.windows.net/public/manifest/versions.json

    Both have the same shape — a product name, a flat `components` map of service → version, and the
    manifest's own release version:

        { "product": "marketplace",
          "components": { "mpt-billing": "5.0.347-g495d92f0", ... },
          "version": "5.0.5921-g18427fa4" }

    This script diffs each manifest against what InfraPilot already believes is deployed
    (`GET /api/deployments/state`) and records the difference:

      * version differs        → POST /api/deployments/manual   (a manual deployment entry)
      * service unknown here   → POST /api/deployments/events   (seeds a baseline, see below)
      * version matches        → nothing, and nothing is logged as changed

    Only the drift is written, so the script is safe to run repeatedly — a second run right after the
    first is a no-op.

    Why two endpoints. `/manual` builds the new entry *from the latest existing one* for that
    product/service/environment: it carries the references and participants over, stamps
    Source="manual", sets triggered-by to the caller, and attaches the note. That means it needs a
    predecessor, and returns 404 when there isn't one. A component appearing in the manifest for the
    first time therefore has nothing to base a manual entry on, so it is seeded through the ingest
    endpoint instead (Source="mpt-manifest"), and every later run of this script updates it through
    `/manual` like everything else. Pass -NoSeed to report those instead of creating them.

    Authentication. -ApiKey (X-Api-Key) is the mode this is built for and the only one that can seed:
    the ingest endpoint accepts API keys exclusively. -BearerToken works for the manual entries alone,
    and the token's user must be an admin. A product-scoped API key must include the manifest's
    product ("marketplace") or every write comes back 403.

.PARAMETER ApiBaseUrl
    Root of the InfraPilot API. Defaults to the local dev API (http://localhost:5259).

.PARAMETER ApiKey
    API key sent as X-Api-Key. Falls back to the INFRAPILOT_API_KEY environment variable.

.PARAMETER BearerToken
    Entra access token, used instead of an API key. Cannot seed services InfraPilot has never seen.

.PARAMETER Target
    Which manifest(s) to sync: staging, production, or all (default).

.PARAMETER Product
    Overrides the product name. By default the manifest's own `product` field is used, so the two
    environments stay under whatever the manifests call themselves.

.PARAMETER Note
    The note recorded on every manual entry — the endpoint requires one. Defaults to a line naming
    the environment and the manifest's release version.

.PARAMETER Service
    Restricts the sync to these component names (wildcards allowed, e.g. `mpt-web-*`). Everything
    else in the manifest is left alone.

.PARAMETER IncludeManifestVersion
    Also record the manifest's own top-level `version` as a service of its own (see
    -ManifestVersionService). Off by default — it is a rollup, not a deployable component.

.PARAMETER ManifestVersionService
    Service name used for the manifest's own version. Default: marketplace-release.

.PARAMETER NoSeed
    Don't create baselines for components InfraPilot has never seen; list them and move on.

.PARAMETER StagingManifestUrl
.PARAMETER ProductionManifestUrl
    Override the manifest locations. A path to a saved copy on disk works as well as a URL, for
    replaying a specific release or running without reaching the blob endpoints.

.PARAMETER StagingEnvironment
.PARAMETER ProductionEnvironment
    Environment names to write under. Defaults: staging, production.

.EXAMPLE
    .\scripts\sync-mpt-versions.ps1 -ApiKey $env:INFRAPILOT_API_KEY -WhatIf

    Dry run against the local API: prints every deployment it would record, writes nothing.

.EXAMPLE
    .\scripts\sync-mpt-versions.ps1 -ApiBaseUrl https://infrapilot.example.com -Target production

.EXAMPLE
    .\scripts\sync-mpt-versions.ps1 -Service 'mpt-web-*' -Note 'Post-release reconciliation'
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ApiBaseUrl,
    [string]$ApiKey = $env:INFRAPILOT_API_KEY,
    [string]$BearerToken,
    [ValidateSet('staging', 'production', 'all')]
    [string]$Target = 'all',
    [string]$Product,
    [string]$Note,
    [string[]]$Service,
    [switch]$IncludeManifestVersion,
    [string]$ManifestVersionService = 'marketplace-release',
    [switch]$NoSeed,
    [string]$StagingManifestUrl = 'https://mptstagingr1data.blob.core.windows.net/public/manifest/versions.json',
    [string]$ProductionManifestUrl = 'https://mptprodr1data.blob.core.windows.net/public/manifest/versions.json',
    [string]$StagingEnvironment = 'staging',
    [string]$ProductionEnvironment = 'production'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Dot-sourced for the Write-Step/Ok/Detail/Note helpers and $ApiPort, so this script prints like the
# rest of scripts/. Loading it defines the local-dev Docker/Postgres settings too, but nothing runs
# until it's called, and this script never calls any of it.
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not $ApiBaseUrl) { $ApiBaseUrl = "http://localhost:$ApiPort" }
$ApiBaseUrl = $ApiBaseUrl.TrimEnd('/')

if (-not $ApiKey -and -not $BearerToken) {
    throw 'No credentials. Pass -ApiKey (or set INFRAPILOT_API_KEY), or -BearerToken for an admin user.'
}

$AuthHeaders = @{}
if ($ApiKey)      { $AuthHeaders['X-Api-Key']     = $ApiKey }
if ($BearerToken) { $AuthHeaders['Authorization'] = "Bearer $BearerToken" }

# ── HTTP ─────────────────────────────────────────────────────────────────────────────────────

<#
    One JSON call against the API. -SkipHttpErrorCheck rather than try/catch: a 404 from /manual is
    an expected, meaningful answer ("no predecessor to base this on"), not an error, and the caller
    needs the status code to tell it apart from a 403 or a 500.

    Returns @{ Status; Body; Raw } — Body is the parsed JSON when there was any, otherwise $null.
#>
function Invoke-Api {
    param(
        [Parameter(Mandatory)][ValidateSet('GET', 'POST')][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        $Body
    )
    $request = @{
        Uri                = "$ApiBaseUrl$Path"
        Method             = $Method
        Headers            = $AuthHeaders
        SkipHttpErrorCheck = $true
        TimeoutSec         = 60
    }
    if ($null -ne $Body) {
        $request['Body']        = ($Body | ConvertTo-Json -Depth 10 -Compress)
        $request['ContentType'] = 'application/json'
    }

    $response = Invoke-WebRequest @request
    $parsed = $null
    if ($response.Content) {
        try { $parsed = $response.Content | ConvertFrom-Json } catch { $parsed = $null }
    }
    return @{ Status = [int]$response.StatusCode; Body = $parsed; Raw = "$($response.Content)" }
}

<# Best-effort one-line reason out of an error response, for the console. #>
function Get-ApiError {
    param([Parameter(Mandatory)]$Result)
    $body = $Result.Body
    if ($body) {
        if ($body.PSObject.Properties.Name -contains 'error')  { return "$($body.error)" }
        if ($body.PSObject.Properties.Name -contains 'errors') { return ($body.errors -join '; ') }
        if ($body.PSObject.Properties.Name -contains 'title')  { return "$($body.title)" }
    }
    if ($Result.Raw) { return $Result.Raw.Substring(0, [Math]::Min(200, $Result.Raw.Length)) }
    return "HTTP $($Result.Status)"
}

<#
    Fetches a manifest, over HTTP or from a file on disk — a saved copy is how you replay a specific
    release, or run this from somewhere that can't reach the blob endpoints. Invoke-RestMethod rejects
    the file:// scheme outright, so the local case is read directly rather than handed to it.

    Cache-Control: no-cache because the blobs sit behind a CDN and a stale copy would silently sync
    yesterday's versions — the whole point here is to read what is live *now*.
#>
function Get-Manifest {
    param([Parameter(Mandatory)][string]$Url)

    if ($Url -notmatch '^https?://') {
        $path = $Url -replace '^file:///?', ''
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Manifest not found: $Url (not an http(s) URL, and no such file)."
        }
        return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    }
    return Invoke-RestMethod -Uri $Url -Headers @{ 'Cache-Control' = 'no-cache' } -TimeoutSec 60
}

<# Manifest `components` (a JSON object) flattened to [ordered] service → version, filtered by -Service. #>
function Get-ManifestComponents {
    param([Parameter(Mandatory)]$Manifest)

    $components = [ordered]@{}
    if (-not $Manifest.PSObject.Properties['components']) { return $components }

    foreach ($property in $Manifest.components.PSObject.Properties | Sort-Object Name) {
        $version = "$($property.Value)".Trim()
        if (-not $version) { continue }   # a component listed with no version says nothing to record
        if ($Service -and -not ($Service | Where-Object { $property.Name -like $_ })) { continue }
        $components[$property.Name] = $version
    }
    return $components
}

# ── Sync ─────────────────────────────────────────────────────────────────────────────────────

<#
    Diffs one manifest against InfraPilot's current state for that environment and records what
    differs. Returns a summary hashtable; per-service progress goes to the console as it happens.
#>
function Sync-Environment {
    param(
        [Parameter(Mandatory)][string]$Environment,
        [Parameter(Mandatory)][string]$Url
    )

    Write-Step "$Environment — $Url"
    $manifest = Get-Manifest -Url $Url

    $productName = if ($Product) {
        $Product
    } elseif ($manifest.PSObject.Properties['product']) {
        "$($manifest.product)"
    } else {
        ''
    }
    if (-not $productName) {
        throw "The $Environment manifest names no product. Pass -Product to name it explicitly."
    }

    $manifestVersion = if ($manifest.PSObject.Properties['version']) { "$($manifest.version)" } else { '' }
    $components = Get-ManifestComponents -Manifest $manifest
    if ($IncludeManifestVersion -and $manifestVersion) {
        if (-not $Service -or ($Service | Where-Object { $ManifestVersionService -like $_ })) {
            $components[$ManifestVersionService] = $manifestVersion
        }
    }
    Write-Detail "product '$productName', $($components.Count) component(s), manifest version $(if ($manifestVersion) { $manifestVersion } else { 'n/a' })"

    if ($components.Count -eq 0) {
        Write-Note 'Nothing to sync — the manifest had no components (or -Service matched none).'
        return @{ Environment = $Environment; Updated = 0; Seeded = 0; Unchanged = 0; Skipped = 0; Failed = 0 }
    }

    $noteText = if ($Note) {
        $Note
    } elseif ($manifestVersion) {
        "Synced from the MPT $Environment manifest (versions.json), release $manifestVersion"
    } else {
        "Synced from the MPT $Environment manifest (versions.json)"
    }

    # Current state, as one call — /state returns the latest event per (product, service, environment).
    $stateResult = Invoke-Api -Method GET -Path "/api/deployments/state?product=$([Uri]::EscapeDataString($productName))&environment=$([Uri]::EscapeDataString($Environment))"
    if ($stateResult.Status -ne 200) {
        throw "Reading current state failed (HTTP $($stateResult.Status)): $(Get-ApiError $stateResult)"
    }
    $current = @{}   # PowerShell hashtables are case-insensitive, which is what we want for service names
    foreach ($row in @($stateResult.Body)) {
        if ($row) { $current[$row.service] = "$($row.version)" }
    }
    Write-Detail "InfraPilot knows $($current.Count) service(s) in $productName/$Environment"

    $summary = @{ Environment = $Environment; Updated = 0; Seeded = 0; Unchanged = 0; Skipped = 0; Failed = 0 }

    foreach ($name in $components.Keys) {
        $version = $components[$name]
        $known = $current.ContainsKey($name)
        $live = if ($known) { $current[$name] } else { $null }

        if ($known -and $live -eq $version) {
            $summary.Unchanged++
            Write-Verbose "$name unchanged at $version"
            continue
        }

        if (-not $known) {
            if ($NoSeed) {
                $summary.Skipped++
                Write-Note "$name — not in InfraPilot yet, skipped (-NoSeed). Would have been seeded at $version."
                continue
            }
            if (-not $ApiKey) {
                $summary.Skipped++
                Write-Note "$name — not in InfraPilot yet and seeding needs an API key (ingest rejects bearer tokens). Skipped."
                continue
            }
            if (-not $PSCmdlet.ShouldProcess("$productName/$name in $Environment", "seed baseline $version")) {
                $summary.Skipped++
                continue
            }

            # Baseline only — DeployedAt is the manifest read, which is the closest honest timestamp
            # we have (the manifest says what is running, not when it got there). Source distinguishes
            # these from the manual entries every later run writes, and the manifest reference is
            # carried onto all of them by /manual, so each entry points back at its source of truth.
            #
            # Metadata stays deliberately thin: /manual copies the predecessor's metadata forward and
            # only overwrites `note`, so anything release-specific put here (the manifest's own
            # version, say) would be inherited unchanged and read as a lie on every later entry.
            $result = Invoke-Api -Method POST -Path '/api/deployments/events' -Body @{
                product     = $productName
                service     = $name
                environment = $Environment
                version     = $version
                source      = 'mpt-manifest'
                deployedAt  = (Get-Date).ToUniversalTime().ToString('o')
                status      = 'succeeded'
                references  = @(@{ type = 'manifest'; url = $Url; key = "$productName/$Environment" })
                metadata    = @{ note = $noteText }
            }
            if ($result.Status -in 200, 201) {
                $summary.Seeded++
                Write-Ok "$name — seeded at $version"
            } else {
                $summary.Failed++
                Write-Warning "$name — seeding failed (HTTP $($result.Status)): $(Get-ApiError $result)"
            }
            continue
        }

        if (-not $PSCmdlet.ShouldProcess("$productName/$name in $Environment", "record manual deployment $live -> $version")) {
            $summary.Skipped++
            continue
        }

        $result = Invoke-Api -Method POST -Path '/api/deployments/manual' -Body @{
            product     = $productName
            service     = $name
            environment = $Environment
            version     = $version
            note        = $noteText
            status      = 'succeeded'
        }
        switch ($result.Status) {
            { $_ -in 200, 201 } {
                $summary.Updated++
                Write-Ok "$name — $live -> $version"
            }
            404 {
                # /manual found no predecessor although /state listed one: the state read is a snapshot,
                # so this is the race (or a service filtered out of state by an admin action) rather
                # than a bug. Report it — seeding here would create a second, contradictory baseline.
                $summary.Failed++
                Write-Warning "$name — /manual found no predecessor to base the entry on: $(Get-ApiError $result)"
            }
            default {
                $summary.Failed++
                Write-Warning "$name — failed (HTTP $($result.Status)): $(Get-ApiError $result)"
            }
        }
    }

    # Informational: services InfraPilot tracks that the manifest doesn't mention. Not touched — the
    # manifest is authoritative about what it lists, not about what it omits.
    if (-not $Service) {
        $orphans = @($current.Keys | Where-Object { -not $components.Contains($_) } | Sort-Object)
        if ($orphans.Count -gt 0) {
            Write-Detail "Not in the manifest, left untouched: $($orphans -join ', ')"
        }
    }

    return $summary
}

# ── Run ──────────────────────────────────────────────────────────────────────────────────────

$environments = @()
if ($Target -in 'staging', 'all')    { $environments += @{ Environment = $StagingEnvironment;    Url = $StagingManifestUrl } }
if ($Target -in 'production', 'all') { $environments += @{ Environment = $ProductionEnvironment; Url = $ProductionManifestUrl } }

Write-Step "InfraPilot $ApiBaseUrl (auth: $(if ($ApiKey) { 'API key' } else { 'bearer token' }))"
if ($WhatIfPreference) { Write-Note 'Dry run (-WhatIf) — nothing will be written.' }

$summaries = @()
foreach ($source in $environments) {
    $summaries += Sync-Environment -Environment $source.Environment -Url $source.Url
}

Write-Host ''
Write-Step 'Summary'
$failures = 0
foreach ($s in $summaries) {
    Write-Detail ("{0,-12} updated {1,-4} seeded {2,-4} unchanged {3,-4} skipped {4,-4} failed {5}" -f `
        $s.Environment, $s.Updated, $s.Seeded, $s.Unchanged, $s.Skipped, $s.Failed)
    $failures += $s.Failed
}

if ($failures -gt 0) {
    Write-Note "$failures component(s) failed — see the warnings above."
    exit 1
}
