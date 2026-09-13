$ErrorActionPreference = 'Stop'

$issuer = 'https://localhost:8443/realms/laundry-development'
$cloudScanEndpoint = 'https://localhost:7100/api/scans'
$clientId = 'gateway-development'

$composeJson = docker compose -f docker-compose.identity.yml config --format json
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the local identity configuration.' }
$compose = $composeJson | ConvertFrom-Json
$clientSecret = $compose.services.keycloak.environment.GATEWAY_CLIENT_SECRET
if ([string]::IsNullOrWhiteSpace($clientSecret)) { throw 'GATEWAY_CLIENT_SECRET is missing.' }

$metadata = Invoke-RestMethod "$issuer/.well-known/openid-configuration" -TimeoutSec 10
$tokenResponse = Invoke-RestMethod `
    -Method Post `
    -Uri $metadata.token_endpoint `
    -ContentType 'application/x-www-form-urlencoded' `
    -Body @{ grant_type = 'client_credentials'; client_id = $clientId; client_secret = $clientSecret } `
    -TimeoutSec 10
$authorization = @{ Authorization = "Bearer $($tokenResponse.access_token)" }

$scan = Get-Content packages/contracts/examples/scan-observed.v1.barcode.json -Raw | ConvertFrom-Json
$eventId = [guid]::NewGuid().ToString()
$scan.eventId = $eventId
$scan.correlationId = $eventId
$scan.observedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$scan.gatewayAcceptedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
$scanJson = $scan | ConvertTo-Json -Depth 5 -Compress

$missingToken = Invoke-WebRequest -Method Post -Uri $cloudScanEndpoint `
    -ContentType 'application/json' -Body $scanJson -SkipHttpErrorCheck -TimeoutSec 10
if ($missingToken.StatusCode -ne 401) { throw 'The cloud accepted a request without a gateway token.' }

$wrongPlant = $scanJson | ConvertFrom-Json
$wrongPlant.eventId = [guid]::NewGuid().ToString()
$wrongPlant.correlationId = $wrongPlant.eventId
$wrongPlant.plantId = [guid]::NewGuid().ToString()
$wrongPlantResponse = Invoke-WebRequest -Method Post -Uri $cloudScanEndpoint -Headers $authorization `
    -ContentType 'application/json' -Body ($wrongPlant | ConvertTo-Json -Depth 5 -Compress) `
    -SkipHttpErrorCheck -TimeoutSec 10
if ($wrongPlantResponse.StatusCode -ne 403) { throw 'The cloud did not reject a different plant.' }

$first = Invoke-WebRequest -Method Post -Uri $cloudScanEndpoint -Headers $authorization `
    -ContentType 'application/json' -Body $scanJson -SkipHttpErrorCheck -TimeoutSec 10
if ($first.StatusCode -ne 201) { throw "Expected first delivery status 201, received $($first.StatusCode)." }
$firstReceipt = $first.Content | ConvertFrom-Json
if ($firstReceipt.eventId -cne $eventId -or $firstReceipt.status -cne 'accepted') {
    throw 'Cloud returned an invalid first receipt.'
}

$retry = Invoke-WebRequest -Method Post -Uri $cloudScanEndpoint -Headers $authorization `
    -ContentType 'application/json' -Body $scanJson -SkipHttpErrorCheck -TimeoutSec 10
if ($retry.StatusCode -ne 200) { throw "Expected retry status 200, received $($retry.StatusCode)." }
$retryReceipt = $retry.Content | ConvertFrom-Json
if ($retryReceipt.status -cne 'alreadyProcessed' -or
    $retryReceipt.cloudReceivedAtUtc -cne $firstReceipt.cloudReceivedAtUtc) {
    throw 'Cloud retry receipt did not preserve the original acceptance.'
}

Write-Host 'Cloud gateway authentication verified over trusted HTTPS.'
Write-Host 'Missing credentials returned 401, wrong plant returned 403, and an authorized retry remained idempotent.'
Write-Host 'No token, credential, or scan identifier was printed.'
