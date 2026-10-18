param([switch]$WithOrderDownloads)
$ErrorActionPreference = 'Stop'

$issuer = 'https://localhost:8443/realms/laundry-development'
$clientId = 'gateway-development'
$expectedAudience = 'laundry-cloud-api'
$expectedTenant = '11111111-1111-4111-8111-111111111111'
$expectedPlant = '22222222-2222-4222-8222-222222222222'

$composeJson = docker compose -f docker-compose.identity.yml config --format json
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the local identity configuration.' }
$compose = $composeJson | ConvertFrom-Json
$clientSecret = $compose.services.keycloak.environment.GATEWAY_CLIENT_SECRET
if ([string]::IsNullOrWhiteSpace($clientSecret)) { throw 'GATEWAY_CLIENT_SECRET is missing.' }

$metadata = Invoke-RestMethod "$issuer/.well-known/openid-configuration" -TimeoutSec 10
if ($metadata.issuer -cne $issuer) { throw 'Unexpected identity issuer.' }

$rejected = Invoke-WebRequest `
    -Method Post `
    -Uri $metadata.token_endpoint `
    -ContentType 'application/x-www-form-urlencoded' `
    -Body @{ grant_type = 'client_credentials'; client_id = $clientId; client_secret = 'deliberately-invalid' } `
    -SkipHttpErrorCheck `
    -TimeoutSec 10
if ($rejected.StatusCode -ne 401) { throw 'Invalid gateway credentials were not rejected.' }

$tokenResponse = Invoke-RestMethod `
    -Method Post `
    -Uri $metadata.token_endpoint `
    -ContentType 'application/x-www-form-urlencoded' `
    -Body @{ grant_type = 'client_credentials'; client_id = $clientId; client_secret = $clientSecret } `
    -TimeoutSec 10

if ($tokenResponse.token_type -cne 'Bearer' -or [string]::IsNullOrWhiteSpace($tokenResponse.access_token)) {
    throw 'Keycloak did not return a bearer access token.'
}
if ($null -ne $tokenResponse.refresh_token) { throw 'Gateway client credentials must not receive a refresh token.' }

$parts = $tokenResponse.access_token.Split('.')
if ($parts.Count -ne 3) { throw 'Access token is not a compact signed JWT.' }
$payload = $parts[1].Replace('-', '+').Replace('_', '/')
$payload += '=' * ((4 - ($payload.Length % 4)) % 4)
$claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json

if ($claims.iss -cne $issuer) { throw 'Token issuer is incorrect.' }
if ($claims.azp -cne $clientId -and $claims.client_id -cne $clientId) { throw 'Token client identity is incorrect.' }
if ($claims.tenant_id -cne $expectedTenant -or $claims.plant_id -cne $expectedPlant) { throw 'Token plant registration is incorrect.' }
if ($expectedAudience -notin @($claims.aud)) { throw 'Token is not intended for the cloud API.' }
if ('scans.ingest' -notin @($claims.realm_access.roles)) { throw 'Gateway lacks scans.ingest permission.' }
$permissions = @($claims.laundry_permissions)
if ('scans.ingest' -notin $permissions -or ($WithOrderDownloads -and 'orders.download' -notin $permissions)) {
    throw 'Gateway permission claim is incorrect.'
}
if (@($permissions | Where-Object { $_ -notin @('scans.ingest', 'orders.download') }).Count -ne 0 -or
    @($permissions | Select-Object -Unique).Count -ne $permissions.Count) { throw 'Unexpected gateway application permissions.' }
$applicationRoles = @($claims.realm_access.roles | Where-Object { $_ -match '\.' })
if (@(Compare-Object ($permissions | Sort-Object) ($applicationRoles | Sort-Object)).Count -ne 0) {
    throw 'Gateway has an unexpected application permission.'
}

$now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
if ($claims.exp -le $now -or ($claims.exp - $now) -gt 300) { throw 'Token lifetime is invalid.' }

Write-Host 'Development gateway identity verified.'
Write-Host 'Invalid credentials are rejected; the valid token is short-lived, has only the approved application permissions, and is registered to the synthetic tenant/plant.'
Write-Host 'No token or credential was printed or stored.'
