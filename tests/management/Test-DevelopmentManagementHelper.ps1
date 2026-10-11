$ErrorActionPreference = 'Stop'

# Synthetic-only script tests: no live network, passwords, private files, or server launch.
$helperRepositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$helperPath = Join-Path $helperRepositoryRoot 'deploy/local/Start-DevelopmentManagement.ps1'
$helperTestState = @{ Mode = 'accepted'; LaunchCount = 0; ProbeCount = 0 }

Add-Type -TypeDefinition @'
public sealed class HelperTestResponse : System.Net.WebResponse {
    public System.Net.HttpStatusCode StatusCode { get; set; }
}
'@

function Read-Host {
    param($Prompt, [switch]$AsSecureString)
    $value = switch ($helperTestState.Mode) {
        'empty' { '' }
        'whitespace' { ' synthetic-test-only ' }
        default { 'synthetic-test-only' }
    }
    if ($value -eq '') { return [System.Security.SecureString]::new() }
    ConvertTo-SecureString $value -AsPlainText -Force
}

function Invoke-WebRequest {
    param($Method, $Uri, $ContentType, [switch]$UseBasicParsing, $TimeoutSec, $Body)
    $helperTestState.ProbeCount++
    if ($Body.client_secret -cne 'synthetic-test-only' -or
        $Body.client_id -cne 'management-development' -or
        $Uri -cne 'https://localhost:8443/realms/laundry-development/protocol/openid-connect/ext/par/request' -or
        $Body.code_challenge_method -cne 'S256' -or $Body.code_challenge.Length -ne 43) {
        throw 'Synthetic request assertion failed.'
    }
    if ($helperTestState.Mode -eq 'outage') { throw 'Synthetic connection outage.' }
    if ($helperTestState.Mode -eq 'rejected') {
        $response = [HelperTestResponse]::new()
        $response.StatusCode = [System.Net.HttpStatusCode]::BadRequest
        throw [System.Net.WebException]::new('Synthetic rejection.', $null,
            [System.Net.WebExceptionStatus]::ProtocolError, $response)
    }
    @{ StatusCode = 201 }
}

function dotnet {
    $helperTestState.LaunchCount++
    if ([Environment]::GetEnvironmentVariable('ManagementAuthentication__ClientSecret', 'Process') -cne 'synthetic-test-only' -or
        [Environment]::GetEnvironmentVariable('ManagementAuthentication__ClientId', 'Process') -cne 'management-development' -or
        [Environment]::GetEnvironmentVariable('ManagementAuthentication__Authority', 'Process') -cne 'https://localhost:8443/realms/laundry-development') {
        throw 'Synthetic launch settings assertion failed.'
    }
    $global:LASTEXITCODE = 0
}

$helperSettingNames = @('ManagementAuthentication__Enabled', 'ManagementAuthentication__ClientSecret',
    'ManagementAuthentication__Authority', 'ManagementAuthentication__ClientId')
$helperOriginalSettings = @{}
foreach ($settingName in $helperSettingNames) {
    $helperOriginalSettings[$settingName] = [Environment]::GetEnvironmentVariable($settingName, 'Process')
}
try {
foreach ($settingName in $helperSettingNames) {
    [Environment]::SetEnvironmentVariable($settingName, 'synthetic-baseline', 'Process')
}

& $helperPath *> $null
if ($helperTestState.LaunchCount -ne 1) { throw 'Accepted helper flow did not launch once.' }
foreach ($case in @(
    @{ Mode = 'rejected'; Expected = 'Keycloak did not accept*HTTP 400*' },
    @{ Mode = 'outage'; Expected = 'Could not verify*' },
    @{ Mode = 'empty'; Expected = 'A management client secret is required.*' },
    @{ Mode = 'whitespace'; Expected = 'The pasted client secret contains surrounding whitespace.*' }
)) {
    $helperTestState.Mode = $case.Mode
    $matched = $false
    try { & $helperPath *> $null } catch { $matched = $_.Exception.Message -like $case.Expected }
    if (-not $matched -or $helperTestState.LaunchCount -ne 1) { throw ('Helper case failed: ' + $case.Mode) }
    foreach ($settingName in $helperSettingNames) {
        if ([Environment]::GetEnvironmentVariable($settingName, 'Process') -cne 'synthetic-baseline') {
            throw 'Helper did not restore process settings.'
        }
    }
}
if ($helperTestState.ProbeCount -ne 3) { throw 'Unexpected preflight request count.' }
Write-Output ('All five synthetic management-helper cases passed on PowerShell ' + $PSVersionTable.PSVersion.ToString())
} finally {
    foreach ($settingName in $helperSettingNames) {
        [Environment]::SetEnvironmentVariable($settingName, $helperOriginalSettings[$settingName], 'Process')
    }
}
