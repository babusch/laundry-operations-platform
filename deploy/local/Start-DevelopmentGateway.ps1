$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$previousSecret = $env:GatewayIdentity__ClientSecret
$previousForwarding = $env:Forwarding__Enabled

Push-Location $repositoryRoot
try {
    $composeJson = docker compose -f docker-compose.identity.yml config --format json
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the local identity configuration.' }
    $compose = $composeJson | ConvertFrom-Json
    $gatewaySecret = $compose.services.keycloak.environment.GATEWAY_CLIENT_SECRET
    if ([string]::IsNullOrWhiteSpace($gatewaySecret)) { throw 'GATEWAY_CLIENT_SECRET is missing.' }

    $env:GatewayIdentity__ClientSecret = $gatewaySecret
    $env:Forwarding__Enabled = 'true'
    Write-Host 'Starting the development gateway with authenticated cloud forwarding enabled.'
    Write-Host 'The private gateway credential will not be printed. Press Ctrl+C to stop.'
    dotnet run --project apps/edge/Laundry.Edge
}
finally {
    if ($null -eq $previousSecret) { Remove-Item Env:GatewayIdentity__ClientSecret -ErrorAction SilentlyContinue }
    else { $env:GatewayIdentity__ClientSecret = $previousSecret }
    if ($null -eq $previousForwarding) { Remove-Item Env:Forwarding__Enabled -ErrorAction SilentlyContinue }
    else { $env:Forwarding__Enabled = $previousForwarding }
    $gatewaySecret = $null
    Pop-Location
}
