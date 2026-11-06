[CmdletBinding()]
param(
    [switch] $Security,
    [switch] $ContractParity
)

$ErrorActionPreference = 'Stop'

if ($Security) {
    # The .NET CLI reports advisories in text but may exit successfully when findings exist.
    $securityReport = dotnet list OpenConnect.slnx package --vulnerable --include-transitive 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    $securityReport | Write-Output
    if ($securityReport -match '(?i)has the following vulnerable packages') {
        Write-Error 'Vulnerable dependencies were reported; resolve the advisories before merging.'
        exit 1
    }
}

if ($ContractParity) {
    $contractTestProjects = @(Get-ChildItem -Path 'tests' -Filter '*Contract*.csproj' -Recurse -ErrorAction SilentlyContinue)
    if ($contractTestProjects.Count -eq 0) {
        Write-Error 'Contract parity cannot be verified: no tests/*Contract*.csproj project was found. Add a contract test project or wire the pinned Python contract validator before allowing CI to pass.'
        exit 1
    }
    else {
        foreach ($project in $contractTestProjects) {
            dotnet test $project.FullName -c Release --no-build --filter 'Category=Contract|FullyQualifiedName~Contract'
            if ($LASTEXITCODE -ne 0) {
                exit $LASTEXITCODE
            }
        }
    }
}

if (-not $Security -and -not $ContractParity) {
    dotnet restore OpenConnect.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet build OpenConnect.slnx -c Release --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet test OpenConnect.slnx -c Release --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet format OpenConnect.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
