$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectPath = Join-Path $repositoryRoot 'apps/cloud/Laundry.Api/Laundry.Api.csproj'
$developmentSettings = Get-Content (Join-Path $repositoryRoot 'apps/cloud/Laundry.Api/appsettings.Development.json') -Raw | ConvertFrom-Json
Push-Location $repositoryRoot
try {
    docker compose start --wait postgres
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the existing cloud database.' }
    dotnet ef database update --project $projectPath --context OrderDbContext --connection $developmentSettings.ConnectionStrings.Laundry -- --environment Development
    if ($LASTEXITCODE -ne 0) { throw 'Order storage migration failed.' }
    Write-Host 'Development order storage migration finished. No order was created and no existing data was reset.'
} finally {
    Pop-Location
}
