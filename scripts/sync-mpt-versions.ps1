#Requires -Version 7
<#
.SYNOPSIS
    Catches InfraPortal up with what MPT staging and production are actually running.

.DESCRIPTION
    Pipelines normally report every deploy to InfraPortal as it happens. When InfraPortal was down for
    a while, or a backup had to be restored, the deploys that happened in between are missing and the
    matrix shows versions that are no longer live. This script closes that gap from two sources:

      1. The version manifest each environment publishes as a public blob:

             staging     https://mptstagingr1data.blob.core.windows.net/public/manifest/versions.json
             production  https://mptprodr1data.blob.core.windows.net/public/manifest/versions.json

         Both have the same shape — a flat `components` map of service → version:

             { "product": "marketplace",
               "components": { "mpt-billing": "5.0.348-gabbe9ae2", ... },
               "version": "..." }

         The manifest's own `product` ("marketplace") is an obsolete name and is ignored; see -Product.

      2. The AKS cluster behind each environment (mpt-staging-r1-aks, mpt-prod-r1-aks). Every backend
         component is a Helm release in the `mp-platform` namespace whose workloads carry the version in
         the `app.kubernetes.io/version` label, grouped by `app.kubernetes.io/instance`. The cluster is
         read to confirm the manifest before anything is written: a component whose manifest version
         differs from what the cluster runs is reported and left alone, never recorded. Micro-frontends
         (mpt-web-*, swo-web-*) are static sites and are not in the cluster; those are recorded from the
         manifest alone, and their notes say so.

    For each component the script compares the manifest version with what InfraPortal currently shows
    for that service in that environment (`GET /api/deployments/state`) and records the difference
    with `POST /api/deployments/manual`, which creates a new deploy event for an existing service. Three
    rules keep a catch-up from rewriting history:

      * Existing services only. A component InfraPortal has never seen is listed, not created — the
        pipeline that owns it registers it, with the references and participants this script cannot
        know. Rows under obsolete products don't count as "seen" (see -IgnoreProduct).
      * Forward only. A manifest version older than InfraPortal's current one is reported and skipped —
        during a catch-up InfraPortal is behind, never ahead, so "older" means the manifest or the
        comparison is wrong, not that a rollback happened. Equal version numbers are unchanged, even
        when the git suffix differs.
      * Confirmed only. See the cluster check above. -NoClusterCheck turns it off for a machine without
        kubectl access; the run then trusts the manifest.

    Only the drift is written, so the script is safe to run repeatedly — a second run right after the
    first is a no-op. -WhatIf prints every entry it would record and writes nothing.

.PARAMETER ApiBaseUrl
    Root of the InfraPortal API. Defaults to the DEPLOYMENTS_URL environment variable.

.PARAMETER ApiKey
    API key sent as X-Api-Key. Defaults to the DEPLOYMENTS_API_KEY environment variable (then
    INFRAPILOT_API_KEY). The key must not be product-scoped more narrowly than the products the MPT
    services are filed under — mpt, mpt-extensions, mpt-jenkins-tools today — or those writes come
    back 403.

.PARAMETER Target
    Which environment(s) to sync: staging, production, or all (default).

.PARAMETER Product
    The product MPT services belong to by default: mpt. A service InfraPortal already files under
    another product (because an admin configured a service product override, or its pipeline posts
    under mpt-extensions directly) is updated where it lives; the server applies the same overrides on
    write, so the admin's mapping wins either way. When a service appears under more than one product,
    this one is preferred; otherwise the service is reported as ambiguous and skipped.

.PARAMETER IgnoreProduct
    Products whose rows are treated as if they didn't exist. Default: marketplace — the obsolete name
    an earlier version of this script wrote under. Nothing is ever written there, and a service tracked
    only under an ignored product counts as unknown.

.PARAMETER Note
    The note recorded on every manual entry — the endpoint requires one. The default names the source
    manifest and whether the cluster confirmed the version.

.PARAMETER Service
    Restricts the sync to these component names (wildcards allowed, e.g. `mpt-web-*`).

