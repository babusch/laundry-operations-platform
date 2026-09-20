$ErrorActionPreference = 'Stop'

$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$enrollment = $null
$sourceSession = $null
$scan = $null
$submission = $null

try {
    $enrollment = Invoke-RestMethod -Method Post `
        -Uri 'https://localhost:7200/api/development/source-enrollments' `
        -WebSession $session -TimeoutSec 10

    $exchangeBody = @{ enrollmentCode = $enrollment.enrollmentCode } | ConvertTo-Json -Compress
    $exchange = Invoke-WebRequest -Method Post `
        -Uri 'https://localhost:7200/api/source-enrollment/exchange' `
        -ContentType 'application/json' -Body $exchangeBody `
        -WebSession $session -TimeoutSec 10
    if ($exchange.StatusCode -ne 204) { throw 'The enrollment exchange did not complete.' }

    $sourceSession = Invoke-RestMethod -Uri 'https://localhost:7200/api/source-session' `
        -WebSession $session -TimeoutSec 10
    if ($sourceSession.permissions -notcontains 'scans.submit') {
        throw 'The enrolled browser source does not have scans.submit.'
    }

    $headers = @{ $sourceSession.antiforgeryHeaderName = $sourceSession.antiforgeryToken }
    $verification = Invoke-WebRequest -Method Post `
        -Uri 'https://localhost:7200/api/development/source-session/verify' `
        -Headers $headers -WebSession $session -TimeoutSec 10
    if ($verification.StatusCode -ne 204) { throw 'The antiforgery-protected session check failed.' }

    $repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    $scan = Get-Content (Join-Path $repositoryRoot 'packages/contracts/examples/submit-scan.v1.barcode.json') -Raw |
        ConvertFrom-Json
    $eventId = [guid]::NewGuid()
    $scan.eventId = $eventId.ToString()
    $scan.correlationId = $eventId.ToString()
    $scan.observedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $submission = $scan | ConvertTo-Json -Depth 5 -Compress

    $accepted = Invoke-RestMethod -Method Post -Uri 'https://localhost:7200/api/scans' `
        -ContentType 'application/json' -Body $submission -Headers $headers `
        -WebSession $session -TimeoutSec 10
    if ($accepted.status -cne 'acceptedLocally') {
        throw 'The authenticated source did not durably accept the synthetic scan.'
    }

    $retried = Invoke-RestMethod -Method Post -Uri 'https://localhost:7200/api/scans' `
        -ContentType 'application/json' -Body $submission -Headers $headers `
        -WebSession $session -TimeoutSec 10
    if ($retried.status -cne 'alreadyAcceptedLocally') {
        throw 'The authenticated retry did not return the original durable observation.'
    }

    Write-Host 'Development browser-source enrollment and protected scan acceptance verified.'
    Write-Host 'The helper did not print the one-time code, secure cookie, antiforgery token, tag value, or generated identifiers.'
}
finally {
    $accepted = $null
    $exchangeBody = $null
    $headers = $null
    $enrollment = $null
    $eventId = $null
    $retried = $null
    $scan = $null
    $sourceSession = $null
    $submission = $null
    $session = $null
}
