[CmdletBinding()]
param(
    [ValidateSet('metadata','build','all')]
    [string]$Mode = 'all'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# Match docfx.json's src/*/*.csproj scope and exclusions on every platform.
# The compiler is a non-packable net8.0 build tool, not a public API library.
$projects = @(Get-ChildItem 'src/*/*.csproj' -File |
    Where-Object {
        $_.Directory.Name -notlike 'OpenConnect.WorkerHosts.*' -and
        $_.Directory.Name -ne 'OpenConnect.LiveUI.Compiler'
    } | Sort-Object FullName)
if (-not $projects) {
    throw 'No publishable OpenConnect source projects found.'
}
if (-not (Test-Path 'contracts/openapi/openconnect.v1.yaml')) {
    throw 'The versioned OpenAPI contract is missing.'
}
if (-not (Get-ChildItem contracts/jsonschema -File -Filter '*.json')) {
    throw 'No JSON Schema contracts found.'
}

$generated = Join-Path $repoRoot 'site/OpenConnect.Docs/generated'
if (Test-Path $generated) { Remove-Item $generated -Recurse -Force }

if ($Mode -in @('metadata','all')) {
    dotnet tool run docfx metadata docfx.json
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $yaml = Get-ChildItem (Join-Path $generated 'api/v1') -Recurse -File -Filter '*.yml' -ErrorAction SilentlyContinue
    if (-not $yaml) { throw 'DocFX metadata completed without generated API YAML.' }
}

if ($Mode -in @('build','all')) {
    dotnet tool run docfx build docfx.json --warningsAsErrors
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not (Test-Path (Join-Path $generated 'docfx'))) { throw 'DocFX build did not create the generated documentation directory.' }
}

$contractOutput = Join-Path $generated 'contracts'
New-Item -ItemType Directory -Path $contractOutput -Force | Out-Null
Copy-Item 'contracts/openapi/openconnect.v1.yaml' (Join-Path $contractOutput 'openconnect.v1.yaml') -Force
Copy-Item 'contracts/jsonschema' (Join-Path $contractOutput 'jsonschema') -Recurse -Force

$manifest = [ordered]@{
    schemaVersion = 'openconnect.docs.generated.v1'
    sourceRevision = (git rev-parse HEAD).Trim()
    targetFramework = 'net10.0'
    packageProjectCount = $projects.Count
    projects = @($projects | ForEach-Object { $_.FullName.Substring($repoRoot.Length + 1).Replace('\','/') })
    openApi = 'contracts/openapi/openconnect.v1.yaml'
    jsonSchemaDirectory = 'contracts/jsonschema'
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $generated 'manifest.json')
Write-Host "PASS: generated DocFX artifacts for $($projects.Count) source projects at $generated"