.PARAMETER NoClusterCheck
    Skip reading the AKS clusters and trust the manifests as they are. For machines without kubectl or
    without access to the clusters.

.PARAMETER StagingManifestUrl
.PARAMETER ProductionManifestUrl
    Override the manifest locations. A path to a saved copy on disk works as well as a URL.

.PARAMETER StagingCluster
.PARAMETER ProductionCluster
    kubectl context names for the two clusters. Defaults: mpt-staging-r1-aks, mpt-prod-r1-aks — the
    names `az aks get-credentials` creates.

.PARAMETER ClusterNamespace
    Namespace the MPT Helm releases live in. Default: mp-platform.

.PARAMETER StagingEnvironment
.PARAMETER ProductionEnvironment
    InfraPortal environment names to read and write under. Defaults: staging, production. InfraPortal
    resolves its own aliases (production → prod), so these stay as the manifests call them.

.EXAMPLE
    .\scripts\sync-mpt-versions.ps1 -WhatIf

    Dry run with DEPLOYMENTS_URL / DEPLOYMENTS_API_KEY from the environment: reads both manifests,
    both clusters and InfraPortal, prints every entry it would record, writes nothing.

.EXAMPLE
    .\scripts\sync-mpt-versions.ps1

    The real thing. Run after an outage or a restore.

.EXAMPLE
    .\scripts\sync-mpt-versions.ps1 -Target production -Service 'swo-web-*' -NoClusterCheck

    Only the production micro-frontends, which the cluster cannot confirm anyway.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$ApiBaseUrl = $env:DEPLOYMENTS_URL,
    [string]$ApiKey,
    [ValidateSet('staging', 'production', 'all')]
    [string]$Target = 'all',
    [string]$Product = 'mpt',
    [string[]]$IgnoreProduct = @('marketplace'),
    [string]$Note,
    [string[]]$Service,
    [switch]$NoClusterCheck,
    [string]$StagingManifestUrl = 'https://mptstagingr1data.blob.core.windows.net/public/manifest/versions.json',
    [string]$ProductionManifestUrl = 'https://mptprodr1data.blob.core.windows.net/public/manifest/versions.json',
    [string]$StagingCluster = 'mpt-staging-r1-aks',
    [string]$ProductionCluster = 'mpt-prod-r1-aks',
    [string]$ClusterNamespace = 'mp-platform',
    [string]$StagingEnvironment = 'staging',
    [string]$ProductionEnvironment = 'production'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Dot-sourced for the Write-Step/Ok/Detail/Note helpers, so this script prints like the rest of
# scripts/. Loading it defines the local-dev Docker/Postgres settings too, but nothing runs until
# it's called, and this script never calls any of it.
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not $ApiBaseUrl) {
    throw 'No InfraPortal URL. Set DEPLOYMENTS_URL (e.g. https://infraportal.example.com) or pass -ApiBaseUrl.'
}
$ApiBaseUrl = $ApiBaseUrl.TrimEnd('/')

if (-not $ApiKey) { $ApiKey = $env:DEPLOYMENTS_API_KEY }
if (-not $ApiKey) { $ApiKey = $env:INFRAPILOT_API_KEY }
if (-not $ApiKey) {
    throw 'No API key. Set DEPLOYMENTS_API_KEY or pass -ApiKey.'
}
$AuthHeaders = @{ 'X-Api-Key' = $ApiKey }

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
        TimeoutSec         = 120
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

# ── Sources ──────────────────────────────────────────────────────────────────────────────────

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

<#
    What the cluster runs, as a hashtable of component → distinct version strings. Reads every workload
    kind a Helm release here produces (the mpt-nav-stats release is a CronJob and nothing else) and
    groups by `app.kubernetes.io/instance`, which is the Helm release name and matches the manifest's
    component names. Versions come from `app.kubernetes.io/version`; a workload without the label
    falls back to its first container's image tag.

    More than one distinct version for a component means a rollout is in progress (or a release is
    half-applied) — the caller treats that as "unconfirmed" rather than picking one.

    kubectl failing is thrown, not swallowed: the check exists to stop wrong versions being recorded,
    so silently running without it would defeat the purpose. -NoClusterCheck is the explicit opt-out.
