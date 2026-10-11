$ErrorActionPreference = 'Stop'
$orderTestRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$orderSetupPath = Join-Path $orderTestRoot 'deploy/local/Initialize-DevelopmentOrderStorage.ps1'
$orderTestState = @{ Mode = 'accepted'; Migrations = 0 }

function docker {
    if ($args[0] -ne 'compose' -or $args[1] -ne 'start' -or $args[2] -ne '--wait' -or $args[3] -ne 'postgres') {
        throw 'Unexpected synthetic Docker operation.'
    }
    $global:LASTEXITCODE = $(if ($orderTestState.Mode -eq 'database_failure') {1} else {0})
}

function dotnet {
    if ($args[0] -ne 'ef' -or $args -notcontains 'OrderDbContext' -or $args -contains 'run') {
        throw 'Order setup must only migrate storage, never create an order.'
    }
    $orderTestState.Migrations++
    $global:LASTEXITCODE = $(if ($orderTestState.Mode -eq 'migration_failure') {1} else {0})
}

$orderOriginalLocation = (Get-Location).Path
& $orderSetupPath *> $null
if ($orderTestState.Migrations -ne 1) { throw 'Accepted order storage setup failed.' }
foreach ($case in @(
    @{ Mode = 'database_failure'; Expected = 'Could not start*' },
    @{ Mode = 'migration_failure'; Expected = 'Order storage migration failed*' }
)) {
    $orderTestState.Mode = $case.Mode
    $matched = $false
    try { & $orderSetupPath *> $null } catch { $matched = $_.Exception.Message -like $case.Expected }
    if (-not $matched -or (Get-Location).Path -cne $orderOriginalLocation) {
        throw ('Synthetic order storage case failed: ' + $case.Mode)
    }
}
Write-Output ('All three synthetic order-storage setup cases passed on PowerShell ' + $PSVersionTable.PSVersion.ToString())
