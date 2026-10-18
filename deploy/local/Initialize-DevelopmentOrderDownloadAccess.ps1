$ErrorActionPreference = 'Stop'
$runId = [Guid]::NewGuid().ToString('D')
$baseUrl = 'https://localhost:8443'
$realm = 'laundry-development'
$clientId = 'gateway-development'
$clientSecret = $null; $adminPassword = $null; $adminToken = $null; $headers = $null
$composeJson = $null; $compose = $null; $token = $null
$stage = 'local_configuration'
$administrativeChanges = 0

# Explicit local administrative operation, not startup/bootstrap or a production grant.
# Saved credentials are consumed internally; never emit config, HTTP bodies or tokens.
try {
    $composeJson = docker compose -f docker-compose.identity.yml config --format json
    if ($LASTEXITCODE -ne 0) { throw 'Local identity configuration unavailable.' }
    $compose = $composeJson | ConvertFrom-Json
    $adminPassword = $compose.services.keycloak.environment.KC_BOOTSTRAP_ADMIN_PASSWORD
    $clientSecret = $compose.services.keycloak.environment.GATEWAY_CLIENT_SECRET
    if ([string]::IsNullOrWhiteSpace($adminPassword) -or [string]::IsNullOrWhiteSpace($clientSecret)) { throw 'Local credentials unavailable.' }
    $stage = 'administrator_authentication'
    $adminToken = Invoke-RestMethod -Method Post -Uri "$baseUrl/realms/master/protocol/openid-connect/token" `
        -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 10 -Body @{
            grant_type = 'password'; client_id = 'admin-cli'; username = 'local-admin'; password = $adminPassword
        }
    $headers = @{ Authorization = "Bearer $($adminToken.access_token)" }
    $api = "$baseUrl/admin/realms/$realm"
    $stage = 'gateway_registration_checks'
    $clients = @(Invoke-RestMethod -Uri "$api/clients?clientId=$clientId" -Headers $headers -TimeoutSec 10 | ForEach-Object { $_ })
    $stage = 'gateway_client_flags'
    if ($clients.Count -ne 1 -or $clients[0].clientId -cne $clientId -or
        $clients[0].publicClient -ne $false -or $clients[0].fullScopeAllowed -ne $false -or
        $clients[0].serviceAccountsEnabled -ne $true -or $clients[0].enabled -ne $true) { throw 'Unexpected gateway client.' }
    $clientUri = "$api/clients/$($clients[0].id)"
    $stage = 'gateway_service_account'
    $account = Invoke-RestMethod -Uri "$clientUri/service-account-user" -Headers $headers -TimeoutSec 10
    if ($account.username -cne 'service-account-gateway-development' -or -not $account.enabled) { throw 'Unexpected service account.' }
    $mapperUri = "$clientUri/protocol-mappers/models"
    $all = @(Invoke-RestMethod -Uri $mapperUri -Headers $headers -TimeoutSec 10 | ForEach-Object { $_ })
    $stage = 'registered_tenant_plant_claims'
    foreach ($registration in @(@('registered-tenant','tenant_id','11111111-1111-4111-8111-111111111111'),
        @('registered-plant','plant_id','22222222-2222-4222-8222-222222222222'))) {
        $match = @($all | Where-Object name -CEQ $registration[0])
        if ($match.Count -ne 1 -or $match[0].config.'claim.name' -cne $registration[1] -or
            $match[0].config.'claim.value' -cne $registration[2] -or $match[0].config.'access.token.claim' -cne 'true') {
            throw 'Gateway registration mismatch.'
        }
    }
    $stage = 'existing_permission_mapper'
    $mapper = @($all | Where-Object name -CEQ 'application-permissions')
    if ($mapper.Count -ne 1 -or $mapper[0].config.'claim.name' -cne 'laundry_permissions' -or
        $mapper[0].protocolMapper -notin @('oidc-hardcoded-claim-mapper','oidc-usermodel-realm-role-mapper')) { throw 'Unexpected permission mapper.' }
    if ($mapper[0].protocolMapper -ceq 'oidc-hardcoded-claim-mapper' -and $mapper[0].config.'claim.value' -cne 'scans.ingest') {
        throw 'Unexpected baseline permission claim.'
    }
    $stage = 'existing_role_checks'
    $scopeRoles = @(Invoke-RestMethod -Uri "$clientUri/scope-mappings/realm" -Headers $headers -TimeoutSec 10 | ForEach-Object { $_ })
    $accountRolesUri = "$api/users/$($account.id)/role-mappings/realm"
    $accountRoles = @(Invoke-RestMethod -Uri $accountRolesUri -Headers $headers -TimeoutSec 10 | ForEach-Object { $_ })
    foreach ($roles in @($scopeRoles, $accountRoles)) {
        if (@($roles | Where-Object { $_.name -match '\.' -and $_.name -notin @('scans.ingest','orders.download') }).Count -ne 0 -or
            'scans.ingest' -notin @($roles | ForEach-Object name)) { throw 'Unexpected existing role scope.' }
    }
    # Enable standard Keycloak administrator evidence before creating/changing access.
    $stage = 'administrator_audit_setup'
    $realmState = Invoke-RestMethod -Uri $api -Headers $headers -TimeoutSec 10
    if (-not $realmState.adminEventsEnabled) {
        Invoke-RestMethod -Method Put -Uri $api -Headers $headers -ContentType 'application/json' `
            -Body '{"adminEventsEnabled":true}' -TimeoutSec 10 | Out-Null
        $administrativeChanges++
        $realmState = Invoke-RestMethod -Uri $api -Headers $headers -TimeoutSec 10
        if (-not $realmState.adminEventsEnabled) { throw 'Administrator audit logging is not enabled.' }
    }
    $stage = 'download_role_setup'
    try { $role = Invoke-RestMethod -Uri "$api/roles/orders.download" -Headers $headers -TimeoutSec 10 }
    catch {
        if ($null -eq $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
        Invoke-RestMethod -Method Post -Uri "$api/roles" -Headers $headers -ContentType 'application/json' `
            -Body '{"name":"orders.download","description":"Read published order plans for the registered plant"}' -TimeoutSec 10 | Out-Null
        $administrativeChanges++
        $role = Invoke-RestMethod -Uri "$api/roles/orders.download" -Headers $headers -TimeoutSec 10
    }
    if ($role.name -cne 'orders.download' -or $role.composite -eq $true) { throw 'Unexpected download role.' }
    $roleBody = ConvertTo-Json -InputObject @($role) -Depth 5
    $stage = 'download_role_grants'
    if ('orders.download' -notin @($scopeRoles | ForEach-Object name)) {
        Invoke-RestMethod -Method Post -Uri "$clientUri/scope-mappings/realm" -Headers $headers -ContentType 'application/json' -Body $roleBody -TimeoutSec 10 | Out-Null
        $administrativeChanges++
    }
    if ('orders.download' -notin @($accountRoles | ForEach-Object name)) {
        Invoke-RestMethod -Method Post -Uri $accountRolesUri -Headers $headers -ContentType 'application/json' -Body $roleBody -TimeoutSec 10 | Out-Null
        $administrativeChanges++
    }
    $stage = 'permission_mapper_setup'
    $desired = [ordered]@{
        id = $mapper[0].id; name = 'application-permissions'; protocol = 'openid-connect'
        protocolMapper = 'oidc-usermodel-realm-role-mapper'; consentRequired = $false
        config = [ordered]@{
            'claim.name' = 'laundry_permissions'; 'jsonType.label' = 'String'; 'multivalued' = 'true'
            'usermodel.realmRoleMapping.rolePrefix' = ''; 'access.token.claim' = 'true'
            'id.token.claim' = 'false'; 'userinfo.token.claim' = 'false'; 'introspection.token.claim' = 'true'
        }
    }
    $needsMapperUpdate = $mapper[0].protocolMapper -cne $desired.protocolMapper
    foreach ($key in $desired.config.Keys) {
        # Keycloak omits an empty optional prefix when returning mapper configuration.
        if ($key -ceq 'usermodel.realmRoleMapping.rolePrefix' -and [string]::IsNullOrEmpty([string]$mapper[0].config.$key)) { continue }
        if ($mapper[0].config.$key -cne $desired.config[$key]) { $needsMapperUpdate = $true }
    }
    if ($needsMapperUpdate) {
        Invoke-RestMethod -Method Put -Uri "$mapperUri/$($mapper[0].id)" -Headers $headers -ContentType 'application/json' `
            -Body ($desired | ConvertTo-Json -Depth 5) -TimeoutSec 10 | Out-Null
        $administrativeChanges++
    }
    $stage = 'gateway_token_verification'
    $token = Invoke-RestMethod -Method Post -Uri "$baseUrl/realms/$realm/protocol/openid-connect/token" `
        -ContentType 'application/x-www-form-urlencoded' -TimeoutSec 10 -Body @{
            grant_type = 'client_credentials'; client_id = $clientId; client_secret = $clientSecret
        }
    $parts = $token.access_token.Split('.')
    if ($parts.Count -ne 3) { throw 'Invalid gateway token.' }
    $encoded = $parts[1].Replace('-','+').Replace('_','/')
    $encoded += '=' * ((4 - ($encoded.Length % 4)) % 4)
    $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded)) | ConvertFrom-Json
    $permissions = @($claims.laundry_permissions | Sort-Object)
    if (($permissions -join ',') -cne 'orders.download,scans.ingest' -or
        $claims.tenant_id -cne '11111111-1111-4111-8111-111111111111' -or
        $claims.plant_id -cne '22222222-2222-4222-8222-222222222222' -or 'laundry-cloud-api' -notin @($claims.aud)) {
        throw 'Gateway token scope verification failed.'
    }
    $stage = 'administrator_audit_verification'
    $audit = @(Invoke-RestMethod -Uri "$api/admin-events?max=100" -Headers $headers -TimeoutSec 10 | ForEach-Object { $_ })
    if (-not @($audit | Where-Object { $_.resourcePath -like "clients/$($clients[0].id)/*" -or
        $_.resourcePath -like "users/$($account.id)/*" }).Count) { throw 'Administrator audit evidence unavailable.' }
    Write-Host "Download access setup $runId verified: administrativeChanges=$administrativeChanges; scans.ingest preserved; orders.download scoped to the existing development gateway. Administrator audit evidence is present."
    Write-Host 'No credentials, tokens or internal account identifiers were printed. No cloud orders were changed.'
} catch {
    $status = 'no_http_status'
    if ($null -ne $_.Exception.Response -and $null -ne $_.Exception.Response.StatusCode) { $status = [string][int]$_.Exception.Response.StatusCode }
    throw "Download access setup $runId stopped at $stage ($status). Some administrative steps may have committed; rerunning is safe. No credential/error body was printed."
} finally {
    $clientSecret = $null; $adminPassword = $null; $adminToken = $null; $headers = $null
    $composeJson = $null; $compose = $null; $token = $null
}
