$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
function Assert-True($condition, [string]$message) { if (-not $condition) { throw $message } }
function Reset-Fixture {
    $script:fake = @{
        writes = [Collections.Generic.List[string]]::new(); roles = @('scans.ingest'); scope = @('scans.ingest')
        enabled = $true; failMapper = $false; mapper = [pscustomobject]@{
            id='mapper'; name='application-permissions'; protocolMapper='oidc-hardcoded-claim-mapper'
            config=[pscustomobject]@{ 'claim.name'='laundry_permissions'; 'claim.value'='scans.ingest' }
        }
    }
}
function docker {
    $global:LASTEXITCODE = 0
    return '{"services":{"keycloak":{"environment":{"KC_BOOTSTRAP_ADMIN_PASSWORD":"synthetic-test-only","GATEWAY_CLIENT_SECRET":"synthetic-test-only"}}}}'
}
function Invoke-RestMethod {
    param($Uri, $Method='Get', $Headers, $TimeoutSec, $ContentType, $Body)
    $path = ([Uri]$Uri).AbsolutePath
    if ($Method -in @('Post','Put') -and $path -notlike '*/token') { $fake.writes.Add("$Method $path") }
    if ($path -like '*/realms/master/*/token') { return [pscustomobject]@{access_token='synthetic-admin-token'} }
    if ($path -like '*/realms/laundry-development/*/token') {
        $claims = @{laundry_permissions=@('scans.ingest','orders.download');tenant_id='11111111-1111-4111-8111-111111111111';plant_id='22222222-2222-4222-8222-222222222222';aud=@('laundry-cloud-api')}
        $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($claims | ConvertTo-Json -Compress))).TrimEnd('=').Replace('+','-').Replace('/','_')
        return [pscustomobject]@{access_token="header.$encoded.signature"}
    }
    if ($path -like '*/clients' -and $Method -eq 'Get') { return [pscustomobject]@{ id='client';clientId='gateway-development';publicClient=$false;fullScopeAllowed=$false;serviceAccountsEnabled=$true;enabled=$fake.enabled } }
    if ($path -like '*/service-account-user') { return [pscustomobject]@{id='account';username='service-account-gateway-development';enabled=$true} }
    if ($path -like '*/protocol-mappers/models' -and $Method -eq 'Get') {
        Write-Output -NoEnumerate @(
            [pscustomobject]@{ id='tenant-mapper';name='registered-tenant';config=[pscustomobject]@{'claim.name'='tenant_id';'claim.value'='11111111-1111-4111-8111-111111111111';'access.token.claim'='true'} },
            [pscustomobject]@{ id='plant-mapper';name='registered-plant';config=[pscustomobject]@{'claim.name'='plant_id';'claim.value'='22222222-2222-4222-8222-222222222222';'access.token.claim'='true'} }, $fake.mapper)
        return
    }
    if ($path -like '*/scope-mappings/realm') {
        if ($Method -eq 'Post') {
            foreach ($entry in ($Body | ConvertFrom-Json)) { if ($entry.name -notin $fake.scope) { $fake.scope += $entry.name } }; return
        }
        foreach ($name in $fake.scope) { [pscustomobject]@{name=$name} }; return
    }
    if ($path -like '*/role-mappings/realm') {
        if ($Method -eq 'Post') { $fake.roles += 'orders.download'; return }
        foreach ($name in $fake.roles) { [pscustomobject]@{name=$name} }; return
    }
    if ($path -like '*/roles/orders.download') { return [pscustomobject]@{name='orders.download';composite=$false} }
    if ($path -like '*/roles/scans.ingest') { return [pscustomobject]@{name='scans.ingest';composite=$false} }
    if ($path -like '*/protocol-mappers/models/mapper' -and $Method -eq 'Put') {
        if ($fake.failMapper) { throw 'Synthetic mapper-write outage' }
        $fake.mapper = $Body | ConvertFrom-Json
        $fake.mapper.config.PSObject.Properties.Remove('usermodel.realmRoleMapping.rolePrefix')
        return
    }
    if ($path -like '*/protocol-mappers/models/*-mapper' -and $Method -eq 'Put') { return }
    if ($path -like '*/admin-events') { return [pscustomobject]@{resourcePath='clients/client/protocol-mappers/models/mapper'} }
    if ($path -eq '/admin/realms/laundry-development') { return [pscustomobject]@{adminEventsEnabled=$true} }
    throw "Unexpected mocked request: $Method $path"
}

