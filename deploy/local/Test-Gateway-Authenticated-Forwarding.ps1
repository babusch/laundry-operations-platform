$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$scan = Get-Content (Join-Path $repositoryRoot 'packages/contracts/examples/submit-scan.v1.barcode.json') -Raw |
    ConvertFrom-Json
$eventId = [guid]::NewGuid()
$scan.eventId = $eventId.ToString()
$scan.correlationId = $eventId.ToString()
$scan.observedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$submission = $scan | ConvertTo-Json -Depth 5 -Compress

$accepted = Invoke-RestMethod -Method Post -Uri 'http://localhost:5200/api/scans' `
    -ContentType 'application/json' -Body $submission -TimeoutSec 10
if ($accepted.status -cne 'acceptedLocally') { throw 'The gateway did not durably accept the synthetic scan.' }

$deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
do {
    Start-Sleep -Milliseconds 250
    $diagnostic = Invoke-RestMethod -Uri "http://localhost:5200/api/sync/events/$eventId" -TimeoutSec 5
} while ($diagnostic.status -eq 'pending' -and [DateTimeOffset]::UtcNow -lt $deadline)

if ($diagnostic.status -ne 'synchronized') {
    throw "Gateway delivery ended in state $($diagnostic.status) with safe error $($diagnostic.lastError)."
}

Write-Host 'Authenticated gateway forwarding verified.'
Write-Host 'The scan was accepted locally, a Keycloak token was acquired, and the cloud receipt changed it to synchronized.'
Write-Host 'No credential, token, or scan identifier was printed.'
