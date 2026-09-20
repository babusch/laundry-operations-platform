$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryRoot `
    "laundry-source-resilience-$([guid]::NewGuid().ToString('N'))"))
if (-not $temporaryDirectory.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The temporary verification directory is outside the system temporary directory.'
}
[void](New-Item -ItemType Directory -Path $temporaryDirectory)

$gateway = $null
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
$enrollment = $null
$sourceSession = $null
$submission = $null

function Start-TestGateway {
    $standardOutput = Join-Path $temporaryDirectory 'gateway.stdout.log'
    $standardError = Join-Path $temporaryDirectory 'gateway.stderr.log'
    $process = Start-Process -FilePath 'dotnet' `
        -ArgumentList @('run', '--project', 'apps/edge/Laundry.Edge', '--no-build') `
        -WorkingDirectory $repositoryRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput $standardOutput -RedirectStandardError $standardError

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw 'The Development gateway exited before becoming ready.' }
        try {
            if ((Invoke-WebRequest -Uri 'https://localhost:7200/health/ready' `
                    -TimeoutSec 2).StatusCode -eq 200) { return $process }
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    throw 'The Development gateway did not become ready within 30 seconds.'
}

function Stop-TestGateway([Diagnostics.Process]$Process) {
    if ($null -ne $Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id
        [void]$Process.WaitForExit(10000)
    }
}

try {
    if (Get-NetTCPConnection -LocalPort 7200 -State Listen -ErrorAction SilentlyContinue) {
        throw 'Port 7200 is already in use. Stop the running gateway before this verification.'
    }

    $gateway = Start-TestGateway
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
    $scan = Get-Content (Join-Path $repositoryRoot `
        'packages/contracts/examples/submit-scan.v2.barcode.json') -Raw | ConvertFrom-Json
    $eventId = [guid]::NewGuid()
    $scan.eventId = $eventId.ToString()
    $scan.correlationId = $eventId.ToString()
    $scan.observedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $submission = $scan | ConvertTo-Json -Depth 5 -Compress
    $accepted = Invoke-RestMethod -Method Post -Uri 'https://localhost:7200/api/scans' `
        -ContentType 'application/json' -Body $submission -Headers $headers `
        -WebSession $session -TimeoutSec 10
    if ($accepted.status -cne 'acceptedLocally') { throw 'The first scan was not durably accepted.' }

    Stop-TestGateway $gateway
    $gateway = Start-TestGateway
    $sourceSession = Invoke-RestMethod -Uri 'https://localhost:7200/api/source-session' `
        -WebSession $session -TimeoutSec 10
    $headers = @{ $sourceSession.antiforgeryHeaderName = $sourceSession.antiforgeryToken }
    $retried = Invoke-RestMethod -Method Post -Uri 'https://localhost:7200/api/scans' `
        -ContentType 'application/json' -Body $submission -Headers $headers `
        -WebSession $session -TimeoutSec 10
    if ($retried.status -cne 'alreadyAcceptedLocally') {
        throw 'The unchanged scan was not recognized after gateway restart.'
    }

    $sourceId = [guid]$enrollment.sourceId
    $updateSql = "UPDATE plant.trusted_sources SET status = 'Revoked', " +
        "status_changed_at_utc = CURRENT_TIMESTAMP, configuration_version = configuration_version + 1 " +
        "WHERE source_id = '$sourceId';"
    $updateOutput = & docker compose exec -T plant-postgres psql `
        --username laundry_edge --dbname laundry_plant --command $updateSql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Could not revoke the synthetic source: $updateOutput" }

    $rejectedScan = Get-Content (Join-Path $repositoryRoot `
        'packages/contracts/examples/submit-scan.v2.barcode.json') -Raw | ConvertFrom-Json
    $rejectedEventId = [guid]::NewGuid()
    $rejectedScan.eventId = $rejectedEventId.ToString()
    $rejectedScan.correlationId = $rejectedEventId.ToString()
    $rejectedScan.observedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $rejectedSubmission = $rejectedScan | ConvertTo-Json -Depth 5 -Compress
    $rejected = Invoke-WebRequest -Method Post -Uri 'https://localhost:7200/api/scans' `
        -ContentType 'application/json' -Body $rejectedSubmission -Headers $headers `
        -WebSession $session -TimeoutSec 10 -SkipHttpErrorCheck
    if ($rejected.StatusCode -ne 401) { throw 'The locally revoked source was not rejected.' }

    $countSql = "SELECT count(*) FROM plant.observations WHERE event_id = '$rejectedEventId';"
    $countOutput = & docker compose exec -T plant-postgres psql --tuples-only --no-align `
        --username laundry_edge --dbname laundry_plant --command $countSql 2>&1
    if ($LASTEXITCODE -ne 0 -or $countOutput.Trim() -ne '0') {
        throw 'The rejected observation was unexpectedly stored.'
    }

    Write-Host 'Gateway restart, credential persistence, idempotent retry, and local revocation verified.'
    Write-Host 'Cloud forwarding was disabled; no cloud or identity service was required for local acceptance.'
    Write-Host 'The helper did not print credential material, tag values, or generated identifiers.'
}
finally {
    Stop-TestGateway $gateway
    $accepted = $null
    $enrollment = $null
    $eventId = $null
    $exchangeBody = $null
    $headers = $null
    $rejected = $null
    $rejectedEventId = $null
    $rejectedScan = $null
    $rejectedSubmission = $null
    $retried = $null
    $scan = $null
    $session = $null
    $sourceId = $null
    $sourceSession = $null
    $submission = $null
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}
