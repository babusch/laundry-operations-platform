param([switch]$WithAccess, [switch]$WithCustomers, [switch]$WithOrders, [switch]$PromptForSecret, [string]$EnvFile, [switch]$WithOrderDownloads)

$ErrorActionPreference = 'Stop'
if ($WithOrderDownloads -and -not $WithOrders) { throw 'Use -WithOrders together with -WithOrderDownloads.' }

$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectPath = Join-Path $repositoryRoot 'apps/cloud/Laundry.Api/Laundry.Api.csproj'
if ([string]::IsNullOrWhiteSpace($EnvFile)) { $EnvFile = Join-Path $repositoryRoot '.env' }

function Read-ManagementDotEnvSecret([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $found = $false
    $selectedValue = $null
    $reader = [System.IO.File]::OpenText([System.IO.Path]::GetFullPath($path))
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            # Consume only this named setting; never import/evaluate/print other dotenv values.
            $setting = [regex]::Match($line, '^\s*(?:export\s+)?MANAGEMENT_CLIENT_SECRET\s*=\s*(?<value>.*)$')
            if (-not $setting.Success) { continue }
            if ($found) { throw 'MANAGEMENT_CLIENT_SECRET appears more than once in the development environment file. Keep one entry.' }
            $found = $true
            $literal = $setting.Groups['value'].Value.Trim()
            if ($literal -eq '' -or $literal.StartsWith('#')) { $selectedValue = ''; continue }
            $quoted = [regex]::Match($literal, '^''(?<value>[^'']*)''\s*(?:#.*)?$|^"(?<value>[^"]*)"\s*(?:#.*)?$')
            if ($quoted.Success) { $selectedValue = $quoted.Groups['value'].Value; continue }
            $unquoted = [regex]::Match($literal, '^(?<value>[^\s''"]+)\s*(?:#.*)?$')
            if (-not $unquoted.Success) { throw 'MANAGEMENT_CLIENT_SECRET has invalid quoting or whitespace. Use one literal value on one line.' }
            $selectedValue = $unquoted.Groups['value'].Value
        }
    } finally {
        $reader.Dispose()
    }
    return $selectedValue
}
$settingNames = @('ManagementAuthentication__Enabled', 'ManagementAuthentication__ClientSecret',
    'ManagementAuthentication__Authority', 'ManagementAuthentication__ClientId', 'ManagementAccess__Enabled', 'Customers__Enabled', 'OrderPlanning__Enabled', 'OrderPlanDownload__Enabled')
$previousSettings = @{}
foreach ($settingName in $settingNames) {
    $previousSettings[$settingName] = [Environment]::GetEnvironmentVariable($settingName, 'Process')
}