$helper = Join-Path $repositoryRoot 'deploy/local/Initialize-DevelopmentOrderDownloadAccess.ps1'
Reset-Fixture
& $helper
Assert-True ($fake.writes.Count -eq 3) 'First grant must write only scope, account role and mapper.'
Assert-True (($fake.scope -join ',') -eq 'scans.ingest,orders.download') 'Scope permission preservation failed.'
Assert-True (($fake.roles -join ',') -eq 'scans.ingest,orders.download') 'Account permission preservation failed.'
Assert-True ($fake.mapper.protocolMapper -eq 'oidc-usermodel-realm-role-mapper') 'Role-backed mapper missing.'
$fake.writes.Clear()
& $helper
Assert-True ($fake.writes.Count -eq 0) 'Already-configured grant must not write again.'
& (Join-Path $repositoryRoot 'deploy/local/Initialize-Gateway-Identity.ps1')
Assert-True ($fake.mapper.protocolMapper -eq 'oidc-usermodel-realm-role-mapper') 'Baseline setup must preserve upgraded permissions.'
Assert-True ('orders.download' -in $fake.scope -and 'scans.ingest' -in $fake.scope) 'Baseline setup removed approved scope.'
Reset-Fixture; $fake.failMapper = $true
$rejected = $false
try { & $helper } catch { $rejected = $true }
Assert-True ($rejected -and $fake.mapper.protocolMapper -eq 'oidc-hardcoded-claim-mapper') 'Partial failure must leave the baseline mapper intact.'
$fake.failMapper = $false
& $helper
Assert-True ($fake.mapper.protocolMapper -eq 'oidc-usermodel-realm-role-mapper') 'Partial setup did not recover on retry.'
Reset-Fixture; $fake.enabled = $false
$rejected = $false
try { & $helper } catch { $rejected = $true }
Assert-True ($rejected -and $fake.writes.Count -eq 0) 'Unexpected client must reject before mutation.'
Reset-Fixture; $fake.roles += 'orders.create'
$rejected = $false
try { & $helper } catch { $rejected = $true }
Assert-True ($rejected -and $fake.writes.Count -eq 0) 'Unexpected role must reject before mutation.'

# Pure startup tests: no real files, identity service, credential inspection or API launch.
function Invoke-WebRequest { param($Uri,$Method,$ContentType,[switch]$UseBasicParsing,$TimeoutSec,$Body) [pscustomobject]@{StatusCode=201} }
function dotnet { $fake.observedDownloadFlag = $env:OrderPlanDownload__Enabled; $global:LASTEXITCODE = 0 }
$previousCredential = $env:MANAGEMENT_CLIENT_SECRET
$previousFlag = $env:OrderPlanDownload__Enabled
try {
    $env:MANAGEMENT_CLIENT_SECRET = 'synthetic-test-only'
    $env:OrderPlanDownload__Enabled = 'original-test-value'
    $starter = Join-Path $repositoryRoot 'deploy/local/Start-DevelopmentManagement.ps1'
    & $starter -WithOrders
    Assert-True ($fake.observedDownloadFlag -ceq 'False') 'Default startup must leave downloads disabled.'
    Assert-True ($env:OrderPlanDownload__Enabled -ceq 'original-test-value') 'Startup must restore the process flag.'
    & $starter -WithOrders -WithOrderDownloads
    Assert-True ($fake.observedDownloadFlag -ceq 'True') 'Explicit startup opt-in was not forwarded.'
    Assert-True ($env:OrderPlanDownload__Enabled -ceq 'original-test-value') 'Opt-in startup must restore the process flag.'
} finally {
    $env:MANAGEMENT_CLIENT_SECRET = $previousCredential
    $env:OrderPlanDownload__Enabled = $previousFlag
}
Write-Host 'PASS: isolated download grant/idempotency/rejection and startup opt-in/restoration checks. No external calls were made.'
