$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectPath = Join-Path $repositoryRoot 'apps/cloud/Laundry.Api/Laundry.Api.csproj'
$developmentSettings = Get-Content (Join-Path $repositoryRoot 'apps/cloud/Laundry.Api/appsettings.Development.json') -Raw | ConvertFrom-Json
$previousCloudConnection = [Environment]::GetEnvironmentVariable('ConnectionStrings__Laundry', 'Process')
Push-Location $repositoryRoot
try {
    docker compose start --wait postgres
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the existing cloud database.' }
    # Explicit migration of this module only, using the committed disposable Development connection.
    dotnet ef database update --project $projectPath --context CustomerDbContext --connection $developmentSettings.ConnectionStrings.Laundry -- --environment Development
    if ($LASTEXITCODE -ne 0) { throw 'Customer migration failed; no customer has been provisioned.' }
    # Bootstrap must use the same local database as the explicit migration, not an inherited override.
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Laundry', $developmentSettings.ConnectionStrings.Laundry, 'Process')
    dotnet run --project $projectPath -- --initialize-development-customer
    if ($LASTEXITCODE -ne 0) { throw 'Development customer setup failed.' }
    Write-Host 'Development customer setup finished. No passwords or private environment files were inspected.'
} finally {
    [Environment]::SetEnvironmentVariable('ConnectionStrings__Laundry', $previousCloudConnection, 'Process')
    Pop-Location
}