#>
function Get-ClusterVersions {
    param(
        [Parameter(Mandatory)][string]$Context,
        [Parameter(Mandatory)][string]$Namespace
    )

    $output = & kubectl --context $Context get deployments,statefulsets,daemonsets,cronjobs `
        --namespace $Namespace --output json 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "kubectl failed against context '$Context': $($output -join ' ')`nRun 'az aks get-credentials' for the cluster, or pass -NoClusterCheck to trust the manifest."
    }
    $items = ($output -join "`n" | ConvertFrom-Json).items

    $versions = @{}   # case-insensitive, like the service-name lookups below
    foreach ($item in @($items)) {
        $labels = $item.metadata.PSObject.Properties['labels'] ? $item.metadata.labels : $null
        if (-not $labels) { continue }
        $instance = $labels.PSObject.Properties['app.kubernetes.io/instance'] ? "$($labels.'app.kubernetes.io/instance')" : ''
        if (-not $instance) { continue }

        $version = $labels.PSObject.Properties['app.kubernetes.io/version'] ? "$($labels.'app.kubernetes.io/version')".Trim() : ''
        if (-not $version) {
            # CronJobs nest the pod template one level deeper than Deployments do.
            $spec = $item.spec
            $template = $spec.PSObject.Properties['template'] ? $spec.template : $spec.jobTemplate.spec.template
            $image = "$($template.spec.containers[0].image)"
            if ($image -match ':([^:/]+)$') { $version = $Matches[1] }
        }
        if (-not $version) { continue }

        if (-not $versions.ContainsKey($instance)) { $versions[$instance] = [System.Collections.Generic.List[string]]::new() }
        if (-not $versions[$instance].Contains($version)) { $versions[$instance].Add($version) }
    }
    return $versions
}

# ── Versions ─────────────────────────────────────────────────────────────────────────────────

<#
    Orders two MPT version strings. Returns 1 when $A is newer than $B, -1 when older, 0 when they are
    the same version number, and $null when either can't be read.

    MPT versions are `major.minor.patch-g<hash>` (5.0.348-gabbe9ae2) or, for the Jenkins-built
    tools, `major.minor.patch-<build>.<hash>` (0.1.0-50.9f068d23). The numeric parts decide; the hash
    never does — two builds with the same number and different hashes are the same version for the
    purpose of "is InfraPortal behind?", and recording one over the other would be noise.
#>
function Compare-MptVersion {
    param(
        [Parameter(Mandatory)][string]$Left,
        [Parameter(Mandatory)][string]$Right
    )
    # Locals below are deliberately not $a/$b: PowerShell variable names are case-insensitive, so
    # `$a = ...` would overwrite a `$A` parameter (and inherit its [string] constraint).
    # The optional fourth number is a build counter (`-50.`), never the start of a git hash (`-g…`,
    # or a hash that happens to begin with digits): the lookahead refuses a hex character after it.
    $pattern = [regex]'^v?(\d+)\.(\d+)\.(\d+)(?:[-.](\d+)(?![0-9a-fA-F]))?'
    $parse = {
        param([string]$v)
        $m = $pattern.Match($v)
        if (-not $m.Success) { return $null }
        $build = if ($m.Groups[4].Success) { [int]$m.Groups[4].Value } else { 0 }
        return ,@([int]$m.Groups[1].Value, [int]$m.Groups[2].Value, [int]$m.Groups[3].Value, $build)
    }
    $l = & $parse $Left
    $r = & $parse $Right
    if ($null -eq $l -or $null -eq $r) { return $null }
    for ($i = 0; $i -lt 4; $i++) {
        if ($l[$i] -gt $r[$i]) { return 1 }
        if ($l[$i] -lt $r[$i]) { return -1 }
    }
    return 0
}

# ── Sync ─────────────────────────────────────────────────────────────────────────────────────

function New-Summary {
    param([string]$Environment)
    return [ordered]@{
        Environment = $Environment
        Updated = 0; Unchanged = 0; Older = 0; Mismatched = 0; Unknown = 0; Skipped = 0; Failed = 0
    }
}