$secureClientSecret = $null
try {
    $clientSecret = $null
    if (-not $PromptForSecret) {
        $clientSecret = [Environment]::GetEnvironmentVariable('MANAGEMENT_CLIENT_SECRET', 'Process')
        if ([string]::IsNullOrWhiteSpace($clientSecret)) { $clientSecret = Read-ManagementDotEnvSecret $EnvFile }
    }
    if ([string]::IsNullOrWhiteSpace($clientSecret)) {
        $secureClientSecret = Read-Host 'Paste the management-development client secret from Keycloak (input hidden)' -AsSecureString
        $clientSecret = [System.Net.NetworkCredential]::new('', $secureClientSecret).Password
    }
    if ([string]::IsNullOrWhiteSpace($clientSecret)) { throw 'A management client secret is required.' }
    if ($clientSecret -cne $clientSecret.Trim()) {
        throw 'The pasted client secret contains surrounding whitespace. Copy only the secret value. The API has not been started.'
    }

    $managementAuthority = 'https://localhost:8443/realms/laundry-development'
    $managementClientId = 'management-development'
    # Instance APIs work in both Windows PowerShell 5.1 (.NET Framework) and PowerShell 7.
    $managementRandomBytes = New-Object byte[] 32
    $managementRandom = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $managementHasher = [System.Security.Cryptography.SHA256]::Create()
    try {
        $managementRandom.GetBytes($managementRandomBytes)
        $managementVerifier = [Convert]::ToBase64String($managementRandomBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        $managementChallenge = [Convert]::ToBase64String($managementHasher.ComputeHash([System.Text.Encoding]::ASCII.GetBytes($managementVerifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    } finally {
        $managementRandom.Dispose()
        $managementHasher.Dispose()
    }
    $managementProbeStatus = $null
    try {
        $managementProbe = Invoke-WebRequest -Method Post -Uri "$managementAuthority/protocol/openid-connect/ext/par/request" `
            -ContentType 'application/x-www-form-urlencoded' -UseBasicParsing -TimeoutSec 10 -Body @{
                client_id = $managementClientId
                client_secret = $clientSecret
                response_type = 'code'
                redirect_uri = 'https://localhost:7100/management/signin-oidc'
                scope = 'openid'
                code_challenge = $managementChallenge
                code_challenge_method = 'S256'
                nonce = [Guid]::NewGuid().ToString('N')
                state = [Guid]::NewGuid().ToString('N')
            }
        $managementProbeStatus = [int]$managementProbe.StatusCode
    } catch {
        # Neither shell needs -SkipHttpErrorCheck. Only read status, never print an error body.
        if ($null -ne $_.Exception.Response -and $null -ne $_.Exception.Response.StatusCode) {
            $managementProbeStatus = [int]$_.Exception.Response.StatusCode
        } else {
            throw 'Could not verify the management credential over trusted HTTPS. Check Keycloak is running and certificate trust is configured. The API has not been started.'
        }
    }
    if ($managementProbeStatus -ne 201) {
        throw ('Keycloak did not accept the supplied management credential (HTTP {0}). Check the current management-development client secret. The API has not been started.' -f $managementProbeStatus)
    }
    $managementProbe = $null
    Write-Host 'Management client credential accepted by Keycloak over trusted HTTPS.'

    [Environment]::SetEnvironmentVariable('ManagementAuthentication__ClientSecret', $clientSecret, 'Process')
    [Environment]::SetEnvironmentVariable('ManagementAuthentication__Enabled', 'true', 'Process')
    [Environment]::SetEnvironmentVariable('ManagementAuthentication__Authority', $managementAuthority, 'Process')
    [Environment]::SetEnvironmentVariable('ManagementAuthentication__ClientId', $managementClientId, 'Process')
    [Environment]::SetEnvironmentVariable('ManagementAccess__Enabled', ($WithAccess.IsPresent -or $WithCustomers.IsPresent -or $WithOrders.IsPresent).ToString(), 'Process')
    [Environment]::SetEnvironmentVariable('Customers__Enabled', ($WithCustomers.IsPresent -or $WithOrders.IsPresent).ToString(), 'Process')
    [Environment]::SetEnvironmentVariable('OrderPlanning__Enabled', $WithOrders.IsPresent.ToString(), 'Process')
    [Environment]::SetEnvironmentVariable('OrderPlanDownload__Enabled', $WithOrderDownloads.IsPresent.ToString(), 'Process')
    $clientSecret = $null

    Write-Host 'Starting the development cloud API with management sign-in enabled.'
    Write-Host 'Open https://localhost:7100/management/login after the API starts.'
    Write-Host 'The application client secret is separate from your management-demo login password.'
    Write-Host 'Stop with Ctrl+C. Saved credentials are never printed or written by this helper.'
    dotnet run --project $projectPath
    if ($LASTEXITCODE -ne 0) { throw 'The development API did not exit successfully.' }
} finally {
    foreach ($settingName in $settingNames) {
        [Environment]::SetEnvironmentVariable($settingName, $previousSettings[$settingName], 'Process')
    }
    if ($null -ne $secureClientSecret) { $secureClientSecret.Dispose() }
    $clientSecret = $null
    $managementProbe = $null
    $managementVerifier = $null
    $managementChallenge = $null
    $managementRandomBytes = $null
}
