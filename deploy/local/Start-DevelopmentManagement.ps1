$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectPath = Join-Path $repositoryRoot 'apps/cloud/Laundry.Api/Laundry.Api.csproj'
$settingNames = @('ManagementAuthentication__Enabled', 'ManagementAuthentication__ClientSecret',
    'ManagementAuthentication__Authority', 'ManagementAuthentication__ClientId')
$previousSettings = @{}
foreach ($settingName in $settingNames) {
    $previousSettings[$settingName] = [Environment]::GetEnvironmentVariable($settingName, 'Process')
}

$secureClientSecret = Read-Host 'Paste the management-development client secret from Keycloak (input hidden)' -AsSecureString
try {
    $clientSecret = [System.Net.NetworkCredential]::new('', $secureClientSecret).Password
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
    $managementProbe = $null
    $managementVerifier = $null
    $managementChallenge = $null
    $managementRandomBytes = $null
}
