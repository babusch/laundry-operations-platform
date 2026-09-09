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

Write-Host 'Development gateway role scope is configured.'
Write-Host 'No administrator token or credential was printed or stored.'
