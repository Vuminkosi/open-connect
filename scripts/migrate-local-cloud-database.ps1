param([string] $ComposeFile)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ComposeFile)) {
    $ComposeFile = Join-Path $repositoryRoot 'docker-compose-all.yml'
}
$composePath = (Resolve-Path -LiteralPath $ComposeFile).Path
$project = Join-Path $repositoryRoot 'site/OpenConnect.Docs/OpenConnect.Docs.csproj'
$sqlFile = [System.IO.Path]::GetTempFileName()
$previousConnection = $env:ConnectionStrings__CloudControlPlane

try {
    Push-Location $repositoryRoot
    dotnet build $project --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Cloud migration project build failed.' }

    # Script generation does not connect, but the design-time factory requires a value.
    $env:ConnectionStrings__CloudControlPlane = 'Host=localhost;Database=unused;Username=unused'
    dotnet tool run dotnet-ef migrations script --idempotent --no-build --configuration Release `
        --project $project --startup-project $project --output $sqlFile
    if ($LASTEXITCODE -ne 0) { throw 'Cloud migration script generation failed.' }

    $databases = 'SELECT datname FROM pg_database;' | docker compose -f $composePath exec -T postgres `
        sh -c 'exec psql -X -t -A -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres'
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the Compose PostgreSQL databases.' }
    $databaseNames = (($databases -join "`n") -split '\r?\n') | ForEach-Object { $_.Trim() }
    if ($databaseNames -notcontains 'openconnect_cloud') {
        docker compose -f $composePath exec -T postgres sh -c 'exec createdb -U "$POSTGRES_USER" openconnect_cloud'
        if ($LASTEXITCODE -ne 0) { throw 'Unable to create the Cloud database; inspect it before retrying.' }
    }

    Get-Content -LiteralPath $sqlFile -Raw | docker compose -f $composePath exec -T postgres `
        sh -c 'exec psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d openconnect_cloud'
    if ($LASTEXITCODE -ne 0) { throw 'Cloud migration failed; inspect the database before retrying.' }
}
finally {
    $env:ConnectionStrings__CloudControlPlane = $previousConnection
    Pop-Location
    Remove-Item -LiteralPath $sqlFile -ErrorAction SilentlyContinue
}
