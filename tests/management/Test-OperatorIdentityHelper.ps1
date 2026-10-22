$ErrorActionPreference = 'Stop'
# Synthetic temporary files only: never inspect the repository's private .env or launch a real service.
$testRepository = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$helper = Join-Path $testRepository 'deploy/local/Start-DevelopmentOperatorIdentity.ps1'
$fixture = [System.IO.Path]::GetTempFileName()
$state = @{ Prompts = 0; Launches = 0; Busy = $false; Preview = $false; Fail = $false }
$names = @('OPERATOR_CLIENT_SECRET', 'OperatorAuthentication__Enabled', 'OperatorAuthentication__ClientSecret', 'ASPNETCORE_ENVIRONMENT',
    'Forwarding__Enabled', 'LocalOrderList__Enabled', 'DevelopmentItemLookup__Enabled', 'DevelopmentScanPreview__Enabled', 'UNRELATED_SYNTHETIC_SETTING')
$original = @{}
foreach ($setting in $names) { $original[$setting] = [Environment]::GetEnvironmentVariable($setting, 'Process') }
function Get-NetTCPConnection { param($LocalPort, [Alias('State')]$ConnectionState, $ErrorAction); if ($state.Busy) { return @{ OwningProcess = 42 } } }
function Read-Host { param($Prompt, [switch]$AsSecureString); $state.Prompts++; ConvertTo-SecureString 'synthetic-only' -AsPlainText -Force }
function dotnet {
    $state.Launches++
    if ([Environment]::GetEnvironmentVariable('OperatorAuthentication__ClientSecret', 'Process') -cne 'synthetic-only' -or
        [Environment]::GetEnvironmentVariable('OperatorAuthentication__Enabled', 'Process') -cne 'true' -or
        [Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Process') -cne 'Development' -or
        [Environment]::GetEnvironmentVariable('Forwarding__Enabled', 'Process') -cne 'false' -or
        -not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('UNRELATED_SYNTHETIC_SETTING', 'Process'))) { throw 'Synthetic launch isolation failed.' }
    foreach ($setting in @('LocalOrderList__Enabled', 'DevelopmentItemLookup__Enabled', 'DevelopmentScanPreview__Enabled')) {
        if ([Environment]::GetEnvironmentVariable($setting, 'Process') -cne $state.Preview.ToString()) { throw 'Preview opt-in isolation failed.' }
    }
    if ($state.Fail) { throw 'Synthetic launch failure.' }
    $global:LASTEXITCODE = 0
}
try {
    [Environment]::SetEnvironmentVariable('OPERATOR_CLIENT_SECRET', $null, 'Process')
    [Environment]::SetEnvironmentVariable('UNRELATED_SYNTHETIC_SETTING', $null, 'Process')
    [Environment]::SetEnvironmentVariable('OperatorAuthentication__ClientSecret', 'synthetic-baseline', 'Process')
    foreach ($literal in @("'synthetic-only'", '"synthetic-only" # comment', 'synthetic-only # comment')) {
        [System.IO.File]::WriteAllText($fixture, "UNRELATED_SYNTHETIC_SETTING=not-imported`nOPERATOR_CLIENT_SECRET=$literal`n")
        $output = & $helper -EnvFile $fixture *>&1
        if (($output | Out-String).Contains('synthetic-only') -or $state.Prompts -ne 0 -or
            [Environment]::GetEnvironmentVariable('OperatorAuthentication__ClientSecret', 'Process') -cne 'synthetic-baseline') { throw 'Saved setting, privacy or restoration failed.' }
    }
    [System.IO.File]::WriteAllText($fixture, 'OPERATOR_CLIENT_SECRET=not-used')
    [Environment]::SetEnvironmentVariable('OPERATOR_CLIENT_SECRET', 'synthetic-only', 'Process')
    $state.Preview = $true
    & $helper -EnvFile $fixture -WithPreview *> $null
    $state.Preview = $false
    [Environment]::SetEnvironmentVariable('OPERATOR_CLIENT_SECRET', 'not-used', 'Process')
    & $helper -EnvFile $fixture -PromptForSecret *> $null
    if ($state.Prompts -ne 1) { throw 'Forced prompt did not ignore saved values.' }
    [Environment]::SetEnvironmentVariable('OPERATOR_CLIENT_SECRET', $null, 'Process')
    foreach ($bad in @("OPERATOR_CLIENT_SECRET='unterminated", "OPERATOR_CLIENT_SECRET=one`nOPERATOR_CLIENT_SECRET=two")) {
        [System.IO.File]::WriteAllText($fixture, $bad)
        $before = $state.Launches; $rejected = $false
        try { & $helper -EnvFile $fixture *> $null } catch { $rejected = $true }
        if (-not $rejected -or $state.Launches -ne $before) { throw 'Bad literal was not rejected before launch.' }
    }
    $state.Busy = $true; $rejected = $false; $before = $state.Launches
    try { & $helper -EnvFile $fixture *> $null } catch { $rejected = $_.Exception.Message -like '*port 7200*' }
    if (-not $rejected -or $state.Launches -ne $before) { throw 'Occupied listener was not protected.' }
    $state.Busy = $false; $state.Fail = $true
    [System.IO.File]::WriteAllText($fixture, 'OPERATOR_CLIENT_SECRET=synthetic-only')
    $rejected = $false
    try { & $helper -EnvFile $fixture *> $null } catch { $rejected = $_.Exception.Message -eq 'Synthetic launch failure.' }
    if (-not $rejected -or [Environment]::GetEnvironmentVariable('OperatorAuthentication__ClientSecret', 'Process') -cne 'synthetic-baseline') { throw 'Failure did not restore environment.' }
    Write-Output 'Nine synthetic operator-helper cases passed; no real credentials, listeners or services accessed.'
} finally {
    foreach ($setting in $names) { [Environment]::SetEnvironmentVariable($setting, $original[$setting], 'Process') }
    [System.IO.File]::Delete($fixture)
}
