$ErrorActionPreference = 'Stop'
$customerTestRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$customerSetupPath = Join-Path $customerTestRoot 'deploy/local/Initialize-DevelopmentCustomer.ps1'
$customerTestState = @{ Mode = 'accepted'; Migrations = 0; Setups = 0 }
$expectedDevelopmentConnection = (Get-Content (Join-Path $customerTestRoot 'apps/cloud/Laundry.Api/appsettings.Development.json') -Raw | ConvertFrom-Json).ConnectionStrings.Laundry

function docker {
    if ($args[0] -ne 'compose' -or $args[1] -ne 'start' -or $args[2] -ne '--wait' -or $args[3] -ne 'postgres') {
        throw 'Unexpected synthetic Docker operation.'
    }
    $global:LASTEXITCODE = $(if ($customerTestState.Mode -eq 'database_failure') {1} else {0})
}

function dotnet {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'ef') {
        if ($args -notcontains 'CustomerDbContext') { throw 'Wrong customer migration context.' }
        $customerTestState.Migrations++
        if ($customerTestState.Mode -eq 'migration_failure') { $global:LASTEXITCODE = 1 }
        return
    }
    if ($args[0] -ne 'run' -or $args -notcontains '--initialize-development-customer') {
        throw 'Unexpected synthetic customer setup operation.'
    }
    if ([Environment]::GetEnvironmentVariable('ConnectionStrings__Laundry', 'Process') -cne $expectedDevelopmentConnection) {
        throw 'Customer setup did not use the same Development connection as the migration.'
    }
    $customerTestState.Setups++
}

$customerOriginalLocation = (Get-Location).Path
$customerPriorConnection = [Environment]::GetEnvironmentVariable('ConnectionStrings__Laundry', 'Process')
try {
[Environment]::SetEnvironmentVariable('ConnectionStrings__Laundry', 'synthetic-baseline', 'Process')
& $customerSetupPath *> $null
if ($customerTestState.Migrations -ne 1 -or $customerTestState.Setups -ne 1) { throw 'Accepted customer setup failed.' }
foreach ($case in @(
    @{ Mode = 'database_failure'; Expected = 'Could not start*' },
    @{ Mode = 'migration_failure'; Expected = 'Customer migration failed*' }
)) {
    $customerTestState.Mode = $case.Mode
    $matched = $false
    try { & $customerSetupPath *> $null } catch { $matched = $_.Exception.Message -like $case.Expected }
    if (-not $matched -or $customerTestState.Setups -ne 1 -or (Get-Location).Path -cne $customerOriginalLocation -or
        [Environment]::GetEnvironmentVariable('ConnectionStrings__Laundry', 'Process') -cne 'synthetic-baseline') {
        throw ('Synthetic customer setup case failed: ' + $case.Mode)
    }
}
Write-Output ('All three synthetic customer-setup cases passed on PowerShell ' + $PSVersionTable.PSVersion.ToString())
} finally {
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Laundry', $customerPriorConnection, 'Process')
}
