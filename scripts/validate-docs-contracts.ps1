param([switch]$UseParsedReferences)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$openApiDir = Join-Path $root 'contracts/openapi'

function Test-OpenApiInternalRef {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string[]]$Lines,
        [Parameter(Mandatory = $true)][string]$Pointer
    )

    if (-not $Pointer.StartsWith('#/')) {
        return $false
    }

    $segments = $Pointer.Substring(2).Split('/') | ForEach-Object {
        $_.Replace('~1', '/').Replace('~0', '~')
    }

    $startIndex = 0
    $parentIndent = -1

    foreach ($segment in $segments) {
        $found = $false
        $escapedSegment = [regex]::Escape($segment)
        $pattern = "^(\s*)(?:['""]?$escapedSegment['""]?)\s*:"

        for ($i = $startIndex; $i -lt $Lines.Count; $i++) {
            $line = $Lines[$i]
            if ([string]::IsNullOrWhiteSpace($line) -or $line -match '^\s*#') {
                continue
            }

            $indentMatch = [regex]::Match($line, '^(\s*)')
            $indent = $indentMatch.Groups[1].Value.Length

            if ($parentIndent -ge 0 -and $indent -le $parentIndent) {
                break
            }

            if ($line -match $pattern) {
                $matchedIndent = $Matches[1].Length
                if (($parentIndent -lt 0 -and $matchedIndent -eq 0) -or ($parentIndent -ge 0 -and $matchedIndent -gt $parentIndent)) {
                    $parentIndent = $matchedIndent
                    $startIndex = $i + 1
                    $found = $true
                    break
                }
            }
        }

        if (-not $found) {
            return $false
        }
    }

    return $true
}

$requiredContracts = @(
    (Join-Path $openApiDir 'openconnect.v1.yaml'),
    (Join-Path $openApiDir 'openconnect-cloud.v1.yaml')
)
foreach ($required in $requiredContracts) {
    if (-not (Test-Path $required)) {
        throw "Missing OpenAPI contract: $required"
    }
}

$openApiFiles = Get-ChildItem $openApiDir -File -Filter '*.yaml'
$refCount = 0

foreach ($openApiFile in $openApiFiles) {
    $yaml = Get-Content $openApiFile.FullName -Raw
    if ($UseParsedReferences) {
        $validator = Join-Path $PSScriptRoot 'validate-openapi-references.cjs'
        & node $validator $openApiFile.FullName (Join-Path $root 'contracts')
        if ($LASTEXITCODE -ne 0) {
            throw "OpenAPI reference validation failed in ${openApiFile}."
        }
        $refCount += [regex]::Matches($yaml, '\$ref:\s*([^\s},]+)').Count
        continue
    }
    $lines = $yaml -split "`r?`n"

    foreach ($match in [regex]::Matches($yaml, '\$ref:\s*([^\s},]+)')) {
        $ref = $match.Groups[1].Value.Trim("'").Trim('"')
        $refCount++

        if ($ref.StartsWith('#/')) {
            if (-not (Test-OpenApiInternalRef -Lines $lines -Pointer $ref)) {
                throw "OpenAPI internal reference does not resolve in $($openApiFile.Name): $ref"
            }
            continue
        }

        $filePart = ($ref -split '#', 2)[0]
        $target = Join-Path $openApiFile.DirectoryName $filePart
        if (-not (Test-Path $target)) {
            throw "OpenAPI reference does not resolve in $($openApiFile.Name): $ref"
        }
    }
}

$schemas = Get-ChildItem (Join-Path $root 'contracts/jsonschema') -File -Filter '*.json'
foreach ($schema in $schemas) {
    try {
        Get-Content $schema.FullName -Raw | ConvertFrom-Json | Out-Null
    }
    catch {
        throw "Invalid JSON Schema: $($schema.FullName): $($_.Exception.Message)"
    }
}

Write-Host "PASS: validated $($openApiFiles.Count) OpenAPI contracts ($refCount references) and $($schemas.Count) JSON Schema files."
