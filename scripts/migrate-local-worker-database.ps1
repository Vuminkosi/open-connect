param(
    [string] $ComposeFile
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'samples/PostgreSql/OpenConnect.Samples.PostgreSql.csproj'
if ([string]::IsNullOrWhiteSpace($ComposeFile)) {
    $ComposeFile = Join-Path $repositoryRoot 'docker-compose-all.yml'
}
$composePath = (Resolve-Path -LiteralPath $ComposeFile).Path
$sqlFile = [System.IO.Path]::GetTempFileName()

try {
    Push-Location $repositoryRoot
    # Render the reviewed sample migrations rather than allowing workers to run DDL.
    dotnet build $project --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL migration project build failed.' }

    dotnet tool run dotnet-ef migrations script --idempotent --no-build --configuration Release `
        --project $project --startup-project $project --output $sqlFile
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL migration script generation failed.' }

    # The database and user come from the running Compose service, not host defaults.
    Get-Content -LiteralPath $sqlFile -Raw | docker compose -f $composePath exec -T postgres `
        sh -c 'exec psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL migration failed; inspect the database before retrying.' }
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $sqlFile -ErrorAction SilentlyContinue
}
