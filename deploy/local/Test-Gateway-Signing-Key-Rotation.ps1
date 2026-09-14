$ErrorActionPreference = 'Stop'

$baseUrl = 'https://localhost:8443'
$realmName = 'laundry-development'
$issuer = "$baseUrl/realms/$realmName"
$cloudScanEndpoint = 'https://localhost:7100/api/scans'
$clientId = 'gateway-development'
$keyProviderType = 'org.keycloak.keys.KeyProvider'
$rotationName = "rsa-generated-rotation-test-$([guid]::NewGuid().ToString('N'))"
$createdComponentId = $null

function Get-JwtKeyId([string]$token) {
    $parts = $token.Split('.')
    if ($parts.Count -ne 3) { throw 'Access token is not a compact signed JWT.' }
    $header = $parts[0].Replace('-', '+').Replace('_', '/')
    $header += '=' * ((4 - ($header.Length % 4)) % 4)
    $decoded = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($header)) |
        ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($decoded.kid)) { throw 'Access token has no signing-key ID.' }
    return $decoded.kid
}

function Get-GatewayToken([string]$tokenEndpoint, [string]$clientSecret) {
    return (Invoke-RestMethod -Method Post -Uri $tokenEndpoint `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body @{ grant_type = 'client_credentials'; client_id = $clientId; client_secret = $clientSecret } `
        -TimeoutSec 10).access_token
}

function New-ScanJson {
    $scan = Get-Content packages/contracts/examples/scan-observed.v1.barcode.json -Raw |
        ConvertFrom-Json
    $eventId = [guid]::NewGuid().ToString()
    $scan.eventId = $eventId
    $scan.correlationId = $eventId
    $scan.observedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    $scan.gatewayAcceptedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
    return $scan | ConvertTo-Json -Depth 5 -Compress
}

$composeJson = docker compose -f docker-compose.identity.yml config --format json
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the local identity configuration.' }
$compose = $composeJson | ConvertFrom-Json
$adminPassword = $compose.services.keycloak.environment.KC_BOOTSTRAP_ADMIN_PASSWORD
$clientSecret = $compose.services.keycloak.environment.GATEWAY_CLIENT_SECRET
if ([string]::IsNullOrWhiteSpace($adminPassword) -or [string]::IsNullOrWhiteSpace($clientSecret)) {
    throw 'Required local identity credentials are missing.'
}

$metadata = Invoke-RestMethod "$issuer/.well-known/openid-configuration" -TimeoutSec 10
$adminToken = Invoke-RestMethod -Method Post `
    -Uri "$baseUrl/realms/master/protocol/openid-connect/token" `
    -ContentType 'application/x-www-form-urlencoded' `
    -Body @{
        grant_type = 'password'
        client_id = 'admin-cli'
        username = 'local-admin'
        password = $adminPassword
    } `
    -TimeoutSec 10
