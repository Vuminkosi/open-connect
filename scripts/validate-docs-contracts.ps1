$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$openApi = Join-Path $root 'contracts/openapi/openconnect.v1.yaml'
if (-not (Test-Path $openApi)) { throw "Missing OpenAPI contract: $openApi" }
$yaml = Get-Content $openApi -Raw
foreach ($match in [regex]::Matches($yaml, '\$ref:\s*([^\s}]+)')) {
    $relative = $match.Groups[1].Value.Trim("'").Trim('"')
    $target = Join-Path (Split-Path $openApi -Parent) $relative
    if (-not (Test-Path $target)) { throw "OpenAPI reference does not resolve: $relative" }
}
foreach ($schema in Get-ChildItem (Join-Path $root 'contracts/jsonschema') -File -Filter '*.json') {
    try { Get-Content $schema.FullName -Raw | ConvertFrom-Json | Out-Null }
    catch { throw "Invalid JSON Schema: $($schema.FullName): $($_.Exception.Message)" }
}
Write-Host 'PASS: OpenAPI references and JSON Schema files resolve and parse.'
