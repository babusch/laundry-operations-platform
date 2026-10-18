$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$previousSecret = $env:GatewayIdentity__ClientSecret
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$composeJson = $null; $compose = $null; $gatewaySecret = $null
Push-Location $repositoryRoot
try {
    $composeJson = docker compose -f docker-compose.identity.yml config --format json
    if ($LASTEXITCODE -ne 0) { throw 'Local identity configuration unavailable.' }
    $compose = $composeJson | ConvertFrom-Json
    $gatewaySecret = $compose.services.keycloak.environment.GATEWAY_CLIENT_SECRET
    if ([string]::IsNullOrWhiteSpace($gatewaySecret)) { throw 'Gateway credential unavailable.' }
    $env:GatewayIdentity__ClientSecret = $gatewaySecret
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    Write-Host 'Downloading published orders once. Existing services, schemas and permissions are not changed.'
    dotnet run --project apps/edge/Laundry.Edge --configuration OrderDownloadOnce --no-launch-profile -- --download-orders-once
    if ($LASTEXITCODE -eq 2) { Write-Host 'Page bound reached; run this helper again to continue from saved progress.' }
    elseif ($LASTEXITCODE -ne 0) { throw 'Order download stopped; see the safe result code above.' }
}
catch { throw 'Development order download did not finish. No credentials or private response bodies were printed; saved progress is retained.' }
finally {
    if ($null -eq $previousSecret) { Remove-Item Env:GatewayIdentity__ClientSecret -ErrorAction SilentlyContinue }
    else { $env:GatewayIdentity__ClientSecret = $previousSecret }
    if ($null -eq $previousEnvironment) { Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue }
    else { $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment }
    $composeJson = $null; $compose = $null; $gatewaySecret = $null
    Pop-Location
}
