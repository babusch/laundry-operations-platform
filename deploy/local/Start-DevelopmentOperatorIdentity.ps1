param([switch]$WithPreview, [switch]$PromptForSecret, [string]$EnvFile)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if ([string]::IsNullOrWhiteSpace($EnvFile)) { $EnvFile = Join-Path $repositoryRoot '.env' }

function Read-OperatorDotEnvSecret([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    $found = $false
    $selectedValue = $null
    $reader = [System.IO.File]::OpenText([System.IO.Path]::GetFullPath($path))
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            # Consume only this literal named setting. Never evaluate/import/print the environment file.
            $setting = [regex]::Match($line, '^\s*(?:export\s+)?OPERATOR_CLIENT_SECRET\s*=\s*(?<value>.*)$')
            if (-not $setting.Success) { continue }
            if ($found) { throw 'Keep only one OPERATOR_CLIENT_SECRET entry in the development environment file.' }
            $found = $true
            $literal = $setting.Groups['value'].Value.Trim()
            if ($literal -eq '' -or $literal.StartsWith('#')) { $selectedValue = ''; continue }
            $quoted = [regex]::Match($literal, '^''(?<value>[^'']*)''\s*(?:#.*)?$|^"(?<value>[^"]*)"\s*(?:#.*)?$')
            if ($quoted.Success) { $selectedValue = $quoted.Groups['value'].Value; continue }
            $unquoted = [regex]::Match($literal, '^(?<value>[^\s''"]+)\s*(?:#.*)?$')
            if (-not $unquoted.Success) { throw 'OPERATOR_CLIENT_SECRET must be one literal value on one line.' }
            $selectedValue = $unquoted.Groups['value'].Value
        }
    } finally { $reader.Dispose() }
    return $selectedValue
}

# Never stop another process, apply migrations, enroll sources or change permissions from this helper.
if (Get-NetTCPConnection -LocalPort 7200 -State Listen -ErrorAction SilentlyContinue) {
    throw 'A service is already listening on port 7200. Stop the identified development gateway before starting this helper.'
}
$settings = @('ASPNETCORE_ENVIRONMENT', 'OperatorAuthentication__Enabled', 'OperatorAuthentication__ClientSecret',
    'Forwarding__Enabled', 'LocalOrderList__Enabled', 'DevelopmentItemLookup__Enabled', 'DevelopmentScanPreview__Enabled')
$previous = @{}
foreach ($setting in $settings) { $previous[$setting] = [Environment]::GetEnvironmentVariable($setting, 'Process') }
$secureSecret = $null
try {
    $secret = $null
    if (-not $PromptForSecret) {
        $secret = [Environment]::GetEnvironmentVariable('OPERATOR_CLIENT_SECRET', 'Process')
        if ([string]::IsNullOrWhiteSpace($secret)) { $secret = Read-OperatorDotEnvSecret $EnvFile }
    }
    if ([string]::IsNullOrWhiteSpace($secret)) {
        $secureSecret = Read-Host 'Paste the operator-development CLIENT secret from Keycloak (input hidden)' -AsSecureString
        $secret = [System.Net.NetworkCredential]::new('', $secureSecret).Password
    }
    if ([string]::IsNullOrWhiteSpace($secret) -or $secret -cne $secret.Trim()) { throw 'A client secret without surrounding whitespace is required.' }
    [Environment]::SetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Development', 'Process')
    [Environment]::SetEnvironmentVariable('OperatorAuthentication__Enabled', 'true', 'Process')
    [Environment]::SetEnvironmentVariable('OperatorAuthentication__ClientSecret', $secret, 'Process')
    [Environment]::SetEnvironmentVariable('Forwarding__Enabled', 'false', 'Process')
    foreach ($setting in @('LocalOrderList__Enabled', 'DevelopmentItemLookup__Enabled', 'DevelopmentScanPreview__Enabled')) {
        [Environment]::SetEnvironmentVariable($setting, $WithPreview.IsPresent.ToString(), 'Process')
    }
    $secret = $null
    Write-Host 'Starting the Development-only operator identity proof. Cloud forwarding is disabled.'
    Write-Host 'In your enrolled browser, open https://localhost:7200/operator/login'
    Write-Host 'The proof expires after 15 minutes. This is NOT inactivity locking or receiving authorization.'
    Write-Host 'Use the operator-development CLIENT secret here, not an account password or management client secret.'
    dotnet run --project (Join-Path $repositoryRoot 'apps/edge/Laundry.Edge') --configuration OperatorIdentityProof --no-launch-profile -- --urls https://localhost:7200
    if ($LASTEXITCODE -ne 0) { throw 'The development gateway did not exit successfully.' }
} finally {
    foreach ($setting in $settings) { [Environment]::SetEnvironmentVariable($setting, $previous[$setting], 'Process') }
    if ($null -ne $secureSecret) { $secureSecret.Dispose() }
    $secret = $null
}
