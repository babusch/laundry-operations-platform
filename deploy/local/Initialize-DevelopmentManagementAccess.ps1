$ErrorActionPreference = 'Stop'

# Explicit local setup, not self-enrollment on login. Reads only the demo user's non-secret ID.
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectPath = Join-Path $repositoryRoot 'apps/cloud/Laundry.Api/Laundry.Api.csproj'
$developmentSettings = Get-Content (Join-Path $repositoryRoot 'apps/cloud/Laundry.Api/appsettings.Development.json') -Raw | ConvertFrom-Json
$bootstrapSubject = $null
$previousSubject = [Environment]::GetEnvironmentVariable('ManagementBootstrap__Subject', 'Process')
try {
    Push-Location $repositoryRoot
    try {
        $subjectQuery = "SELECT u.id FROM user_entity u JOIN realm r ON r.id = u.realm_id WHERE r.name = 'laundry-development' AND u.username = 'management-demo' AND u.enabled = true AND u.service_account_client_link IS NULL;"
        $bootstrapSubject = @(docker exec laundry-identity-development-identity-postgres-1 psql --username keycloak --dbname keycloak --tuples-only --no-align --command $subjectQuery)
        if ($LASTEXITCODE -ne 0 -or $bootstrapSubject.Count -ne 1) { throw 'Expected one enabled, non-service management-demo user in laundry-development. Start Keycloak and finish demo-user setup first.' }
        $subjectText = $bootstrapSubject[0].Trim()
        $subjectId = [Guid]::Empty
        if (-not [Guid]::TryParse($subjectText, [ref]$subjectId) -or $subjectId -eq [Guid]::Empty) { throw 'The demo user ID was not valid.' }

        docker compose start --wait postgres
        if ($LASTEXITCODE -ne 0) { throw 'Could not start the existing cloud database. Complete the documented local database setup first.' }
        # The committed Development connection is disposable local configuration, not private .env.
        dotnet ef database update --project $projectPath --context ManagementAccessDbContext --connection $developmentSettings.ConnectionStrings.Laundry -- --environment Development
        if ($LASTEXITCODE -ne 0) { throw 'Management access migration failed; no assignment has been provisioned.' }
        [Environment]::SetEnvironmentVariable('ManagementBootstrap__Subject', $subjectText, 'Process')
        dotnet run --project $projectPath -- --initialize-development-management-access
        if ($LASTEXITCODE -ne 0) { throw 'Management assignment provisioning failed.' }
        Write-Host 'Development membership setup finished. No password, client secret, or user ID was printed.'
    } finally {
        Pop-Location
    }
} finally {
    [Environment]::SetEnvironmentVariable('ManagementBootstrap__Subject', $previousSubject, 'Process')
    $bootstrapSubject = $null
    $subjectText = $null
}
