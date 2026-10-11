$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectPath = Join-Path $repositoryRoot 'apps/cloud/Laundry.Api/Laundry.Api.csproj'
$settingNames = @('ManagementAuthentication__Enabled', 'ManagementAuthentication__ClientSecret')
$previousSettings = @{}
foreach ($settingName in $settingNames) {
    $previousSettings[$settingName] = [Environment]::GetEnvironmentVariable($settingName, 'Process')
}

$secureClientSecret = Read-Host 'Paste the management-development client secret from Keycloak (input hidden)' -AsSecureString
try {
    $clientSecret = [System.Net.NetworkCredential]::new('', $secureClientSecret).Password
    if ([string]::IsNullOrWhiteSpace($clientSecret)) { throw 'A management client secret is required.' }
    [Environment]::SetEnvironmentVariable('ManagementAuthentication__ClientSecret', $clientSecret, 'Process')
    [Environment]::SetEnvironmentVariable('ManagementAuthentication__Enabled', 'true', 'Process')
    $clientSecret = $null

    Write-Host 'Starting the development cloud API with management sign-in enabled.'
    Write-Host 'Open https://localhost:7100/management/login after the API starts.'
    Write-Host 'The prompt was for the application client secret, not your login password.'
    Write-Host 'Stop with Ctrl+C. No private .env file is read by this helper.'
    dotnet run --project $projectPath
    if ($LASTEXITCODE -ne 0) { throw 'The development API did not exit successfully.' }
} finally {
    foreach ($settingName in $settingNames) {
        [Environment]::SetEnvironmentVariable($settingName, $previousSettings[$settingName], 'Process')
    }
    $secureClientSecret.Dispose()
    $clientSecret = $null
}
