$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$scan = Get-Content (Join-Path $repositoryRoot 'packages/contracts/examples/submit-scan.v2.barcode.json') -Raw |
    ConvertFrom-Json
$eventId = [guid]::NewGuid()
$scan.eventId = $eventId.ToString()
$scan.correlationId = $eventId.ToString()
$scan.observedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$submission = $scan | ConvertTo-Json -Depth 5 -Compress

$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$enrollment = $null
$sourceSession = $null

try {
    $enrollment = Invoke-RestMethod -Method Post `
        -Uri 'https://localhost:7200/api/development/source-enrollments' `
        -WebSession $session -TimeoutSec 10
    $exchangeBody = @{ enrollmentCode = $enrollment.enrollmentCode } | ConvertTo-Json -Compress
    $exchange = Invoke-WebRequest -Method Post `
        -Uri 'https://localhost:7200/api/source-enrollment/exchange' `
        -ContentType 'application/json' -Body $exchangeBody `
        -WebSession $session -TimeoutSec 10
    if ($exchange.StatusCode -ne 204) { throw 'The source enrollment exchange did not complete.' }

    $sourceSession = Invoke-RestMethod -Uri 'https://localhost:7200/api/source-session' `
        -WebSession $session -TimeoutSec 10
    $headers = @{ $sourceSession.antiforgeryHeaderName = $sourceSession.antiforgeryToken }
    $accepted = Invoke-RestMethod -Method Post -Uri 'https://localhost:7200/api/scans' `
        -ContentType 'application/json' -Body $submission -Headers $headers `
        -WebSession $session -TimeoutSec 10
    if ($accepted.status -cne 'acceptedLocally') { throw 'The gateway did not durably accept the synthetic scan.' }

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $diagnostic = Invoke-RestMethod -Uri "https://localhost:7200/api/sync/events/$eventId" -TimeoutSec 5
    } while ($diagnostic.status -eq 'pending' -and [DateTimeOffset]::UtcNow -lt $deadline)

    if ($diagnostic.status -ne 'synchronized') {
        throw "Gateway delivery ended in state $($diagnostic.status) with safe error $($diagnostic.lastError)."
    }

    Write-Host 'Authenticated gateway forwarding verified.'
    Write-Host 'The source-authenticated scan was accepted locally, a Keycloak token was acquired, and the cloud receipt changed it to synchronized.'
    Write-Host 'No credential, token, or scan identifier was printed.'
}
finally {
    $exchangeBody = $null
    $headers = $null
    $enrollment = $null
    $sourceSession = $null
    $session = $null
}
