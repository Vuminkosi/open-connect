$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'
$contentRoot = (Resolve-Path (Join-Path $root 'site/OpenConnect.Docs/Content')).Path
$routes = @{}
$files = Get-ChildItem $contentRoot -Recurse -File -Filter '*.md'
if (-not $files) { throw 'No portal Markdown files found.' }
foreach ($file in $files) {
    $relative = $file.FullName.Substring($contentRoot.Length + 1).Replace('\','/')
    $parts = $relative.Split('/')
    if ($parts.Count -ne 2) { throw "Content must be Content/<version>/<slug>.md: $relative" }
    $lines = (Get-Content $file.FullName -Raw) -split "`r?`n"
    if ($lines.Count -lt 4 -or $lines[0] -ne '---') { throw "Missing front matter: $relative" }
    $end = [Array]::IndexOf($lines, '---', 1)
    if ($end -lt 1) { throw "Unclosed front matter: $relative" }
    $yaml = $lines[1..($end - 1)] -join "`n"
    if ($yaml -notmatch '(?m)^title:\s*\S') { throw "Missing title: $relative" }
    if ($yaml -notmatch '(?m)^version:\s*\S') { throw "Missing version: $relative" }
    $key = "$($parts[0])/$([IO.Path]::GetFileNameWithoutExtension($parts[1]))"
    if ($routes.ContainsKey($key)) { throw "Duplicate documentation route $key in $relative and $($routes[$key])" }
    $routes[$key] = $relative
    $source = $lines -join "`n"
    if ($source -match '<script\b|javascript:') { throw "Unsafe markup or javascript URL found: $relative" }
}
Write-Host "PASS: validated $($files.Count) documentation files and $($routes.Count) unique routes."
