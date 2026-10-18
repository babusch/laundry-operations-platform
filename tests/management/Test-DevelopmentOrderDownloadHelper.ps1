$ErrorActionPreference = 'Stop'
# Synthetic-only: never resolves real Compose configuration or starts an application.
$helperPath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'deploy/local/Download-DevelopmentOrders.ps1'
$downloadTestState = @{ Mode = 'success'; Launches = 0 }
function docker {
    $downloadTestState.LastDocker = $args -join ' '
    if (($args -join ' ') -cne 'compose -f docker-compose.identity.yml config --format json') { throw 'Unexpected Docker command.' }
    $global:LASTEXITCODE = 0
    if ($downloadTestState.Mode -eq 'compose-failed') { $global:LASTEXITCODE = 1; return }
    $value = if ($downloadTestState.Mode -eq 'missing') { '' } else { 'synthetic-only' }
    @{ services = @{ keycloak = @{ environment = @{ GATEWAY_CLIENT_SECRET = $value } } } } | ConvertTo-Json -Depth 6 -Compress
}
function dotnet {
    $downloadTestState.LastDotnet = $args -join ' '
    # PowerShell consumes the standalone -- for a function mock; native dotnet receives it.
    if (($args -join ' ') -cne 'run --project apps/edge/Laundry.Edge --configuration OrderDownloadOnce --no-launch-profile --download-orders-once' -or
        $env:GatewayIdentity__ClientSecret -cne 'synthetic-only' -or $env:ASPNETCORE_ENVIRONMENT -cne 'Development') { throw 'Unexpected launch configuration.' }
    $downloadTestState.Launches++
    $global:LASTEXITCODE = switch ($downloadTestState.Mode) { 'failed' { 1 } 'bounded' { 2 } default { 0 } }
}
$previousSecret = $env:GatewayIdentity__ClientSecret
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$previousLocation = (Get-Location).Path
try {
    foreach ($mode in @('success','bounded','failed','missing','compose-failed')) {
        $downloadTestState.Mode = $mode; $downloadTestState.Launches = 0
        $env:GatewayIdentity__ClientSecret = 'synthetic-baseline'
        $env:ASPNETCORE_ENVIRONMENT = 'synthetic-baseline'
        $failed = $false
        try { & $helperPath *> $null } catch { $failed = $true }
        if ($failed -ne ($mode -in @('failed','missing','compose-failed'))) { throw "Unexpected result in synthetic mode $mode; failed=$failed, launches=$($downloadTestState.Launches), Docker=$($downloadTestState.LastDocker), dotnet=$($downloadTestState.LastDotnet)." }
        $expected = if ($mode -in @('missing','compose-failed')) { 0 } else { 1 }
        if ($downloadTestState.Launches -ne $expected -or $env:GatewayIdentity__ClientSecret -cne 'synthetic-baseline' -or
            $env:ASPNETCORE_ENVIRONMENT -cne 'synthetic-baseline' -or (Get-Location).Path -cne $previousLocation) { throw 'Launch or restoration failed.' }
    }
    $downloadTestState.Mode = 'success'
    Remove-Item Env:GatewayIdentity__ClientSecret,Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    & $helperPath *> $null
    if ($null -ne $env:GatewayIdentity__ClientSecret -or $null -ne $env:ASPNETCORE_ENVIRONMENT) { throw 'Absent environment settings were not restored.' }
    Write-Host 'Six synthetic order-download helper cases passed.'
}
finally {
    [Environment]::SetEnvironmentVariable('GatewayIdentity__ClientSecret', $previousSecret, 'Process')
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', $previousEnvironment, 'Process')
}
