$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
Push-Location $repositoryRoot
try {
    if (Get-NetTCPConnection -State Listen -LocalPort 7200 -ErrorAction SilentlyContinue) {
        throw 'Gateway port already occupied.'
    }
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    Write-Host 'Verifying local order access with a temporary station; scan forwarding is forced off.'
    Write-Host 'The test retains audit records and revokes its temporary source. No existing station is selected.'
    dotnet run --project apps/edge/Laundry.Edge --configuration LocalOrderAccessTest --no-launch-profile -- --verify-local-order-access
    if ($LASTEXITCODE -ne 0) { throw 'Local verification failed; check its safe summary and any cleanup warning.' }
}
catch { throw 'Local order access verification did not pass. Check target, port, service availability and the safe cleanup summary; no private error body printed.' }
finally {
    if ($null -eq $previousEnvironment) { Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue }
    else { $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment }
    Pop-Location
}
