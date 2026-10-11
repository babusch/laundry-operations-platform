$ErrorActionPreference = 'Stop'
$setupRepositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$setupPath = Join-Path $setupRepositoryRoot 'deploy/local/Initialize-DevelopmentManagementAccess.ps1'
$setupTestState = @{ Mode = 'accepted'; Migrations = 0; Provisions = 0 }
$syntheticSubject = '33333333-3333-4333-8333-333333333333'
$priorTestSubject = [Environment]::GetEnvironmentVariable('ManagementBootstrap__Subject', 'Process')

function docker {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'exec') {
        if ($setupTestState.Mode -eq 'missing_user') { return }
        return $syntheticSubject
    }
    if ($args[0] -ne 'compose' -or $args[1] -ne 'start' -or $args[2] -ne '--wait' -or $args[3] -ne 'postgres') {
        throw 'Unexpected synthetic Docker operation.'
    }
}

function dotnet {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'ef') {
        if ($args -notcontains 'ManagementAccessDbContext') { throw 'Wrong migration context.' }
        $setupTestState.Migrations++
        if ($setupTestState.Mode -eq 'migration_failure') { $global:LASTEXITCODE = 1 }
        return
    }
    if ($args[0] -ne 'run' -or $args -notcontains '--initialize-development-management-access' -or
        [Environment]::GetEnvironmentVariable('ManagementBootstrap__Subject', 'Process') -cne $syntheticSubject) {
        throw 'Unexpected synthetic provisioning operation.'
    }
    $setupTestState.Provisions++
}

try {
    [Environment]::SetEnvironmentVariable('ManagementBootstrap__Subject', 'synthetic-baseline', 'Process')
    $initialLocation = (Get-Location).Path
    & $setupPath *> $null
    if ($setupTestState.Migrations -ne 1 -or $setupTestState.Provisions -ne 1) { throw 'Accepted setup failed.' }
    foreach ($case in @(
        @{ Mode = 'missing_user'; Expected = 'Expected one enabled*' },
        @{ Mode = 'migration_failure'; Expected = 'Management access migration failed*' }
    )) {
        $setupTestState.Mode = $case.Mode
        $matched = $false
        try { & $setupPath *> $null } catch { $matched = $_.Exception.Message -like $case.Expected }
        if (-not $matched -or $setupTestState.Provisions -ne 1) { throw ('Setup case failed: ' + $case.Mode) }
        if ((Get-Location).Path -cne $initialLocation -or
            [Environment]::GetEnvironmentVariable('ManagementBootstrap__Subject', 'Process') -cne 'synthetic-baseline') {
            throw 'Setup did not restore its location/process settings.'
        }
    }
    Write-Output ('All three synthetic membership-setup cases passed on PowerShell ' + $PSVersionTable.PSVersion.ToString())
} finally {
    [Environment]::SetEnvironmentVariable('ManagementBootstrap__Subject', $priorTestSubject, 'Process')
}
