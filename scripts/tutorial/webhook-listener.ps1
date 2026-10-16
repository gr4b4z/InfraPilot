#Requires -Version 7
<#
.SYNOPSIS
    A terminal that shows InfraPortal webhooks arriving — the receiving end of the subscription
    seed-tutorial.ps1 creates.

.DESCRIPTION
    Listens on http://localhost:8787/ and prints every POST it gets: the event type, the InfraPortal
    signature header, and the JSON body. Nothing is stored. Run it in a visible terminal before
    approving or deploying anything on stage, and the audience sees the notification land a second
    after the click.

    The seeded subscription targets http://localhost:8787/infraportal; any path works.

.PARAMETER Port
    Port to listen on (default 8787). Pass the same value to seed-tutorial.ps1 -WebhookUrl if changed.

.EXAMPLE
    .\scripts\tutorial\webhook-listener.ps1
#>
[CmdletBinding()]
param(
    [int]$Port = 8787
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
$listener.Start()
Write-Host "Listening for InfraPortal webhooks on http://localhost:$Port/  (Ctrl+C to stop)" -ForegroundColor Cyan

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        $request = $context.Request
        $body = ''
        if ($request.HasEntityBody) {
            $reader = [System.IO.StreamReader]::new($request.InputStream, $request.ContentEncoding)
            try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }

        $eventType = $request.Headers['X-Webhook-Event'] ?? ''
        if (-not $eventType -and $body) {
            try { $eventType = (ConvertFrom-Json $body).eventType ?? (ConvertFrom-Json $body).event ?? '' } catch { }
        }

        Write-Host ''
        Write-Host ("{0:HH:mm:ss}  {1} {2}" -f (Get-Date), $request.HttpMethod, $request.Url.PathAndQuery) -ForegroundColor Green
        if ($eventType) { Write-Host "  event:      $eventType" -ForegroundColor Yellow }
        foreach ($name in $request.Headers.AllKeys) {
            if ($name -like 'X-*') { Write-Host "  ${name}: $($request.Headers[$name])" -ForegroundColor DarkGray }
        }
        if ($body) {
            try { $pretty = ($body | ConvertFrom-Json | ConvertTo-Json -Depth 20) } catch { $pretty = $body }
            Write-Host $pretty
        }

        $response = $context.Response
        $response.StatusCode = 200
        $bytes = [System.Text.Encoding]::UTF8.GetBytes('{"received":true}')
        $response.ContentType = 'application/json'
        $response.ContentLength64 = $bytes.Length
        $response.OutputStream.Write($bytes, 0, $bytes.Length)
        $response.Close()
    }
} finally {
    $listener.Stop()
    $listener.Close()
}