$adminHeaders = @{ Authorization = "Bearer $($adminToken.access_token)" }
$realm = Invoke-RestMethod -Uri "$baseUrl/admin/realms/$realmName" `
    -Headers $adminHeaders -TimeoutSec 10

$previousToken = Get-GatewayToken $metadata.token_endpoint $clientSecret
$previousKeyId = Get-JwtKeyId $previousToken
$previousAuthorization = @{ Authorization = "Bearer $previousToken" }

# Force the running cloud API to cache the pre-rotation discovery metadata. The
# deliberately invalid body is authenticated but cannot create a scan record.
$warmup = Invoke-WebRequest -Method Post -Uri $cloudScanEndpoint `
    -Headers $previousAuthorization -ContentType 'application/json' -Body '{}' `
    -SkipHttpErrorCheck -TimeoutSec 10
if ($warmup.StatusCode -ne 400) {
    throw "Expected authenticated metadata warm-up status 400, received $($warmup.StatusCode)."
}

try {
    $component = [ordered]@{
        name = $rotationName
        providerId = 'rsa-generated'
        providerType = $keyProviderType
        parentId = $realm.id
        config = [ordered]@{
            priority = @('200')
            enabled = @('true')
            active = @('true')
            algorithm = @('RS256')
            keySize = @('2048')
        }
    }
    Invoke-RestMethod -Method Post -Uri "$baseUrl/admin/realms/$realmName/components" `
        -Headers $adminHeaders -ContentType 'application/json' `
        -Body ($component | ConvertTo-Json -Depth 5) -TimeoutSec 10 | Out-Null

    $type = [uri]::EscapeDataString($keyProviderType)
    $components = Invoke-RestMethod `
        -Uri "$baseUrl/admin/realms/$realmName/components?parent=$($realm.id)&type=$type" `
        -Headers $adminHeaders -TimeoutSec 10
    $created = @($components | Where-Object { $_.name -ceq $rotationName })
    if ($created.Count -ne 1) { throw 'Could not uniquely identify the temporary signing provider.' }
    $createdComponentId = $created[0].id

    $currentToken = $null
    $currentKeyId = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $currentToken = Get-GatewayToken $metadata.token_endpoint $clientSecret
        $currentKeyId = Get-JwtKeyId $currentToken
        if ($currentKeyId -cne $previousKeyId) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($currentKeyId -ceq $previousKeyId) { throw 'Keycloak did not activate the temporary signing key.' }

    $jwks = Invoke-RestMethod $metadata.jwks_uri -TimeoutSec 10
    $publishedKeyIds = @($jwks.keys | ForEach-Object { $_.kid })
    if ($previousKeyId -notin $publishedKeyIds -or $currentKeyId -notin $publishedKeyIds) {
        throw 'Keycloak did not publish both the previous and current signing keys.'
    }

    $currentAuthorization = @{ Authorization = "Bearer $currentToken" }
    $currentScan = New-ScanJson
    $currentResponse = Invoke-WebRequest -Method Post -Uri $cloudScanEndpoint `
        -Headers $currentAuthorization -ContentType 'application/json' -Body $currentScan `
        -SkipHttpErrorCheck -TimeoutSec 10
    if ($currentResponse.StatusCode -eq 401) {
        # The unknown key requested a metadata refresh. Retry the unchanged event,
        # just as the durable gateway sender does after a transient 401.
        $currentResponse = Invoke-WebRequest -Method Post -Uri $cloudScanEndpoint `
            -Headers $currentAuthorization -ContentType 'application/json' -Body $currentScan `
            -SkipHttpErrorCheck -TimeoutSec 10
    }
    if ($currentResponse.StatusCode -ne 201) {
        throw "Expected rotated-key delivery status 201, received $($currentResponse.StatusCode)."
    }

    $previousResponse = Invoke-WebRequest -Method Post -Uri $cloudScanEndpoint `
        -Headers $previousAuthorization -ContentType 'application/json' -Body (New-ScanJson) `
        -SkipHttpErrorCheck -TimeoutSec 10
    if ($previousResponse.StatusCode -ne 201) {
        throw "Expected passive-key delivery status 201, received $($previousResponse.StatusCode)."
    }

    Write-Host 'Development gateway signing-key rotation verified.'
    Write-Host 'The running cloud refreshed metadata, accepted the new key, and continued accepting the previous passive key.'
    Write-Host 'No credential, token, signing-key ID, or scan identifier was printed.'
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($createdComponentId)) {
        Invoke-RestMethod -Method Delete `
            -Uri "$baseUrl/admin/realms/$realmName/components/$createdComponentId" `
            -Headers $adminHeaders -TimeoutSec 10 | Out-Null

        $restored = $false
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            $restoredToken = Get-GatewayToken $metadata.token_endpoint $clientSecret
            if ((Get-JwtKeyId $restoredToken) -ceq $previousKeyId) {
                $restored = $true
                break
            }
            Start-Sleep -Milliseconds 250
        }
        if (-not $restored) { throw 'Temporary signing provider was removed, but the original key did not reactivate.' }
        Write-Host 'The temporary signing provider was removed and the original development key was restored.'
    }
}