<#
    Diffs one environment — manifest, cluster, InfraPortal — and records what differs. Returns a
    summary; per-service progress goes to the console as it happens.
#>
function Sync-Environment {
    param(
        [Parameter(Mandatory)][string]$Environment,
        [Parameter(Mandatory)][string]$ManifestUrl,
        [Parameter(Mandatory)][string]$Cluster
    )

    Write-Step "$Environment"
    Write-Detail "manifest  $ManifestUrl"
    $manifest = Get-Manifest -Url $ManifestUrl
    $components = Get-ManifestComponents -Manifest $manifest
    $summary = New-Summary -Environment $Environment

    if ($components.Count -eq 0) {
        Write-Note 'Nothing to sync — the manifest had no components (or -Service matched none).'
        return $summary
    }

    $runningVersions = $null   # not $cluster: that would overwrite the $Cluster parameter (names are case-insensitive)
    if (-not $NoClusterCheck) {
        Write-Detail "cluster   $Cluster / $ClusterNamespace"
        $runningVersions = Get-ClusterVersions -Context $Cluster -Namespace $ClusterNamespace
        Write-Detail "$($components.Count) component(s) in the manifest, $($runningVersions.Count) release(s) in the cluster"
    } else {
        Write-Detail "$($components.Count) component(s) in the manifest; cluster check off"
    }

    # Current state across every product — a service is looked up by name and the product it is filed
    # under is whatever InfraPortal says, not what the manifest says. /state returns the latest event
    # per (product, service, environment); rows under ignored products are dropped here so an obsolete
    # product can neither be updated nor make a service look "known".
    $stateResult = Invoke-Api -Method GET -Path "/api/deployments/state?environment=$([Uri]::EscapeDataString($Environment))"
    if ($stateResult.Status -ne 200) {
        throw "Reading current state failed (HTTP $($stateResult.Status)): $(Get-ApiError $stateResult)"
    }
    $current = @{}   # service → list of @{ Product; Version }; PowerShell hashtables are case-insensitive
    foreach ($row in @($stateResult.Body)) {
        if (-not $row) { continue }
        if ($IgnoreProduct -contains $row.product) { continue }
        if (-not $current.ContainsKey($row.service)) { $current[$row.service] = [System.Collections.Generic.List[hashtable]]::new() }
        $current[$row.service].Add(@{ Product = "$($row.product)"; Version = "$($row.version)" })
    }
    Write-Detail "InfraPortal tracks $($current.Count) service(s) in $Environment"

    foreach ($name in $components.Keys) {
        $version = $components[$name]

        # 1. Existing services only.
        if (-not $current.ContainsKey($name)) {
            $summary.Unknown++
            Write-Note "$name — not tracked by InfraPortal in $Environment; not created (manifest says $version)"
            continue
        }
        $rows = $current[$name]
        $row = if ($rows.Count -eq 1) {
            $rows[0]
        } else {
            $preferred = @($rows | Where-Object { $_.Product -eq $Product })
            if ($preferred.Count -eq 1) { $preferred[0] } else { $null }
        }
        if ($null -eq $row) {
            $summary.Skipped++
            Write-Warning "$name — filed under several products ($(($rows | ForEach-Object { $_.Product }) -join ', ')) and none is '$Product'; skipped"
            continue
        }
        $live = $row.Version

        # 2. Forward only.
        $order = Compare-MptVersion -Left $version -Right $live
        if ($null -eq $order) {
            $summary.Skipped++
            Write-Warning "$name — can't order versions '$version' (manifest) and '$live' (InfraPortal); skipped"
            continue
        }
        if ($order -eq 0) {
            $summary.Unchanged++
            Write-Verbose "$name unchanged at $live"
            continue
        }
        if ($order -lt 0) {
            $summary.Older++
            Write-Note "$name — manifest $version is older than InfraPortal's $live; not recorded"
            continue
        }

        # 3. Confirmed by the cluster, when it runs there.
        $confirmation = 'manifest only (not deployed to the cluster)'
        if ($null -ne $runningVersions) {
            if ($runningVersions.ContainsKey($name)) {
                $running = $runningVersions[$name]
                if ($running.Count -ne 1) {
                    $summary.Mismatched++
                    Write-Warning "$name — cluster runs several versions ($($running -join ', ')); rollout in progress? Not recorded."
                    continue
                }
                if ($running[0] -ne $version) {
                    $summary.Mismatched++
                    Write-Warning "$name — manifest says $version but $Cluster runs $($running[0]); not recorded"
                    continue
                }
                $confirmation = "version confirmed on $Cluster"
            }
        } else {
            $confirmation = 'cluster check skipped'
        }

        $what = "$($row.Product)/$name in $Environment"
        if (-not $PSCmdlet.ShouldProcess($what, "record $live -> $version ($confirmation)")) {
            $summary.Skipped++
            continue
        }

        $noteText = if ($Note) { $Note } else { "Catch-up sync from the MPT $Environment manifest (versions.json); $confirmation" }
        $result = Invoke-Api -Method POST -Path '/api/deployments/manual' -Body @{
            product     = $row.Product
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
                # /manual bases the entry on the server's own idea of the latest event, which an admin
                # override can point at a different product than the row compared against above. The
                # response says what it actually superseded; a disagreement is worth a look.
                $previous = $result.Body.PSObject.Properties['previousVersion'] ? "$($result.Body.previousVersion)" : ''
                if ($previous -and $previous -ne $live) {
                    Write-Warning "$name — InfraPortal based the entry on $previous, not the $live compared against; check the product it is filed under"
                }
            }
            404 {
                # /state listed the service but /manual found nothing to base the entry on: an override
                # redirects the write to a product where the service has no history yet, or the state
                # read is a stale snapshot. Either way, report rather than guess.
                $summary.Failed++
                Write-Warning "$name — no predecessor to base the entry on: $(Get-ApiError $result)"
            }
            default {
                $summary.Failed++
                Write-Warning "$name — failed (HTTP $($result.Status)): $(Get-ApiError $result)"
            }
        }
    }

    # Informational: services InfraPortal tracks that the manifest doesn't mention. Not touched — the
    # manifest is authoritative about what it lists, not about what it omits.
    if (-not $Service) {
        $orphans = @($current.Keys |
            Where-Object { -not $components.Contains($_) } |
            Where-Object { $svc = $_; @($current[$svc] | Where-Object { $_.Product -like 'mpt*' }).Count -gt 0 } |
            Sort-Object)
        if ($orphans.Count -gt 0) {
            Write-Detail "Tracked under an mpt* product but not in the manifest, left untouched: $($orphans -join ', ')"
        }
    }

    return $summary
}

