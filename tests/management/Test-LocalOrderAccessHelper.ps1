$ErrorActionPreference = 'Stop'
# Synthetic-only mocks: no server, database, credentials, network or private files.
$helperPath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'deploy/local/Test-DevelopmentLocalOrderAccess.ps1'
$accessHelperState = @{ Mode = 'success'; Launches = 0 }
function Get-NetTCPConnection {
    param($State, $LocalPort, $ErrorAction)
    if ($State -cne 'Listen' -or $LocalPort -ne 7200) { throw 'Unexpected port probe.' }
    if ($accessHelperState.Mode -eq 'occupied') { @{ LocalPort = 7200 } }
}
function dotnet {
    # PowerShell function mocks consume the standalone --; native dotnet receives it.
    if (($args -join ' ') -cne 'run --project apps/edge/Laundry.Edge --configuration LocalOrderAccessTest --no-launch-profile --verify-local-order-access' -or
        $env:ASPNETCORE_ENVIRONMENT -cne 'Development') { throw 'Unexpected verification command.' }
    $accessHelperState.Launches++
    $global:LASTEXITCODE = if ($accessHelperState.Mode -eq 'failed') { 1 } else { 0 }
}
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$previousLocation = (Get-Location).Path
try {
    foreach ($mode in @('success','failed','occupied')) {
        $accessHelperState.Mode = $mode; $accessHelperState.Launches = 0
        $env:ASPNETCORE_ENVIRONMENT = 'synthetic-baseline'
        $failed = $false
        try { & $helperPath *> $null } catch { $failed = $true }
        if ($failed -ne ($mode -ne 'success')) { throw 'Unexpected helper result.' }
        $expected = if ($mode -eq 'occupied') { 0 } else { 1 }
        if ($accessHelperState.Launches -ne $expected -or $env:ASPNETCORE_ENVIRONMENT -cne 'synthetic-baseline' -or
            (Get-Location).Path -cne $previousLocation) { throw 'Launch or environment restoration failed.' }
    }
    Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    $accessHelperState.Mode = 'success'
    & $helperPath *> $null
    if ($null -ne $env:ASPNETCORE_ENVIRONMENT) { throw 'Absent environment setting was not restored.' }
    Write-Host 'Four synthetic local-order access helper cases passed.'
}
finally {
    if ($null -eq $previousEnvironment) { Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue }
    else { $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment }
}
