$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectPath = Join-Path $repositoryRoot 'apps/cloud/Laundry.Api/Laundry.Api.csproj'

# Explicit maintenance action only: no database start, migration, grant or identity impersonation.
Push-Location $repositoryRoot
try {
    dotnet run --no-launch-profile --project $projectPath -- --environment Development --initialize-development-order-publications
    if ($LASTEXITCODE -ne 0) {
        throw 'Initial publication stopped. Earlier copies may have committed; correct the prerequisite and rerun the same command safely.'
    }
} finally {
    Pop-Location
}
