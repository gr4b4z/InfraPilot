#Requires -Version 7
<#
.SYNOPSIS
    Pulls the *active* state of a running InfraPortal instance into scripts/tutorial/snapshot/ so
    seed-tutorial.ps1 can rebuild it locally.

.DESCRIPTION
    Reads through the public API only (X-Api-Key), so it needs nothing but the two connection
    variables every InfraPilot task uses:

        DEPLOYMENTS_URL       base URL of the instance, e.g. https://infraportal.example.com
        DEPLOYMENTS_API_KEY   sent as X-Api-Key on every request

    What is exported — deliberately "what is live now", not the ledger:

        products.json              GET /api/deployments/products
        state/<product>.json       GET /api/deployments/state?product=…   (current version per env)
        recent/<product>.json      GET /api/deployments/recent/<product>  (last -HistoryDays days,
                                                                            capped at -HistoryLimit)
        promotions-pending.json    GET /api/promotions?status=Pending     (open candidates only)
        builds.json                GET /api/builds?limit=-BuildLimit      (newest registered builds)
        manifest.json              when, from where, how much

    The short history window exists so the analytics and timeline pages have something to draw;
    set -HistoryDays 0 to skip it and keep strictly the current matrix.

    The output contains real names, e-mail addresses and internal URLs. The directory is gitignored —
    keep it that way.

.PARAMETER OutputDir
    Where to write. Defaults to scripts/tutorial/snapshot next to this script.

.PARAMETER Products
    Only export these products (kebab-case, as the API reports them). Default: everything.

.PARAMETER HistoryDays
    Days of recent deploys to pull per product (default 7; 0 disables).

.PARAMETER HistoryLimit
    Cap on recent deploys per product (default 400).

.PARAMETER BuildLimit
    How many registered builds to pull (default 200, the API's page size).

.EXAMPLE
    .\scripts\tutorial\export-snapshot.ps1

.EXAMPLE
    .\scripts\tutorial\export-snapshot.ps1 -Products mpt,mpt-extensions -HistoryDays 3
#>
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot 'snapshot'),
    [string[]]$Products = @(),
    [int]$HistoryDays = 7,
    [int]$HistoryLimit = 400,
    [int]$BuildLimit = 200
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..' '_common.ps1')

# ── Connection ───────────────────────────────────────────────────────────────────────────────
$baseUrl = ($env:DEPLOYMENTS_URL ?? '').TrimEnd('/')
$apiKey  = $env:DEPLOYMENTS_API_KEY ?? ''
if (-not $baseUrl) { throw 'DEPLOYMENTS_URL is not set. Point it at the InfraPortal instance to copy (e.g. https://infraportal.example.com).' }
if (-not $apiKey)  { throw 'DEPLOYMENTS_API_KEY is not set. It is sent as X-Api-Key; ask the InfraPortal admin for a read-capable key.' }

$headers = @{ 'X-Api-Key' = $apiKey }

<#
    One GET against the source instance. Retries on 429 (the per-key limit is 120/min) and on the
    transient 5xx a serverless database produces while waking up. Returns the parsed body.
#>
function Get-Remote {
    param([Parameter(Mandatory)][string]$Path)
    $uri = "$baseUrl$Path"
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        $status = 0
        $body = Invoke-RestMethod -Uri $uri -Headers $headers -SkipHttpErrorCheck -StatusCodeVariable status -TimeoutSec 120
        if ($status -eq 200) { return $body }
        if ($status -eq 429 -or $status -ge 500) {
            $pause = 15 * $attempt
            Write-Note "HTTP $status from $Path — waiting ${pause}s (attempt $attempt/5)"
            Start-Sleep -Seconds $pause
            continue
        }
        throw "GET $Path returned HTTP $status"
    }
    throw "GET $Path kept failing after 5 attempts"
}

function Save-Json {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][AllowNull()]$Object)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    # Depth matters: a deploy event nests references → participants → … five levels deep, and
    # ConvertTo-Json silently flattens anything past its default depth of 2 into a string.
    ConvertTo-Json -InputObject $Object -Depth 40 | Set-Content -Path $Path -Encoding utf8
}

# ── Go ───────────────────────────────────────────────────────────────────────────────────────
Write-Step "Exporting from $baseUrl"
$health = Get-Remote '/health'
Write-Detail "health: $($health.status)"

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$productSummaries = @(Get-Remote '/api/deployments/products')
$selected = if ($Products.Count -gt 0) {
    $wanted = [System.Collections.Generic.HashSet[string]]::new([string[]]$Products, [System.StringComparer]::OrdinalIgnoreCase)
    @($productSummaries | Where-Object { $wanted.Contains($_.product) })
} else {
    $productSummaries
}
if ($selected.Count -eq 0) { throw "None of the requested products exist on $baseUrl. Available: $($productSummaries.product -join ', ')" }
Save-Json (Join-Path $OutputDir 'products.json') $selected
Write-Ok "products: $($selected.product -join ', ')"

$since = [DateTimeOffset]::UtcNow.AddDays(-$HistoryDays).ToString('yyyy-MM-ddTHH:mm:ssZ')
$stateCount = 0
$recentCount = 0
foreach ($summary in $selected) {
    $product = $summary.product
    Write-Step "  $product"

    $state = @(Get-Remote "/api/deployments/state?product=$([uri]::EscapeDataString($product))")
    Save-Json (Join-Path $OutputDir 'state' "$product.json") $state
    $stateCount += $state.Count
    Write-Detail "state: $($state.Count) current (service, environment) cells"

    if ($HistoryDays -gt 0) {
        $recent = @(Get-Remote "/api/deployments/recent/$([uri]::EscapeDataString($product))?since=$since&limit=$HistoryLimit")
        Save-Json (Join-Path $OutputDir 'recent' "$product.json") $recent
        $recentCount += $recent.Count
        Write-Detail "recent: $($recent.Count) deploys in the last $HistoryDays days"
    }
}

Write-Step 'Open promotions'
$promotions = Get-Remote '/api/promotions?status=Pending'
$pending = @($promotions.candidates | Where-Object { $selected.product -contains $_.product })
Save-Json (Join-Path $OutputDir 'promotions-pending.json') $pending
Write-Ok "$($pending.Count) pending candidates"

Write-Step 'Registered builds'
$builds = Get-Remote "/api/builds?limit=$BuildLimit"
$buildRows = @($builds.results | Where-Object { $selected.product -contains $_.product })
Save-Json (Join-Path $OutputDir 'builds.json') $buildRows
Write-Ok "$($buildRows.Count) builds"

Save-Json (Join-Path $OutputDir 'manifest.json') ([ordered]@{
    exportedAt   = [DateTimeOffset]::UtcNow.ToString('o')
    source       = $baseUrl
    products     = @($selected.product)
    historyDays  = $HistoryDays
    historyLimit = $HistoryLimit
    counts       = [ordered]@{
        stateCells        = $stateCount
        recentDeploys     = $recentCount
        pendingPromotions = $pending.Count
        builds            = $buildRows.Count
    }
})

Write-Host ''
Write-Ok "Snapshot written to $OutputDir"
Write-Note 'It contains production names and e-mail addresses — the directory is gitignored, keep it local.'