# ── Run ──────────────────────────────────────────────────────────────────────────────────────

$environments = @()
if ($Target -in 'staging', 'all') {
    $environments += @{ Environment = $StagingEnvironment; ManifestUrl = $StagingManifestUrl; Cluster = $StagingCluster }
}
if ($Target -in 'production', 'all') {
    $environments += @{ Environment = $ProductionEnvironment; ManifestUrl = $ProductionManifestUrl; Cluster = $ProductionCluster }
}

Write-Step "InfraPortal $ApiBaseUrl"
if ($WhatIfPreference) { Write-Note 'Dry run (-WhatIf) — nothing will be written.' }

$summaries = @()
foreach ($source in $environments) {
    $summaries += Sync-Environment -Environment $source.Environment -ManifestUrl $source.ManifestUrl -Cluster $source.Cluster
}

Write-Host ''
Write-Step 'Summary'
$attention = 0
foreach ($s in $summaries) {
    Write-Detail ("{0,-12} updated {1,-4} unchanged {2,-4} older {3,-4} mismatched {4,-4} unknown {5,-4} skipped {6,-4} failed {7}" -f `
        $s.Environment, $s.Updated, $s.Unchanged, $s.Older, $s.Mismatched, $s.Unknown, $s.Skipped, $s.Failed)
    $attention += $s.Failed + $s.Mismatched
}

if ($attention -gt 0) {
    Write-Note "$attention component(s) failed or disagreed with the cluster — see the warnings above."
    exit 1
}
