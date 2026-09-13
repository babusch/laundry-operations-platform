$ErrorActionPreference = 'Stop'

$baseUrl = 'https://localhost:8443'
$realm = 'laundry-development'
$clientId = 'gateway-development'
$roleName = 'scans.ingest'

$composeJson = docker compose -f docker-compose.identity.yml config --format json
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the local identity configuration.' }
$compose = $composeJson | ConvertFrom-Json
$adminPassword = $compose.services.keycloak.environment.KC_BOOTSTRAP_ADMIN_PASSWORD
if ([string]::IsNullOrWhiteSpace($adminPassword)) { throw 'IDENTITY_ADMIN_PASSWORD is missing.' }

$adminToken = Invoke-RestMethod `
    -Method Post `
    -Uri "$baseUrl/realms/master/protocol/openid-connect/token" `
    -ContentType 'application/x-www-form-urlencoded' `
    -Body @{
        grant_type = 'password'
        client_id = 'admin-cli'
        username = 'local-admin'
        password = $adminPassword
    } `
    -TimeoutSec 10

$headers = @{ Authorization = "Bearer $($adminToken.access_token)" }
$clients = @(Invoke-RestMethod `
    -Method Get `
    -Uri "$baseUrl/admin/realms/$realm/clients?clientId=$clientId" `
    -Headers $headers `
    -TimeoutSec 10)
if ($clients.Count -ne 1 -or $clients[0].clientId -cne $clientId) {
    throw 'Expected exactly one development gateway client.'
}

$role = Invoke-RestMethod `
    -Method Get `
    -Uri "$baseUrl/admin/realms/$realm/roles/$roleName" `
    -Headers $headers `
    -TimeoutSec 10

$roleBody = ConvertTo-Json -InputObject @($role) -Depth 5
Invoke-RestMethod `
    -Method Post `
    -Uri "$baseUrl/admin/realms/$realm/clients/$($clients[0].id)/scope-mappings/realm" `
    -Headers $headers `
    -ContentType 'application/json' `
    -Body $roleBody `
    -TimeoutSec 10 | Out-Null

function Set-HardcodedAccessTokenClaim([string]$mapperName, [string]$claimName, [string]$claimValue) {
    $mapper = [ordered]@{
        name = $mapperName
        protocol = 'openid-connect'
        protocolMapper = 'oidc-hardcoded-claim-mapper'
        consentRequired = $false
        config = [ordered]@{
            'claim.name' = $claimName
            'claim.value' = $claimValue
            'jsonType.label' = 'String'
            'access.token.claim' = 'true'
            'id.token.claim' = 'false'
            'userinfo.token.claim' = 'false'
            'introspection.token.claim' = 'true'
        }
    }
    $mappersUri = "$baseUrl/admin/realms/$realm/clients/$($clients[0].id)/protocol-mappers/models"
    $allMappers = Invoke-RestMethod -Method Get -Uri $mappersUri -Headers $headers -TimeoutSec 10
    $existing = @($allMappers | Where-Object { $_.name -ceq $mapperName })
    if ($existing.Count -gt 1) { throw "Duplicate protocol mapper: $mapperName" }
    if ($existing.Count -eq 1) {
        $mapper.id = $existing[0].id
        Invoke-RestMethod -Method Put -Uri "$mappersUri/$($existing[0].id)" -Headers $headers `
            -ContentType 'application/json' -Body ($mapper | ConvertTo-Json -Depth 5) -TimeoutSec 10 | Out-Null
    } else {
        Invoke-RestMethod -Method Post -Uri $mappersUri -Headers $headers `
            -ContentType 'application/json' -Body ($mapper | ConvertTo-Json -Depth 5) -TimeoutSec 10 | Out-Null
    }
}

Set-HardcodedAccessTokenClaim 'registered-tenant' 'tenant_id' '11111111-1111-4111-8111-111111111111'
Set-HardcodedAccessTokenClaim 'registered-plant' 'plant_id' '22222222-2222-4222-8222-222222222222'
Set-HardcodedAccessTokenClaim 'application-permissions' 'laundry_permissions' 'scans.ingest'

Write-Host 'Development gateway role scope is configured.'
Write-Host 'No administrator token or credential was printed or stored.'
