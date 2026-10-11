$ErrorActionPreference = 'Stop'

# Only synthetic temporary dotenv fixtures are used; never read the repository's private .env.
$secretTestRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$secretHelperPath = Join-Path $secretTestRoot 'deploy/local/Start-DevelopmentManagement.ps1'
$secretFixturePath = [System.IO.Path]::GetTempFileName()
$secretTestState = @{ Prompts = 0; Launches = 0; Probes = 0 }
$secretSettingNames = @('MANAGEMENT_CLIENT_SECRET', 'ManagementAuthentication__Enabled',
    'ManagementAuthentication__ClientSecret', 'ManagementAuthentication__Authority',
    'ManagementAuthentication__ClientId', 'ManagementAccess__Enabled', 'Customers__Enabled', 'UNRELATED_SYNTHETIC_SETTING')
$secretOriginalSettings = @{}
foreach ($settingName in $secretSettingNames) {
    $secretOriginalSettings[$settingName] = [Environment]::GetEnvironmentVariable($settingName, 'Process')
}

function Read-Host {
    param($Prompt, [switch]$AsSecureString)
    $secretTestState.Prompts++
    ConvertTo-SecureString 'synthetic-test-only' -AsPlainText -Force
}

function Invoke-WebRequest {
    param($Method, $Uri, $ContentType, [switch]$UseBasicParsing, $TimeoutSec, $Body)
    $secretTestState.Probes++
    if ($Body.client_secret -cne 'synthetic-test-only') { throw 'Synthetic credential loading assertion failed.' }
    @{ StatusCode = 201 }
}

function dotnet {
    $secretTestState.Launches++
    if ([Environment]::GetEnvironmentVariable('ManagementAuthentication__ClientSecret', 'Process') -cne 'synthetic-test-only' -or
        -not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable('UNRELATED_SYNTHETIC_SETTING', 'Process'))) {
        throw 'Synthetic credential handoff or unrelated-setting isolation assertion failed.'
    }
    $global:LASTEXITCODE = 0
}

try {
    [Environment]::SetEnvironmentVariable('MANAGEMENT_CLIENT_SECRET', $null, 'Process')
    [Environment]::SetEnvironmentVariable('UNRELATED_SYNTHETIC_SETTING', $null, 'Process')
    [Environment]::SetEnvironmentVariable('ManagementAuthentication__ClientSecret', 'synthetic-baseline', 'Process')
    foreach ($line in @(
        "MANAGEMENT_CLIENT_SECRET='synthetic-test-only'",
        'MANAGEMENT_CLIENT_SECRET="synthetic-test-only" # comment',
        'MANAGEMENT_CLIENT_SECRET=synthetic-test-only # comment'
    )) {
        [System.IO.File]::WriteAllText($secretFixturePath, "UNRELATED_SYNTHETIC_SETTING=do-not-import`n$line`n")
        $captured = & $secretHelperPath -EnvFile $secretFixturePath *>&1
        if ($secretTestState.Prompts -ne 0 -or ($captured | Out-String).Contains('synthetic-test-only') -or
            [Environment]::GetEnvironmentVariable('ManagementAuthentication__ClientSecret', 'Process') -cne 'synthetic-baseline') {
            throw 'Saved-secret loading, output privacy, or environment restoration failed.'
        }
    }
    # A process setting takes precedence; a forced prompt ignores both process/file values.
    [System.IO.File]::WriteAllText($secretFixturePath, 'MANAGEMENT_CLIENT_SECRET=synthetic-not-used')
    [Environment]::SetEnvironmentVariable('MANAGEMENT_CLIENT_SECRET', 'synthetic-test-only', 'Process')
    & $secretHelperPath -EnvFile $secretFixturePath *> $null
    [Environment]::SetEnvironmentVariable('MANAGEMENT_CLIENT_SECRET', 'synthetic-not-used', 'Process')
    & $secretHelperPath -EnvFile $secretFixturePath -PromptForSecret *> $null
    if ($secretTestState.Prompts -ne 1) { throw 'Forced prompt precedence failed.' }
    [Environment]::SetEnvironmentVariable('MANAGEMENT_CLIENT_SECRET', $null, 'Process')
    [System.IO.File]::WriteAllText($secretFixturePath, 'MANAGEMENT_CLIENT_SECRET=')
    & $secretHelperPath -EnvFile $secretFixturePath *> $null
    if ($secretTestState.Prompts -ne 2) { throw 'Empty saved setting did not fall back to the hidden prompt.' }
    [System.IO.File]::WriteAllText($secretFixturePath, 'UNRELATED_SYNTHETIC_SETTING=do-not-import')
    & $secretHelperPath -EnvFile $secretFixturePath *> $null
    if ($secretTestState.Prompts -ne 3) { throw 'Absent saved setting did not fall back to the hidden prompt.' }
    foreach ($case in @(
        @{ Text = "MANAGEMENT_CLIENT_SECRET='synthetic-test-only'`nMANAGEMENT_CLIENT_SECRET='duplicate'"; Expected = 'MANAGEMENT_CLIENT_SECRET appears more than once*' },
        @{ Text = "MANAGEMENT_CLIENT_SECRET='unterminated"; Expected = 'MANAGEMENT_CLIENT_SECRET has invalid quoting*' }
    )) {
        [System.IO.File]::WriteAllText($secretFixturePath, $case.Text)
        $matched = $false
        try { & $secretHelperPath -EnvFile $secretFixturePath *> $null } catch { $matched = $_.Exception.Message -like $case.Expected }
        if (-not $matched -or $secretTestState.Launches -ne 7 -or $secretTestState.Probes -ne 7) {
            throw 'Invalid dotenv configuration was not rejected before launch.'
        }
    }
    Write-Output ('Nine synthetic management-secret loading cases passed on PowerShell ' + $PSVersionTable.PSVersion.ToString())
} finally {
    foreach ($settingName in $secretSettingNames) {
        [Environment]::SetEnvironmentVariable($settingName, $secretOriginalSettings[$settingName], 'Process')
    }
    [System.IO.File]::Delete($secretFixturePath)
}
