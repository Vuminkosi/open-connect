$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$openApi = Join-Path $root 'contracts/openapi/openconnect.v1.yaml'
if (-not (Test-Path $openApi)) { throw "Missing OpenAPI contract: $openApi" }
$validator = Join-Path $PSScriptRoot 'validate-openapi-references.cjs'
& node $validator $openApi (Join-Path $root 'contracts')
if ($LASTEXITCODE -ne 0) { throw 'OpenAPI reference validation failed.' }
foreach ($schema in Get-ChildItem (Join-Path $root 'contracts/jsonschema') -File -Filter '*.json') {
    try { Get-Content $schema.FullName -Raw | ConvertFrom-Json | Out-Null }
    catch { throw "Invalid JSON Schema: $($schema.FullName): $($_.Exception.Message)" }
}
Write-Host 'PASS: OpenAPI references and JSON Schema files resolve and parse.'
